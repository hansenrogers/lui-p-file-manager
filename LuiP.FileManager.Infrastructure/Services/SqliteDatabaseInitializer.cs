using Microsoft.Data.Sqlite;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class SqliteDatabaseInitializer : IDatabaseInitializer
{
    private readonly IAppPathsProvider _pathsProvider;

    public SqliteDatabaseInitializer(IAppPathsProvider pathsProvider)
    {
        _pathsProvider = pathsProvider;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var appRoot = _pathsProvider.GetAppDataRoot();
        var dbPath = _pathsProvider.GetDatabasePath();
        var dbDirectory = Path.GetDirectoryName(dbPath);

        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(_pathsProvider.GetRecoveryRoot());
        Directory.CreateDirectory(_pathsProvider.GetDiagnosticsLogRoot());

        if (!string.IsNullOrWhiteSpace(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var schema = await LoadSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await EnsureColumnAsync(connection, "operations", "source_paths_json", "TEXT NULL", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({tableName});";

        var exists = false;
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> LoadSchemaAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(SqliteDatabaseInitializer).Assembly;
        const string resourceName = "LuiP.FileManager.Infrastructure.Persistence.SqliteSchema.sql";

        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded schema resource '{resourceName}' was not found.");

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
