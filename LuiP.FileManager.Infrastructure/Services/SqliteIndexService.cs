using Microsoft.Data.Sqlite;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class SqliteIndexService : IIndexService
{
    private readonly IAppPathsProvider _pathsProvider;
    private readonly ILocationRepository _locationRepository;

    public SqliteIndexService(IAppPathsProvider pathsProvider, ILocationRepository locationRepository)
    {
        _pathsProvider = pathsProvider;
        _locationRepository = locationRepository;
    }

    public async Task RebuildAllAsync(CancellationToken cancellationToken)
    {
        var sources = await _locationRepository.GetByKindAsync(LocationKind.Source, cancellationToken).ConfigureAwait(false);
        var destinations = await _locationRepository.GetByKindAsync(LocationKind.Destination, cancellationToken).ConfigureAwait(false);

        foreach (var location in sources.Concat(destinations).Where(x => x.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RebuildLocationInternalAsync(location, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RebuildLocationAsync(string locationId, CancellationToken cancellationToken)
    {
        var sources = await _locationRepository.GetByKindAsync(LocationKind.Source, cancellationToken).ConfigureAwait(false);
        var destinations = await _locationRepository.GetByKindAsync(LocationKind.Destination, cancellationToken).ConfigureAwait(false);

        var target = sources.Concat(destinations).FirstOrDefault(x => x.Id.Equals(locationId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            throw new InvalidOperationException("Configured location was not found.");
        }

        await RebuildLocationInternalAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexLocationStatus>> GetLocationStatusesAsync(CancellationToken cancellationToken)
    {
        var results = new List<IndexLocationStatus>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    l.location_id,
    l.kind,
    l.name,
    l.path,
    l.status,
    l.last_indexed_utc,
    l.last_error,
    (SELECT COUNT(1) FROM indexed_items i WHERE i.location_id = l.location_id) AS item_count
FROM indexed_locations l
ORDER BY l.kind ASC, l.name ASC;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new IndexLocationStatus
            {
                LocationId = reader.GetString(0),
                Kind = (LocationKind)reader.GetInt32(1),
                Name = reader.GetString(2),
                Path = reader.GetString(3),
                Status = reader.GetString(4),
                LastIndexedUtc = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                LastError = reader.IsDBNull(6) ? null : reader.GetString(6),
                ItemCount = reader.IsDBNull(7) ? 0 : reader.GetInt64(7)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<IndexedSearchResult>> SearchAsync(
        LocationKind kind,
        string locationId,
        string query,
        DateFilterPreset dateFilter,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var safeMax = Math.Clamp(maxResults, 1, 2000);
        var results = new List<IndexedSearchResult>(safeMax);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var lower = (query ?? string.Empty).Trim().ToLowerInvariant();

        var dateCondition = BuildDateFilterCondition(dateFilter, out var dateThresholdUtc);

        command.CommandText = $@"
SELECT
    i.location_id,
    l.kind,
    i.full_path,
    i.name,
    i.is_folder,
    i.size_bytes,
    i.modified_utc
FROM indexed_items i
JOIN indexed_locations l ON l.location_id = i.location_id
WHERE l.kind = $kind
  AND i.location_id = $location_id
  AND (
      $q = ''
      OR lower(i.name) LIKE '%' || $q || '%'
      OR lower(i.full_path) LIKE '%' || $q || '%'
  )
  {dateCondition}
ORDER BY i.is_folder DESC, i.name ASC
LIMIT $limit;";

        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$location_id", locationId);
        command.Parameters.AddWithValue("$q", lower);
        command.Parameters.AddWithValue("$limit", safeMax);
        if (dateThresholdUtc is not null)
        {
            command.Parameters.AddWithValue("$modified_since", dateThresholdUtc.Value.UtcDateTime.ToString("O"));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new IndexedSearchResult
            {
                LocationId = reader.GetString(0),
                Kind = (LocationKind)reader.GetInt32(1),
                FullPath = reader.GetString(2),
                Name = reader.GetString(3),
                IsFolder = reader.GetInt32(4) == 1,
                SizeBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                ModifiedUtc = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))
            });
        }

        return results;
    }

    private async Task RebuildLocationInternalAsync(ConfiguredLocation location, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await UpsertLocationStatusAsync(connection, location, "Indexing", null, cancellationToken).ConfigureAwait(false);

            await using (var clear = connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM indexed_items WHERE location_id = $location_id;";
                clear.Parameters.AddWithValue("$location_id", location.Id);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!Directory.Exists(location.Path))
            {
                await UpsertLocationStatusAsync(connection, location, "Offline", "Path is unavailable.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var buffer = new List<(string FullPath, string ParentPath, string Name, string? Extension, long? SizeBytes, DateTimeOffset? CreatedUtc, DateTimeOffset? ModifiedUtc, bool IsFolder)>();

            foreach (var path in Directory.EnumerateFileSystemEntries(location.Path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isFolder = Directory.Exists(path);
                var info = isFolder ? null : new FileInfo(path);
                var dirInfo = new DirectoryInfo(path);

                DateTimeOffset? createdUtc;
                DateTimeOffset? modifiedUtc;
                if (isFolder)
                {
                    createdUtc = dirInfo.CreationTimeUtc;
                    modifiedUtc = dirInfo.LastWriteTimeUtc;
                }
                else
                {
                    createdUtc = info?.CreationTimeUtc;
                    modifiedUtc = info?.LastWriteTimeUtc;
                }

                buffer.Add((
                    FullPath: path,
                    ParentPath: Path.GetDirectoryName(path) ?? string.Empty,
                    Name: Path.GetFileName(path),
                    Extension: isFolder ? null : Path.GetExtension(path),
                    SizeBytes: isFolder ? null : info?.Length,
                    CreatedUtc: createdUtc,
                    ModifiedUtc: modifiedUtc,
                    IsFolder: isFolder));
            }

            foreach (var item in buffer)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var insert = connection.CreateCommand();
                insert.CommandText = @"
INSERT INTO indexed_items
(
    item_key,
    location_id,
    full_path,
    parent_path,
    name,
    extension,
    size_bytes,
    created_utc,
    modified_utc,
    is_folder,
    last_indexed_utc
)
VALUES
(
    $item_key,
    $location_id,
    $full_path,
    $parent_path,
    $name,
    $extension,
    $size_bytes,
    $created_utc,
    $modified_utc,
    $is_folder,
    $last_indexed_utc
);";

                insert.Parameters.AddWithValue("$item_key", BuildItemKey(location.Id, item.FullPath));
                insert.Parameters.AddWithValue("$location_id", location.Id);
                insert.Parameters.AddWithValue("$full_path", item.FullPath);
                insert.Parameters.AddWithValue("$parent_path", item.ParentPath);
                insert.Parameters.AddWithValue("$name", item.Name);
                insert.Parameters.AddWithValue("$extension", (object?)item.Extension ?? DBNull.Value);
                insert.Parameters.AddWithValue("$size_bytes", (object?)item.SizeBytes ?? DBNull.Value);
                insert.Parameters.AddWithValue("$created_utc", (object?)item.CreatedUtc?.UtcDateTime.ToString("O") ?? DBNull.Value);
                insert.Parameters.AddWithValue("$modified_utc", (object?)item.ModifiedUtc?.UtcDateTime.ToString("O") ?? DBNull.Value);
                insert.Parameters.AddWithValue("$is_folder", item.IsFolder ? 1 : 0);
                insert.Parameters.AddWithValue("$last_indexed_utc", now.UtcDateTime.ToString("O"));

                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await UpsertLocationStatusAsync(connection, location, "Ready", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UpsertLocationStatusAsync(connection, location, "Error", ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static string BuildItemKey(string locationId, string fullPath)
    {
        return $"{locationId}|{fullPath}".ToLowerInvariant();
    }

    private static string BuildDateFilterCondition(DateFilterPreset filter, out DateTimeOffset? threshold)
    {
        threshold = null;
        var now = DateTimeOffset.UtcNow;

        switch (filter)
        {
            case DateFilterPreset.Today:
                threshold = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
                return "AND i.modified_utc >= $modified_since";
            case DateFilterPreset.Last7Days:
                threshold = now.AddDays(-7);
                return "AND i.modified_utc >= $modified_since";
            case DateFilterPreset.Last30Days:
                threshold = now.AddDays(-30);
                return "AND i.modified_utc >= $modified_since";
            case DateFilterPreset.ThisYear:
                threshold = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                return "AND i.modified_utc >= $modified_since";
            default:
                return string.Empty;
        }
    }

    private async Task UpsertLocationStatusAsync(
        SqliteConnection connection,
        ConfiguredLocation location,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO indexed_locations
(
    location_id,
    kind,
    name,
    path,
    status,
    last_indexed_utc,
    last_error
)
VALUES
(
    $location_id,
    $kind,
    $name,
    $path,
    $status,
    $last_indexed_utc,
    $last_error
)
ON CONFLICT(location_id) DO UPDATE SET
    kind = excluded.kind,
    name = excluded.name,
    path = excluded.path,
    status = excluded.status,
    last_indexed_utc = excluded.last_indexed_utc,
    last_error = excluded.last_error;";

        command.Parameters.AddWithValue("$location_id", location.Id);
        command.Parameters.AddWithValue("$kind", (int)location.Kind);
        command.Parameters.AddWithValue("$name", location.Name);
        command.Parameters.AddWithValue("$path", location.Path);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$last_indexed_utc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$last_error", (object?)error ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _pathsProvider.GetDatabasePath(),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
