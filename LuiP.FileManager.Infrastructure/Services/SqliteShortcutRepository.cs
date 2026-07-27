using Microsoft.Data.Sqlite;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class SqliteShortcutRepository : IShortcutRepository
{
    private readonly IAppPathsProvider _pathsProvider;

    public SqliteShortcutRepository(IAppPathsProvider pathsProvider)
    {
        _pathsProvider = pathsProvider;
    }

    public async Task<IReadOnlyList<ShortcutItem>> GetAllAsync(CancellationToken cancellationToken)
    {
        var results = new List<ShortcutItem>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, label, target_path, target_kind, sort_order
FROM shortcuts
ORDER BY sort_order ASC, label ASC;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ShortcutItem
            {
                Id = reader.GetString(0),
                Label = reader.GetString(1),
                TargetPath = reader.GetString(2),
                TargetKind = (ShortcutTargetKind)reader.GetInt32(3),
                SortOrder = reader.GetInt32(4)
            });
        }

        return results;
    }

    public async Task UpsertAsync(ShortcutItem item, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO shortcuts
(
    id,
    label,
    target_path,
    target_kind,
    sort_order
)
VALUES
(
    $id,
    $label,
    $target_path,
    $target_kind,
    $sort_order
)
ON CONFLICT(id) DO UPDATE SET
    label = excluded.label,
    target_path = excluded.target_path,
    target_kind = excluded.target_kind,
    sort_order = excluded.sort_order;";

        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$label", item.Label);
        command.Parameters.AddWithValue("$target_path", item.TargetPath);
        command.Parameters.AddWithValue("$target_kind", (int)item.TargetKind);
        command.Parameters.AddWithValue("$sort_order", item.SortOrder);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM shortcuts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
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
