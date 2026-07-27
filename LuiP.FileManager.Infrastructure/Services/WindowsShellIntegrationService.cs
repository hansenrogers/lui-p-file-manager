using Microsoft.Win32;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using System.Runtime.Versioning;

namespace LuiP.FileManager.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsShellIntegrationService : IShellIntegrationService
{
    private const string Root = "Software\\Classes";
    private const string ShellKey = "AllFilesystemObjects\\shell\\LuiPFileManagerCopy";
    private const string ShellSubcommandsKey = "shell";
    private const string CommandSubKey = "command";

    public bool IsInstalled()
    {
        using var baseKey = Registry.CurrentUser.OpenSubKey($"{Root}\\{ShellKey}", writable: false);
        if (baseKey is null)
        {
            return false;
        }

        using var chooseCommand = Registry.CurrentUser.OpenSubKey($"{Root}\\{ShellKey}\\{ShellSubcommandsKey}\\choose\\{CommandSubKey}", writable: false);
        var chooseValue = chooseCommand?.GetValue(string.Empty) as string;
        if (!string.IsNullOrWhiteSpace(chooseValue))
        {
            return true;
        }

        using var legacyCommand = Registry.CurrentUser.OpenSubKey($"{Root}\\{ShellKey}\\{CommandSubKey}", writable: false);
        var legacyValue = legacyCommand?.GetValue(string.Empty) as string;
        return !string.IsNullOrWhiteSpace(legacyValue);
    }

    public void InstallPerUser(string executablePath, IReadOnlyList<ConfiguredLocation> destinations)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executablePath));
        }

        destinations ??= Array.Empty<ConfiguredLocation>();

        using var shell = Registry.CurrentUser.CreateSubKey($"{Root}\\{ShellKey}", writable: true)
            ?? throw new InvalidOperationException("Unable to create shell integration key.");

        shell.SetValue(string.Empty, "Copy with Lui-P File Manager", RegistryValueKind.String);
        shell.SetValue("Icon", executablePath, RegistryValueKind.String);
        shell.SetValue("MUIVerb", "Copy with Lui-P File Manager", RegistryValueKind.String);
        shell.SetValue("SubCommands", string.Empty, RegistryValueKind.String);

        Registry.CurrentUser.DeleteSubKeyTree($"{Root}\\{ShellKey}\\{CommandSubKey}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($"{Root}\\{ShellKey}\\{ShellSubcommandsKey}", throwOnMissingSubKey: false);

        using var subcommands = shell.CreateSubKey(ShellSubcommandsKey, writable: true)
            ?? throw new InvalidOperationException("Unable to create shell integration submenu key.");

        using (var chooseCommand = subcommands.CreateSubKey("choose", writable: true)
            ?? throw new InvalidOperationException("Unable to create shell chooser command key."))
        {
            chooseCommand.SetValue(string.Empty, "Choose Destination...", RegistryValueKind.String);
            chooseCommand.SetValue("Icon", executablePath, RegistryValueKind.String);
            using var chooseCommandExec = chooseCommand.CreateSubKey(CommandSubKey, writable: true)
                ?? throw new InvalidOperationException("Unable to create shell chooser execution key.");
            chooseCommandExec.SetValue(string.Empty, $"\"{executablePath}\" --shell-copy %*", RegistryValueKind.String);
        }

        var orderedDestinations = destinations
            .Where(d => d.Kind == LocationKind.Destination && d.IsEnabled)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < orderedDestinations.Count; i++)
        {
            var destination = orderedDestinations[i];
            var commandKeyName = $"dest_{i + 1}_{SanitizeKeySegment(destination.Id)}";
            using var destinationKey = subcommands.CreateSubKey(commandKeyName, writable: true)
                ?? throw new InvalidOperationException("Unable to create shell destination command key.");

            destinationKey.SetValue(string.Empty, $"Copy to {destination.Name}", RegistryValueKind.String);
            destinationKey.SetValue("Icon", executablePath, RegistryValueKind.String);

            using var command = destinationKey.CreateSubKey(CommandSubKey, writable: true)
                ?? throw new InvalidOperationException("Unable to create shell destination execution key.");

            command.SetValue(string.Empty, $"\"{executablePath}\" --shell-copy-destination \"{destination.Id}\" %*", RegistryValueKind.String);
        }
    }

    public void UninstallPerUser()
    {
        Registry.CurrentUser.DeleteSubKeyTree($"{Root}\\{ShellKey}", throwOnMissingSubKey: false);
    }

    private static string SanitizeKeySegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "destination";
        }

        var chars = value
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '_')
            .ToArray();

        if (chars.Length == 0)
        {
            return "destination";
        }

        return new string(chars);
    }
}
