using System.Windows;
using System.Diagnostics;
using System.IO;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using LuiP.FileManager.App.Persistence;

namespace LuiP.FileManager.App;

public partial class SettingsWindow : Window
{
    private readonly ILocationRepository _locationRepository;
    private readonly IIndexService _indexService;
    private readonly IShellIntegrationService _shellIntegrationService;
    private readonly Action _onLocationsUpdated;
    private readonly Action? _onPreferencesUpdated;
    private readonly AppPreferencesStore _preferencesStore;

    public SettingsWindow(
        ILocationRepository locationRepository,
        IIndexService indexService,
        IShellIntegrationService shellIntegrationService,
        IAppPathsProvider appPathsProvider,
        Action onLocationsUpdated,
        Action? onPreferencesUpdated = null)
    {
        _locationRepository = locationRepository;
        _indexService = indexService;
        _shellIntegrationService = shellIntegrationService;
        _onLocationsUpdated = onLocationsUpdated;
        _onPreferencesUpdated = onPreferencesUpdated;
        _preferencesStore = new AppPreferencesStore(appPathsProvider);
        InitializeComponent();
        RefreshShellStatus();
        LoadChecksumPreferences();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void EditSourcesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var editor = new LocationEditorWindow(_locationRepository, LocationKind.Source)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true)
        {
            _onLocationsUpdated();
        }
    }

    private void EditDestinationsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var editor = new LocationEditorWindow(_locationRepository, LocationKind.Destination)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true)
        {
            _onLocationsUpdated();

            if (_shellIntegrationService.IsInstalled())
            {
                try
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrWhiteSpace(exePath))
                    {
                        var destinations = _locationRepository
                            .GetByKindAsync(LocationKind.Destination, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();

                        _shellIntegrationService.InstallPerUser(exePath, destinations);
                    }
                }
                catch
                {
                    // Keep settings editable even if shell refresh fails.
                }
            }
        }
    }

    private void IndexStatusButton_OnClick(object sender, RoutedEventArgs e)
    {
        var indexWindow = new IndexStatusWindow(_indexService)
        {
            Owner = this
        };
        indexWindow.ShowDialog();
    }

    private void InstallShellButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("Unable to determine application executable path.");
            }

            var destinations = _locationRepository
                .GetByKindAsync(LocationKind.Destination, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            _shellIntegrationService.InstallPerUser(exePath, destinations);
            RefreshShellStatus();
            MessageBox.Show(this, "Explorer integration installed for the current user.", "Shell Integration", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Shell Integration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UninstallShellButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _shellIntegrationService.UninstallPerUser();
            RefreshShellStatus();
            MessageBox.Show(this, "Explorer integration removed for the current user.", "Shell Integration", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Shell Integration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReinstallShellButton_OnClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "This will remove and re-add Explorer integration using your current destination list. Continue?",
            "Shell Integration",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("Unable to determine application executable path.");
            }

            var destinations = _locationRepository
                .GetByKindAsync(LocationKind.Destination, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            _shellIntegrationService.UninstallPerUser();
            _shellIntegrationService.InstallPerUser(exePath, destinations);
            RefreshShellStatus();

            MessageBox.Show(this, "Explorer integration was reinstalled successfully.", "Shell Integration", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Shell Integration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshShellStatus()
    {
        ShellStatusTextBlock.Text = _shellIntegrationService.IsInstalled()
            ? "Status: Installed (per user)"
            : "Status: Not installed";
    }

    private void LoadChecksumPreferences()
    {
        var prefs = _preferencesStore.Load();
        ChecksumDefaultCheckBox.IsChecked = prefs.VerifyLargeFileChecksumByDefault;
        ConfirmMoveCheckBox.IsChecked = prefs.ConfirmBeforeMoveToDestination;
        SyncBoardAutoSyncCheckBox.IsChecked = prefs.SyncBoardAutoSyncEnabled;
        SyncBoardMirrorDeletesCheckBox.IsChecked = prefs.SyncBoardMirrorDeletesEnabled;
        SelectIndexingAggressiveness(prefs.IndexingAggressiveness);
        var thresholdMb = Math.Max(1L, prefs.ChecksumThresholdBytes / (1024L * 1024L));
        ChecksumThresholdTextBox.Text = thresholdMb.ToString();
    }

    private void SaveChecksumSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(ChecksumThresholdTextBox.Text?.Trim(), out var thresholdMb) || thresholdMb <= 0)
        {
            MessageBox.Show(this, "Threshold must be a positive whole number of MB.", "Copy Verification", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prefs = new AppPreferences
        {
            VerifyLargeFileChecksumByDefault = ChecksumDefaultCheckBox.IsChecked == true,
            ConfirmBeforeMoveToDestination = ConfirmMoveCheckBox.IsChecked != false,
            SyncBoardAutoSyncEnabled = SyncBoardAutoSyncCheckBox.IsChecked != false,
            SyncBoardMirrorDeletesEnabled = SyncBoardMirrorDeletesCheckBox.IsChecked != false,
            IndexingAggressiveness = GetSelectedIndexingAggressiveness(),
            ChecksumThresholdBytes = thresholdMb * 1024L * 1024L
        };

        _preferencesStore.Save(prefs);
        _onPreferencesUpdated?.Invoke();
        MessageBox.Show(this, "Copy/move preferences saved.", "Copy Verification", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SelectIndexingAggressiveness(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Equals(AppPreferences.IndexingProfileFast, StringComparison.OrdinalIgnoreCase))
        {
            IndexingAggressivenessComboBox.SelectedIndex = 0;
            return;
        }

        if (normalized.Equals(AppPreferences.IndexingProfileLow, StringComparison.OrdinalIgnoreCase))
        {
            IndexingAggressivenessComboBox.SelectedIndex = 2;
            return;
        }

        IndexingAggressivenessComboBox.SelectedIndex = 1;
    }

    private string GetSelectedIndexingAggressiveness()
    {
        return IndexingAggressivenessComboBox.SelectedIndex switch
        {
            0 => AppPreferences.IndexingProfileFast,
            2 => AppPreferences.IndexingProfileLow,
            _ => AppPreferences.IndexingProfileNormal
        };
    }

    private void RunLocalUninstallButton_OnClick(object sender, RoutedEventArgs e)
    {
        var scriptPath = ResolveLocalUninstallScriptPath();
        if (scriptPath is null)
        {
            MessageBox.Show(this, "Could not find scripts\\uninstall-local.ps1 from this runtime location.", "Local Uninstall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "This will launch the local uninstall script and then close the app. Continue?",
            "Local Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var removeAppDataResult = MessageBox.Show(
            this,
            "Also remove local app data under %LOCALAPPDATA%\\LuiPFileManager?",
            "Local Uninstall Options",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (removeAppDataResult == MessageBoxResult.Cancel)
        {
            return;
        }

        var keepShellResult = MessageBox.Show(
            this,
            "Keep current Explorer shell integration registry entry?",
            "Local Uninstall Options",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (keepShellResult == MessageBoxResult.Cancel)
        {
            return;
        }

        try
        {
            var args = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -ForceCloseRunningApp";
            if (removeAppDataResult == MessageBoxResult.Yes)
            {
                args += " -RemoveAppData";
            }

            if (keepShellResult == MessageBoxResult.Yes)
            {
                args += " -KeepShellIntegration";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(psi);
            MessageBox.Show(this, "Uninstall script started. The application will now close.", "Local Uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Local Uninstall", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunLocalInstallButton_OnClick(object sender, RoutedEventArgs e)
    {
        var scriptPath = ResolveLocalInstallScriptPath();
        if (scriptPath is null)
        {
            MessageBox.Show(this, "Could not find scripts\\install-local.ps1 from this runtime location.", "Local Install", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var createDesktopResult = MessageBox.Show(
            this,
            "Create a Desktop shortcut as part of install?",
            "Local Install Options",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (createDesktopResult == MessageBoxResult.Cancel)
        {
            return;
        }

        var forceCloseResult = MessageBox.Show(
            this,
            "Force-close a running Lui-P File Manager instance before install?",
            "Local Install Options",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (forceCloseResult == MessageBoxResult.Cancel)
        {
            return;
        }

        try
        {
            var args = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
            if (createDesktopResult == MessageBoxResult.Yes)
            {
                args += " -CreateDesktopShortcut";
            }

            if (forceCloseResult == MessageBoxResult.Yes)
            {
                args += " -ForceCloseRunningApp";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(psi);
            MessageBox.Show(this, "Install script started.", "Local Install", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Local Install", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? ResolveLocalUninstallScriptPath()
    {
        return ResolveScriptPath("uninstall-local.ps1");
    }

    private static string? ResolveLocalInstallScriptPath()
    {
        return ResolveScriptPath("install-local.ps1");
    }

    private static string? ResolveScriptPath(string scriptFileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", scriptFileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", scriptFileName)),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "scripts", scriptFileName))
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
