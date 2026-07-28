using System.IO;
using System.Text.Json;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.App.Persistence;

public sealed class AppPreferencesStore
{
    private const long DefaultThresholdBytes = 32L * 1024L * 1024L;
    private readonly string _preferencesFilePath;

    public AppPreferencesStore(IAppPathsProvider pathsProvider)
    {
        var appDataRoot = pathsProvider.GetAppDataRoot();
        _preferencesFilePath = Path.Combine(appDataRoot, "config", "app-preferences.json");
    }

    public AppPreferences Load()
    {
        if (!File.Exists(_preferencesFilePath))
        {
            return AppPreferences.Default();
        }

        try
        {
            var json = File.ReadAllText(_preferencesFilePath);
            var loaded = JsonSerializer.Deserialize<AppPreferences>(json);
            if (loaded is null)
            {
                return AppPreferences.Default();
            }

            return loaded with
            {
                ChecksumThresholdBytes = loaded.ChecksumThresholdBytes > 0 ? loaded.ChecksumThresholdBytes : DefaultThresholdBytes
            };
        }
        catch
        {
            return AppPreferences.Default();
        }
    }

    public void Save(AppPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_preferencesFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = preferences with
        {
            ChecksumThresholdBytes = preferences.ChecksumThresholdBytes > 0 ? preferences.ChecksumThresholdBytes : DefaultThresholdBytes
        };

        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_preferencesFilePath, json);
    }
}

public sealed record AppPreferences
{
    public bool VerifyLargeFileChecksumByDefault { get; init; }

    public bool ConfirmBeforeMoveToDestination { get; init; } = true;

    public bool SyncBoardAutoSyncEnabled { get; init; } = true;

    public long ChecksumThresholdBytes { get; init; } = 32L * 1024L * 1024L;

    public static AppPreferences Default()
    {
        return new AppPreferences
        {
            VerifyLargeFileChecksumByDefault = false,
            ConfirmBeforeMoveToDestination = true,
            SyncBoardAutoSyncEnabled = true,
            ChecksumThresholdBytes = 32L * 1024L * 1024L
        };
    }
}
