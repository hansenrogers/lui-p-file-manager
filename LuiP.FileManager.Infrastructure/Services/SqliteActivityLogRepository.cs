using Microsoft.Data.Sqlite;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class SqliteActivityLogRepository : IActivityLogRepository
{
    private readonly IAppPathsProvider _pathsProvider;

    public SqliteActivityLogRepository(IAppPathsProvider pathsProvider)
    {
        _pathsProvider = pathsProvider;
    }

    public async Task UpsertAsync(FileOperationRecord operation, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO operations
(
    operation_id,
    started_utc,
    completed_utc,
    operation_type,
    status,
    item_count,
    total_bytes,
    source_path,
    source_paths_json,
    destination_path,
    undo_available,
    undo_expiry_utc,
    error_message
)
VALUES
(
    $operation_id,
    $started_utc,
    $completed_utc,
    $operation_type,
    $status,
    $item_count,
    $total_bytes,
    $source_path,
    $source_paths_json,
    $destination_path,
    $undo_available,
    $undo_expiry_utc,
    $error_message
)
ON CONFLICT(operation_id) DO UPDATE SET
    completed_utc = excluded.completed_utc,
    status = excluded.status,
    item_count = excluded.item_count,
    total_bytes = excluded.total_bytes,
    source_path = excluded.source_path,
    source_paths_json = excluded.source_paths_json,
    destination_path = excluded.destination_path,
    undo_available = excluded.undo_available,
    undo_expiry_utc = excluded.undo_expiry_utc,
    error_message = excluded.error_message;";

        BindRecord(command, operation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileOperationRecord?> GetByOperationIdAsync(string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    operation_id,
    started_utc,
    completed_utc,
    operation_type,
    status,
    item_count,
    total_bytes,
    source_path,
    source_paths_json,
    destination_path,
    undo_available,
    undo_expiry_utc,
    error_message
FROM operations
WHERE operation_id = $operation_id
LIMIT 1;";
        command.Parameters.AddWithValue("$operation_id", operationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    public async Task<IReadOnlyList<FileOperationRecord>> GetRecentAsync(int take, CancellationToken cancellationToken)
    {
        var safeTake = Math.Max(1, Math.Min(take, 1000));
        var results = new List<FileOperationRecord>(safeTake);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    operation_id,
    started_utc,
    completed_utc,
    operation_type,
    status,
    item_count,
    total_bytes,
    source_path,
    source_paths_json,
    destination_path,
    undo_available,
    undo_expiry_utc,
    error_message
FROM operations
ORDER BY started_utc DESC
LIMIT $take;";
        command.Parameters.AddWithValue("$take", safeTake);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<FileOperationRecord>> GetInterruptedAsync(CancellationToken cancellationToken)
    {
        var results = new List<FileOperationRecord>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    operation_id,
    started_utc,
    completed_utc,
    operation_type,
    status,
    item_count,
    total_bytes,
    source_path,
    source_paths_json,
    destination_path,
    undo_available,
    undo_expiry_utc,
    error_message
FROM operations
WHERE status IN ($planned, $running)
ORDER BY started_utc DESC
LIMIT 200;";
        command.Parameters.AddWithValue("$planned", (int)FileOperationStatus.Planned);
        command.Parameters.AddWithValue("$running", (int)FileOperationStatus.Running);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    public async Task AddOperationItemAsync(OperationItemRecord item, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO operation_items
(
    item_id,
    operation_id,
    kind,
    source_path,
    destination_path,
    recovery_path,
    destination_size_bytes,
    destination_last_write_utc
)
VALUES
(
    $item_id,
    $operation_id,
    $kind,
    $source_path,
    $destination_path,
    $recovery_path,
    $destination_size_bytes,
    $destination_last_write_utc
);";

        command.Parameters.AddWithValue("$item_id", item.ItemId);
        command.Parameters.AddWithValue("$operation_id", item.OperationId);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$source_path", item.SourcePath);
        command.Parameters.AddWithValue("$destination_path", (object?)item.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$recovery_path", (object?)item.RecoveryPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$destination_size_bytes", (object?)item.DestinationSizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$destination_last_write_utc", (object?)item.DestinationLastWriteUtc?.UtcDateTime.ToString("O") ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OperationItemRecord>> GetOperationItemsAsync(string operationId, CancellationToken cancellationToken)
    {
        var results = new List<OperationItemRecord>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    item_id,
    operation_id,
    kind,
    source_path,
    destination_path,
    recovery_path,
    destination_size_bytes,
    destination_last_write_utc
FROM operation_items
WHERE operation_id = $operation_id
ORDER BY item_id ASC;";
        command.Parameters.AddWithValue("$operation_id", operationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new OperationItemRecord
            {
                ItemId = reader.GetString(0),
                OperationId = reader.GetString(1),
                Kind = (OperationItemKind)reader.GetInt32(2),
                SourcePath = reader.GetString(3),
                DestinationPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                RecoveryPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                DestinationSizeBytes = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                DestinationLastWriteUtc = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))
            });
        }

        return results;
    }

    public async Task MarkUndoConsumedAsync(string operationId, string note, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE operations
SET
    undo_available = 0,
    error_message = CASE
        WHEN error_message IS NULL OR error_message = '' THEN $note
        ELSE error_message || ' | ' || $note
    END
WHERE operation_id = $operation_id;";
        command.Parameters.AddWithValue("$operation_id", operationId);
        command.Parameters.AddWithValue("$note", note);
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

    private static void BindRecord(SqliteCommand command, FileOperationRecord operation)
    {
        command.Parameters.AddWithValue("$operation_id", operation.OperationId);
        command.Parameters.AddWithValue("$started_utc", operation.StartedUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$completed_utc", (object?)operation.CompletedUtc?.UtcDateTime.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation_type", (int)operation.OperationType);
        command.Parameters.AddWithValue("$status", (int)operation.Status);
        command.Parameters.AddWithValue("$item_count", operation.ItemCount);
        command.Parameters.AddWithValue("$total_bytes", operation.TotalBytes);
        command.Parameters.AddWithValue("$source_path", (object?)operation.SourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_paths_json", (object?)operation.SourcePathsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$destination_path", (object?)operation.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$undo_available", operation.UndoAvailable ? 1 : 0);
        command.Parameters.AddWithValue("$undo_expiry_utc", (object?)operation.UndoExpiryUtc?.UtcDateTime.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_message", (object?)operation.ErrorMessage ?? DBNull.Value);
    }

    private static FileOperationRecord ReadRecord(SqliteDataReader reader)
    {
        return new FileOperationRecord
        {
            OperationId = reader.GetString(0),
            StartedUtc = DateTimeOffset.Parse(reader.GetString(1)),
            CompletedUtc = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
            OperationType = (FileOperationType)reader.GetInt32(3),
            Status = (FileOperationStatus)reader.GetInt32(4),
            ItemCount = reader.GetInt32(5),
            TotalBytes = reader.GetInt64(6),
            SourcePath = reader.IsDBNull(7) ? null : reader.GetString(7),
            SourcePathsJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            DestinationPath = reader.IsDBNull(9) ? null : reader.GetString(9),
            UndoAvailable = reader.GetInt32(10) == 1,
            UndoExpiryUtc = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
            ErrorMessage = reader.IsDBNull(12) ? null : reader.GetString(12)
        };
    }
}
