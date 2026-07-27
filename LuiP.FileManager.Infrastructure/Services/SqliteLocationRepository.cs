using Microsoft.Data.Sqlite;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class SqliteLocationRepository : ILocationRepository
{
    private readonly IAppPathsProvider _pathsProvider;

    public SqliteLocationRepository(IAppPathsProvider pathsProvider)
    {
        _pathsProvider = pathsProvider;
    }

    public async Task<IReadOnlyList<ConfiguredLocation>> GetByKindAsync(LocationKind kind, CancellationToken cancellationToken)
    {
        var results = new List<ConfiguredLocation>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, name, path, kind, is_enabled, sort_order
FROM configured_locations
WHERE kind = $kind
ORDER BY sort_order ASC, name ASC;";
        command.Parameters.AddWithValue("$kind", (int)kind);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ConfiguredLocation
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
                Kind = (LocationKind)reader.GetInt32(3),
                IsEnabled = reader.GetInt32(4) == 1,
                SortOrder = reader.GetInt32(5)
            });
        }

        return results;
    }

    public async Task UpsertAsync(ConfiguredLocation location, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO configured_locations
(
    id,
    name,
    path,
    kind,
    is_enabled,
    sort_order
)
VALUES
(
    $id,
    $name,
    $path,
    $kind,
    $is_enabled,
    $sort_order
)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    path = excluded.path,
    kind = excluded.kind,
    is_enabled = excluded.is_enabled,
    sort_order = excluded.sort_order;";

        command.Parameters.AddWithValue("$id", location.Id);
        command.Parameters.AddWithValue("$name", location.Name);
        command.Parameters.AddWithValue("$path", location.Path);
        command.Parameters.AddWithValue("$kind", (int)location.Kind);
        command.Parameters.AddWithValue("$is_enabled", location.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$sort_order", location.SortOrder);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, LocationKind kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
DELETE FROM configured_locations
WHERE id = $id AND kind = $kind;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", (int)kind);
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
