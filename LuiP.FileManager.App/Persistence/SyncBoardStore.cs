using System.IO;
using System.Text.Json;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.App.Persistence;

public sealed class SyncBoardStore
{
    private readonly string _stateFilePath;

    public SyncBoardStore(IAppPathsProvider pathsProvider)
    {
        var appDataRoot = pathsProvider.GetAppDataRoot();
        _stateFilePath = Path.Combine(appDataRoot, "config", "sync-board-state.json");
    }

    public SyncBoardState Load()
    {
        if (!File.Exists(_stateFilePath))
        {
            return SyncBoardState.Default();
        }

        try
        {
            var json = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<SyncBoardState>(json);
            if (state is null)
            {
                return SyncBoardState.Default();
            }

            return state with
            {
                People = state.People ?? new List<SyncBoardPersonRow>(),
                Jobs = state.Jobs ?? new List<SyncBoardJobColumn>()
            };
        }
        catch
        {
            return SyncBoardState.Default();
        }
    }

    public void Save(SyncBoardState state)
    {
        var directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = state with
        {
            People = state.People ?? new List<SyncBoardPersonRow>(),
            Jobs = state.Jobs ?? new List<SyncBoardJobColumn>()
        };

        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_stateFilePath, json);
    }
}

public sealed record SyncBoardState
{
    public string SourceRootPath { get; init; } = string.Empty;

    public string DestinationRootPath { get; init; } = string.Empty;

    public string ProjectNumber { get; init; } = string.Empty;

    public DateTimeOffset? LastSyncUtc { get; init; }

    public string LastSyncStatus { get; init; } = "Not synced yet.";

    public bool StrictProjectFolderGuardEnabled { get; init; } = true;

    public List<SyncBoardPersonRow> People { get; init; } = new();

    public List<SyncBoardJobColumn> Jobs { get; init; } = new();

    public static SyncBoardState Default()
    {
        return new SyncBoardState
        {
            StrictProjectFolderGuardEnabled = true,
            People = new List<SyncBoardPersonRow>(),
            Jobs = new List<SyncBoardJobColumn>()
        };
    }
}

public sealed record SyncBoardPersonRow
{
    public string PersonName { get; init; } = string.Empty;

    public Dictionary<string, bool> JobChecks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record SyncBoardJobColumn
{
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");

    public string JobName { get; init; } = string.Empty;

    public string ProjectNumber { get; init; } = string.Empty;
}
