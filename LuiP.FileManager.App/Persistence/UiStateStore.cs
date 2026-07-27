using System.IO;
using System.Text.Json;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.App.Persistence;

public sealed class UiStateStore
{
    private readonly string _stateFilePath;

    public UiStateStore(IAppPathsProvider pathsProvider)
    {
        var appDataRoot = pathsProvider.GetAppDataRoot();
        _stateFilePath = Path.Combine(appDataRoot, "config", "ui-state.json");
    }

    public UiState? Load()
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        var json = File.ReadAllText(_stateFilePath);
        return JsonSerializer.Deserialize<UiState>(json);
    }

    public void Save(UiState state)
    {
        var directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_stateFilePath, json);
    }
}

public sealed class UiState
{
    public double Width { get; init; }

    public double Height { get; init; }

    public double Top { get; init; }

    public double Left { get; init; }

    public bool IsMaximized { get; init; }
}
