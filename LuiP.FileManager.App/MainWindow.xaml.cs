using System.IO;
using System.Windows.Input;
using System.Windows;
using System.Windows.Threading;
using System.Collections.Specialized;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using LuiP.FileManager.App.Persistence;
using LuiP.FileManager.Infrastructure.Services;

namespace LuiP.FileManager.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly IActivityLogRepository _activityLogRepository;
    private readonly IFileOperationService _fileOperationService;
    private readonly IUndoService _undoService;
    private readonly ILocationRepository _locationRepository;
    private readonly IIndexService _indexService;
    private readonly IShortcutRepository _shortcutRepository;
    private readonly IShellIntegrationService _shellIntegrationService;
    private readonly IAppPathsProvider _appPathsProvider;
    private readonly UiStateStore _uiStateStore;
    private readonly AppPreferencesStore _preferencesStore;
    private readonly DispatcherTimer _uiStateSaveTimer;

    private readonly List<ConfiguredLocation> _sourceLocations = new();
    private readonly List<ConfiguredLocation> _destinationLocations = new();
    private readonly Dictionary<string, ConfiguredLocation> _sourceLocationByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConfiguredLocation> _destinationLocationByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourcePathByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _destinationPathByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutItem> _shortcutByDisplay = new(StringComparer.Ordinal);

    private DateFilterPreset _sourceDateFilter = DateFilterPreset.AllDates;
    private DateFilterPreset _destinationDateFilter = DateFilterPreset.AllDates;
    private List<string> _pendingCutPaths = new();
    private long _checksumThresholdBytes = 32L * 1024L * 1024L;
    private string? _currentSourcePath;
    private string? _currentDestinationPath;
    private CancellationTokenSource? _indexingCts;
    private bool _isIndexing;

    public MainWindow()
    {
        _appPathsProvider = new WindowsAppPathsProvider();
        _activityLogRepository = new SqliteActivityLogRepository(_appPathsProvider);
        _fileOperationService = new JournalingFileOperationService(_activityLogRepository, _appPathsProvider);
        _undoService = new UndoService(_activityLogRepository);
        _locationRepository = new SqliteLocationRepository(_appPathsProvider);
        _indexService = new SqliteIndexService(_appPathsProvider, _locationRepository);
        _shortcutRepository = new SqliteShortcutRepository(_appPathsProvider);
        _shellIntegrationService = new WindowsShellIntegrationService();
        _uiStateStore = new UiStateStore(_appPathsProvider);
        _preferencesStore = new AppPreferencesStore(_appPathsProvider);
        _uiStateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _uiStateSaveTimer.Tick += UiStateSaveTimer_OnTick;

        InitializeComponent();
        RestoreUiState();
        ApplyAppPreferences();
        InitializeLocationsFromRepository();
        RefreshShortcuts();
        RefreshAllViews();
        _ = EnsureIndexBuiltAsync();

        SizeChanged += MainWindow_OnUiChanged;
        LocationChanged += MainWindow_OnUiChanged;
        StateChanged += MainWindow_OnUiChanged;
        Closed += MainWindow_OnClosed;
    }

    private void RestoreUiState()
    {
        var state = _uiStateStore.Load();
        if (state is null)
        {
            return;
        }

        if (double.IsNaN(state.Left) || double.IsNaN(state.Top) || double.IsNaN(state.Width) || double.IsNaN(state.Height))
        {
            return;
        }

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        var previewWidth = state.Width > 0 ? state.Width : Width;
        var previewHeight = state.Height > 0 ? state.Height : Height;
        var previewRight = state.Left + Math.Max(240, previewWidth * 0.25);
        var previewBottom = state.Top + Math.Max(120, previewHeight * 0.25);

        var looksOffscreen =
            previewRight < screenLeft ||
            previewBottom < screenTop ||
            state.Left > screenRight - 120 ||
            state.Top > screenBottom - 80;

        if (looksOffscreen)
        {
            return;
        }

        if (state.Width > 0 && state.Height > 0)
        {
            Width = state.Width;
            Height = state.Height;
        }

        Left = state.Left;
        Top = state.Top;
        WindowState = state.IsMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void MainWindow_OnUiChanged(object? sender, EventArgs e)
    {
        _uiStateSaveTimer.Stop();
        _uiStateSaveTimer.Start();
    }

    private void UiStateSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _uiStateSaveTimer.Stop();
        SaveUiState();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        SaveUiState();
    }

    private void SaveUiState()
    {
        var state = new UiState
        {
            Width = Width,
            Height = Height,
            Left = Left,
            Top = Top,
            IsMaximized = WindowState == WindowState.Maximized
        };

        _uiStateStore.Save(state);
    }

    private void ApplyAppPreferences()
    {
        var preferences = _preferencesStore.Load();
        VerifyChecksumCheckBox.IsChecked = preferences.VerifyLargeFileChecksumByDefault;
        _checksumThresholdBytes = preferences.ChecksumThresholdBytes > 0
            ? preferences.ChecksumThresholdBytes
            : 32L * 1024L * 1024L;
    }

    private void InitializeLocationsFromRepository()
    {
        EnsureDefaultLocationsIfNeeded();

        var sources = _locationRepository.GetByKindAsync(LocationKind.Source, CancellationToken.None).GetAwaiter().GetResult();
        var destinations = _locationRepository.GetByKindAsync(LocationKind.Destination, CancellationToken.None).GetAwaiter().GetResult();

        _sourceLocations.Clear();
        _sourceLocations.AddRange(sources.Where(x => x.IsEnabled).OrderBy(x => x.SortOrder));
        _sourceLocationByDisplay.Clear();

        _destinationLocations.Clear();
        _destinationLocations.AddRange(destinations.Where(x => x.IsEnabled).OrderBy(x => x.SortOrder));
        _destinationLocationByDisplay.Clear();

        var sourceDisplays = new List<string>(_sourceLocations.Count);
        foreach (var source in _sourceLocations)
        {
            var display = ToLocationDisplay(source);
            _sourceLocationByDisplay[display] = source;
            sourceDisplays.Add(display);
        }

        var destinationDisplays = new List<string>(_destinationLocations.Count);
        foreach (var destination in _destinationLocations)
        {
            var display = ToLocationDisplay(destination);
            _destinationLocationByDisplay[display] = destination;
            destinationDisplays.Add(display);
        }

        SourceLocationListView.ItemsSource = sourceDisplays;
        DestinationLocationListView.ItemsSource = destinationDisplays;

        if (SourceLocationListView.Items.Count > 0)
        {
            SourceLocationListView.SelectedIndex = 0;
        }

        if (DestinationLocationListView.Items.Count > 0)
        {
            DestinationLocationListView.SelectedIndex = 0;
        }
    }

    private void EnsureDefaultLocationsIfNeeded()
    {
        var existingSources = _locationRepository.GetByKindAsync(LocationKind.Source, CancellationToken.None).GetAwaiter().GetResult();
        var existingDestinations = _locationRepository.GetByKindAsync(LocationKind.Destination, CancellationToken.None).GetAwaiter().GetResult();

        if (existingSources.Count > 0 && existingDestinations.Count > 0)
        {
            return;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(profile, "Downloads");

        if (existingSources.Count == 0)
        {
            var defaultSources = new[]
            {
                new ConfiguredLocation { Id = Guid.NewGuid().ToString("N"), Name = "Desktop", Path = desktop, Kind = LocationKind.Source, SortOrder = 1 },
                new ConfiguredLocation { Id = Guid.NewGuid().ToString("N"), Name = "Documents", Path = documents, Kind = LocationKind.Source, SortOrder = 2 },
                new ConfiguredLocation { Id = Guid.NewGuid().ToString("N"), Name = "Downloads", Path = downloads, Kind = LocationKind.Source, SortOrder = 3 }
            };

            foreach (var source in defaultSources)
            {
                _locationRepository.UpsertAsync(source, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        if (existingDestinations.Count == 0)
        {
            var defaultDestinations = new[]
            {
                new ConfiguredLocation { Id = Guid.NewGuid().ToString("N"), Name = "Documents", Path = documents, Kind = LocationKind.Destination, SortOrder = 1 },
                new ConfiguredLocation { Id = Guid.NewGuid().ToString("N"), Name = "Desktop", Path = desktop, Kind = LocationKind.Destination, SortOrder = 2 },
                new ConfiguredLocation { Id = Guid.NewGuid().ToString("N"), Name = "Downloads", Path = downloads, Kind = LocationKind.Destination, SortOrder = 3 }
            };

            foreach (var destination in defaultDestinations)
            {
                _locationRepository.UpsertAsync(destination, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
    }

    private void RefreshAllViews()
    {
        RefreshSourceItems();
        RefreshDestinationItems();
    }

    private void RefreshSourceItems()
    {
        if (SourceItemListView is null || SourceSearchTextBox is null)
        {
            return;
        }

        var selected = GetSelectedSourceLocation();
        if (selected is not { Path: { Length: > 0 } selectedRoot } || !Directory.Exists(selectedRoot))
        {
            SourceItemListView.ItemsSource = Array.Empty<string>();
            if (SourceCurrentPathTextBlock is not null)
            {
                SourceCurrentPathTextBlock.Text = string.Empty;
            }
            if (SourceUpButton is not null)
            {
                SourceUpButton.IsEnabled = false;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentSourcePath) || !Directory.Exists(_currentSourcePath) || !IsPathWithinRoot(_currentSourcePath, selectedRoot))
        {
            _currentSourcePath = selectedRoot;
        }

        UpdateSourceNavigationUi(selectedRoot, _currentSourcePath);

        var query = SourceSearchTextBox.Text?.Trim() ?? string.Empty;
        if (query.Length > 0)
        {
            var searchResults = _indexService
                .SearchAsync(LocationKind.Source, selected.Id, query, _sourceDateFilter, 500, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            _sourcePathByDisplay.Clear();
            var displayed = new List<string>();
            foreach (var result in searchResults)
            {
                var display = FormatSearchDisplay(result.IsFolder, result.Name, result.FullPath);
                _sourcePathByDisplay[display] = result.FullPath;
                displayed.Add(display);
            }

            SourceItemListView.ItemsSource = displayed;
            return;
        }

        var fsEntries = Directory.EnumerateFileSystemEntries(_currentSourcePath).Take(500).ToList();
        _sourcePathByDisplay.Clear();
        var items = new List<string>(fsEntries.Count);
        foreach (var entry in fsEntries)
        {
            var display = FormatItemDisplay(entry);
            _sourcePathByDisplay[display] = entry;
            items.Add(display);
        }

        SourceItemListView.ItemsSource = items;
    }

    private void RefreshDestinationItems()
    {
        if (DestinationItemListView is null || DestinationSearchTextBox is null)
        {
            return;
        }

        var selected = GetSelectedDestinationLocation();
        if (selected is not { Path: { Length: > 0 } selectedRoot } || !Directory.Exists(selectedRoot))
        {
            DestinationItemListView.ItemsSource = Array.Empty<string>();
            if (DestinationCurrentPathTextBlock is not null)
            {
                DestinationCurrentPathTextBlock.Text = string.Empty;
            }
            if (DestinationUpButton is not null)
            {
                DestinationUpButton.IsEnabled = false;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentDestinationPath) || !Directory.Exists(_currentDestinationPath) || !IsPathWithinRoot(_currentDestinationPath, selectedRoot))
        {
            _currentDestinationPath = selectedRoot;
        }

        UpdateDestinationNavigationUi(selectedRoot, _currentDestinationPath);

        var query = DestinationSearchTextBox.Text?.Trim() ?? string.Empty;
        if (query.Length > 0)
        {
            var searchResults = _indexService
                .SearchAsync(LocationKind.Destination, selected.Id, query, _destinationDateFilter, 500, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            _destinationPathByDisplay.Clear();
            var displayed = new List<string>();
            foreach (var result in searchResults)
            {
                var display = FormatSearchDisplay(result.IsFolder, result.Name, result.FullPath);
                _destinationPathByDisplay[display] = result.FullPath;
                displayed.Add(display);
            }

            DestinationItemListView.ItemsSource = displayed;
            return;
        }

        var fsEntries = Directory.EnumerateFileSystemEntries(_currentDestinationPath).Take(500).ToList();
        _destinationPathByDisplay.Clear();
        var items = new List<string>(fsEntries.Count);
        foreach (var entry in fsEntries)
        {
            var display = FormatItemDisplay(entry);
            _destinationPathByDisplay[display] = entry;
            items.Add(display);
        }

        DestinationItemListView.ItemsSource = items;
    }

    private ConfiguredLocation? GetSelectedSourceLocation()
    {
        return GetSelectedLocation(SourceLocationListView, _sourceLocationByDisplay);
    }

    private ConfiguredLocation? GetSelectedDestinationLocation()
    {
        return GetSelectedLocation(DestinationLocationListView, _destinationLocationByDisplay);
    }

    private static ConfiguredLocation? GetSelectedLocation(System.Windows.Controls.ListView listView, IReadOnlyDictionary<string, ConfiguredLocation> map)
    {
        if (listView is null)
        {
            return null;
        }

        if (listView.SelectedItem is not string selectedLine)
        {
            return null;
        }

        return map.TryGetValue(selectedLine, out var configured) ? configured : null;
    }

    private static string ToLocationDisplay(ConfiguredLocation location)
    {
        return $"{location.Name}   {location.Path}";
    }

    private static string FormatItemDisplay(string path)
    {
        var name = Path.GetFileName(path);
        var marker = Directory.Exists(path) ? "📁" : "📄";
        return $"{marker} {name}";
    }

    private static string FormatSearchDisplay(bool isFolder, string name, string fullPath)
    {
        var marker = isFolder ? "📁" : "📄";
        var parent = Path.GetDirectoryName(fullPath) ?? string.Empty;
        return $"{marker} {name} — {parent}";
    }

    private static string ExtractNameFromItemDisplay(string itemDisplay)
    {
        var parts = itemDisplay.Split(' ', 2);
        return parts.Length == 2 ? parts[1] : itemDisplay;
    }

    private void SourceSearchTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshSourceItems();
    }

    private void DestinationSearchTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshDestinationItems();
    }

    private void SourceDateFilterComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _sourceDateFilter = ParseDateFilterPreset(SourceDateFilterComboBox.SelectedIndex);
        RefreshSourceItems();
    }

    private void DestinationDateFilterComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _destinationDateFilter = ParseDateFilterPreset(DestinationDateFilterComboBox.SelectedIndex);
        RefreshDestinationItems();
    }

    private static DateFilterPreset ParseDateFilterPreset(int selectedIndex)
    {
        return selectedIndex switch
        {
            1 => DateFilterPreset.Today,
            2 => DateFilterPreset.Last7Days,
            3 => DateFilterPreset.Last30Days,
            4 => DateFilterPreset.ThisYear,
            _ => DateFilterPreset.AllDates
        };
    }

    private void SourceLocationListView_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = GetSelectedSourceLocation();
        _currentSourcePath = selected?.Path;
        RefreshSourceItems();
    }

    private void DestinationLocationListView_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = GetSelectedDestinationLocation();
        _currentDestinationPath = selected?.Path;
        RefreshDestinationItems();
    }

    private void SourceItemListView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SourceItemListView.SelectedItem is not string display)
        {
            return;
        }

        var sourceLocation = GetSelectedSourceLocation();
        if (sourceLocation is null)
        {
            return;
        }

        var path = ResolveSourcePath(display, sourceLocation.Path);
        if (Directory.Exists(path))
        {
            _currentSourcePath = path;
            SourceSearchTextBox.Text = string.Empty;
            RefreshSourceItems();
            return;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            PreviewWindow.OpenPathWithShell(path);
        }
    }

    private void DestinationItemListView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DestinationItemListView.SelectedItem is not string display)
        {
            return;
        }

        if (!_destinationPathByDisplay.TryGetValue(display, out var path))
        {
            var destinationLocation = GetSelectedDestinationLocation();
            if (destinationLocation is null)
            {
                return;
            }

            var name = ExtractNameFromItemDisplay(display);
            path = Path.Combine(_currentDestinationPath ?? destinationLocation.Path, name);
        }

        if (Directory.Exists(path))
        {
            _currentDestinationPath = path;
            DestinationSearchTextBox.Text = string.Empty;
            RefreshDestinationItems();
            return;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            PreviewWindow.OpenPathWithShell(path);
        }
    }

    private void PreviewSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var path = ResolveSelectedPreviewPath();
        if (path is null)
        {
            MessageBox.Show(this, "Select a file from Source Items or Destination Items first.", "Preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new PreviewWindow(path)
        {
            Owner = this
        };
        window.Show();
    }

    private async void CopyToDestinationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var sourceLocation = GetSelectedSourceLocation();
        var destinationLocation = GetSelectedDestinationLocation();

        if (sourceLocation is null || destinationLocation is null)
        {
            MessageBox.Show(this, "Select both a source location and destination location.", "Copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SourceItemListView.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Select one or more source items to copy.", "Copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedFullPaths = SourceItemListView.SelectedItems
            .OfType<string>()
            .Select(display => ResolveSourcePath(display, sourceLocation.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var conflictResolution = ConflictResolution.KeepBoth;
        var applyToAll = false;
        if (selectedFullPaths.Count > 0)
        {
            var firstFile = selectedFullPaths.FirstOrDefault(File.Exists);
            if (firstFile is not null)
            {
                var destinationBasePath = GetCurrentDestinationPath(destinationLocation);
                var expectedDestination = Path.Combine(destinationBasePath, Path.GetFileName(firstFile));
                if (File.Exists(expectedDestination))
                {
                    var conflictDialog = new ConflictDialogWindow(firstFile, expectedDestination)
                    {
                        Owner = this
                    };

                    var dialogResult = conflictDialog.ShowDialog();
                    if (dialogResult != true && conflictDialog.SelectedResolution == ConflictResolution.Cancel)
                    {
                        StatusTextBlock.Text = "Copy canceled.";
                        return;
                    }

                    conflictResolution = conflictDialog.SelectedResolution;
                    applyToAll = conflictDialog.ApplyToAll;
                }
            }
        }

        await RunCopyOperationAsync(selectedFullPaths, destinationLocation, conflictResolution, applyToAll, "Copy completed.").ConfigureAwait(true);
    }

    public async Task HandleShellCopyAsync(IReadOnlyList<string> shellPaths, string? preferredDestinationId = null)
    {
        if (shellPaths.Count == 0)
        {
            return;
        }

        if (_destinationLocations.Count == 0)
        {
            MessageBox.Show(this, "No destination is configured. Open Settings and add a destination location first.", "Shell Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ConfiguredLocation? destination = null;
        if (!string.IsNullOrWhiteSpace(preferredDestinationId))
        {
            destination = _destinationLocations.FirstOrDefault(d => string.Equals(d.Id, preferredDestinationId, StringComparison.OrdinalIgnoreCase));
            if (destination is null)
            {
                StatusTextBlock.Text = "Shell destination not found. Please choose a destination.";
            }
        }

        if (destination is null && _destinationLocations.Count == 1)
        {
            destination = _destinationLocations[0];
        }

        if (destination is null)
        {
            var picker = new DestinationPickerWindow(_destinationLocations)
            {
                Owner = this
            };

            if (picker.ShowDialog() != true || picker.SelectedDestination is null)
            {
                StatusTextBlock.Text = "Shell copy canceled.";
                return;
            }

            destination = picker.SelectedDestination;
        }

        await RunCopyOperationAsync(shellPaths, destination, ConflictResolution.KeepBoth, applyToAll: true, "Shell copy completed.").ConfigureAwait(true);
    }

    private async Task RunCopyOperationAsync(
        IReadOnlyList<string> sourcePaths,
        ConfiguredLocation destinationLocation,
        ConflictResolution conflictResolution,
        bool applyToAll,
        string completionSummary)
    {
        try
        {
            StatusTextBlock.Text = "Copy operation started...";
            var destinationBasePath = GetCurrentDestinationPath(destinationLocation);
            var request = new FileOperationRequest
            {
                OperationType = FileOperationType.Copy,
                SourcePaths = sourcePaths,
                DestinationPath = destinationBasePath,
                ConflictResolution = conflictResolution,
                ApplyToAllConflicts = applyToAll,
                VerifyChecksumForLargeFiles = VerifyChecksumCheckBox.IsChecked == true,
                ChecksumThresholdBytes = _checksumThresholdBytes
            };

            using var cts = new CancellationTokenSource();
            var progressWindow = new OperationProgressWindow("Copy Progress", () => cts.Cancel())
            {
                Owner = this
            };

            var progress = new Progress<FileOperationProgress>(progressWindow.UpdateProgress);
            progressWindow.Show();
            try
            {
                await _fileOperationService.ExecuteAsync(request, progress, cts.Token).ConfigureAwait(true);
                progressWindow.MarkCompleted(completionSummary);
            }
            finally
            {
                if (progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }
            }

            StatusTextBlock.Text = $"Copied {sourcePaths.Count} item(s) to {destinationBasePath}.";
            RefreshDestinationItems();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Copy canceled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Copy failed.";
            MessageBox.Show(this, $"Copy failed.\n\n{ex.Message}", "Copy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DestinationItemListView_OnDrop(object sender, DragEventArgs e)
    {
        var destinationLocation = GetSelectedDestinationLocation();
        if (destinationLocation is null)
        {
            MessageBox.Show(this, "Select a destination location before dropping files.", "Drag and Drop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
        {
            return;
        }

        await RunCopyOperationAsync(dropped, destinationLocation, ConflictResolution.KeepBoth, applyToAll: true, "Drag-and-drop copy completed.").ConfigureAwait(true);
        StatusTextBlock.Text = $"Copied {dropped.Length} dropped item(s) to {destinationLocation.Name}.";
    }

    private void DestinationItemListView_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void SourceItemListView_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var sourceLocation = GetSelectedSourceLocation();
        if (sourceLocation is null)
        {
            return;
        }

        var selected = SourceItemListView.SelectedItems.OfType<string>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var fullPaths = selected.Select(display => ResolveSourcePath(display, sourceLocation.Path)).Where(File.Exists).Concat(selected.Select(display => ResolveSourcePath(display, sourceLocation.Path)).Where(Directory.Exists)).Distinct().ToArray();
        if (fullPaths.Length == 0)
        {
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, fullPaths);
        DragDrop.DoDragDrop(SourceItemListView, data, DragDropEffects.Copy);
    }

    private async void DeleteSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var sourceLocation = GetSelectedSourceLocation();
        if (sourceLocation is null)
        {
            MessageBox.Show(this, "Select a source location first.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SourceItemListView.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Select one or more items to delete.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedFullPaths = SourceItemListView.SelectedItems
            .OfType<string>()
            .Select(display => ResolveSourcePath(display, sourceLocation.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var totalBytes = TryComputeTotalBytes(selectedFullPaths);

        var confirmation = new DeleteConfirmationWindow(sourceLocation.Path, selectedFullPaths.Count, totalBytes)
        {
            Owner = this
        };

        var confirmed = confirmation.ShowDialog() == true && confirmation.IsConfirmed;
        if (!confirmed)
        {
            StatusTextBlock.Text = "Delete canceled.";
            return;
        }

        try
        {
            StatusTextBlock.Text = "Delete operation started...";
            var request = new FileOperationRequest
            {
                OperationType = FileOperationType.Delete,
                SourcePaths = selectedFullPaths,
                DestinationPath = null,
                ApplyToAllConflicts = false
            };

            using var cts = new CancellationTokenSource();
            var progressWindow = new OperationProgressWindow("Delete Progress", () => cts.Cancel())
            {
                Owner = this
            };

            var progress = new Progress<FileOperationProgress>(progressWindow.UpdateProgress);
            progressWindow.Show();
            try
            {
                await _fileOperationService.ExecuteAsync(request, progress, cts.Token).ConfigureAwait(true);
                progressWindow.MarkCompleted("Delete completed. Items are in protected recovery.");
            }
            finally
            {
                if (progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }
            }

            StatusTextBlock.Text = $"Deleted {selectedFullPaths.Count} item(s) to protected recovery.";
            RefreshSourceItems();
            RefreshDestinationItems();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Delete canceled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Delete failed.";
            MessageBox.Show(this, $"Delete failed.\n\n{ex.Message}", "Delete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            CopySelectionToClipboard(markAsCut: false);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X)
        {
            CopySelectionToClipboard(markAsCut: true);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            await PasteFromClipboardAsync().ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            SelectAllInFocusedList();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F2)
        {
            RenameSelectedItem();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete)
        {
            DeleteSelectedButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.Delete)
        {
            var warning = MessageBox.Show(
                this,
                "Shift+Delete uses immediate application-controlled delete with recovery. Continue?",
                "Strong Delete Warning",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (warning == MessageBoxResult.OK)
            {
                DeleteSelectedButton_OnClick(this, new RoutedEventArgs());
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Enter)
        {
            OpenSelectedItem();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Enter)
        {
            PreviewSelectedButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            ActivityLogButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void CopySelectionToClipboard(bool markAsCut)
    {
        var sourceLocation = GetSelectedSourceLocation();
        if (sourceLocation is null)
        {
            return;
        }

        var paths = SourceItemListView.SelectedItems
            .OfType<string>()
            .Select(display => ResolveSourcePath(display, sourceLocation.Path))
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            return;
        }

        var fileDrop = new StringCollection();
        fileDrop.AddRange(paths.ToArray());

        var data = new DataObject();
        data.SetFileDropList(fileDrop);
        Clipboard.SetDataObject(data, true);

        _pendingCutPaths = markAsCut ? paths : new List<string>();
        StatusTextBlock.Text = markAsCut ? $"Cut {paths.Count} item(s)." : $"Copied {paths.Count} item(s).";
    }

    private async Task PasteFromClipboardAsync()
    {
        var destination = GetSelectedDestinationLocation();
        if (destination is null)
        {
            MessageBox.Show(this, "Select a destination before pasting.", "Paste", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Clipboard.ContainsFileDropList())
        {
            return;
        }

        var dropList = Clipboard.GetFileDropList();
        if (dropList.Count == 0)
        {
            return;
        }

        var paths = dropList.Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        if (_pendingCutPaths.Count > 0 && _pendingCutPaths.All(paths.Contains))
        {
            await RunMoveOperationAsync(paths, destination, "Move completed.").ConfigureAwait(true);
            _pendingCutPaths.Clear();
            return;
        }

        await RunCopyOperationAsync(paths, destination, ConflictResolution.KeepBoth, applyToAll: true, "Paste completed.").ConfigureAwait(true);

        if (_pendingCutPaths.Count > 0)
        {
            _pendingCutPaths.Clear();
        }
    }

    private void SelectAllInFocusedList()
    {
        if (SourceItemListView.IsKeyboardFocusWithin)
        {
            SourceItemListView.SelectAll();
            return;
        }

        if (DestinationItemListView.IsKeyboardFocusWithin)
        {
            DestinationItemListView.SelectAll();
        }
    }

    private void OpenSelectedItem()
    {
        if (SourceItemListView.SelectedItem is string sourceDisplay)
        {
            var sourceLocation = GetSelectedSourceLocation();
            if (sourceLocation is null)
            {
                return;
            }

            var sourcePath = ResolveSourcePath(sourceDisplay, sourceLocation.Path);
            if (File.Exists(sourcePath) || Directory.Exists(sourcePath))
            {
                PreviewWindow.OpenPathWithShell(sourcePath);
            }
            return;
        }

        if (DestinationItemListView.SelectedItem is string destinationDisplay)
        {
            if (_destinationPathByDisplay.TryGetValue(destinationDisplay, out var destinationPath)
                && (File.Exists(destinationPath) || Directory.Exists(destinationPath)))
            {
                PreviewWindow.OpenPathWithShell(destinationPath);
            }
        }
    }

    private void RenameSelectedItem()
    {
        var sourceLocation = GetSelectedSourceLocation();
        if (sourceLocation is null || SourceItemListView.SelectedItem is not string selectedDisplay)
        {
            return;
        }

        var path = ResolveSourcePath(selectedDisplay, sourceLocation.Path);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var currentName = Path.GetFileName(path);
        var prompt = new InputPromptWindow("Rename", "Enter a new name:", currentName)
        {
            Owner = this
        };

        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Result))
        {
            return;
        }

        var newName = prompt.Result.Trim();
        if (string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        var newPath = Path.Combine(Path.GetDirectoryName(path) ?? sourceLocation.Path, newName);
        var request = new FileOperationRequest
        {
            OperationType = FileOperationType.Rename,
            SourcePaths = new[] { path },
            DestinationPath = newPath,
            ApplyToAllConflicts = false
        };

        try
        {
            _fileOperationService.ExecuteAsync(request, progress: null, CancellationToken.None).GetAwaiter().GetResult();
            StatusTextBlock.Text = $"Renamed '{currentName}' to '{newName}'.";
            RefreshSourceItems();
            _ = EnsureIndexBuiltAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Rename failed.\n\n{ex.Message}", "Rename", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunMoveOperationAsync(IReadOnlyList<string> sourcePaths, ConfiguredLocation destinationLocation, string completionSummary)
    {
        try
        {
            StatusTextBlock.Text = "Move operation started...";
            var destinationBasePath = GetCurrentDestinationPath(destinationLocation);
            var request = new FileOperationRequest
            {
                OperationType = FileOperationType.Move,
                SourcePaths = sourcePaths,
                DestinationPath = destinationBasePath,
                ConflictResolution = ConflictResolution.KeepBoth,
                ApplyToAllConflicts = true
            };

            using var cts = new CancellationTokenSource();
            var progressWindow = new OperationProgressWindow("Move Progress", () => cts.Cancel())
            {
                Owner = this
            };

            var progress = new Progress<FileOperationProgress>(progressWindow.UpdateProgress);
            progressWindow.Show();
            try
            {
                await _fileOperationService.ExecuteAsync(request, progress, cts.Token).ConfigureAwait(true);
                progressWindow.MarkCompleted(completionSummary);
            }
            finally
            {
                if (progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }
            }

            StatusTextBlock.Text = $"Moved {sourcePaths.Count} item(s) to {destinationBasePath}.";
            RefreshSourceItems();
            RefreshDestinationItems();
            _ = EnsureIndexBuiltAsync();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Move canceled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Move failed.";
            MessageBox.Show(this, $"Move failed.\n\n{ex.Message}", "Move", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CreateFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var destination = GetSelectedDestinationLocation();
        if (destination is null)
        {
            MessageBox.Show(this, "Select a destination first.", "Create Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var prompt = new InputPromptWindow("Create Folder", "Enter new folder name:", "New Folder")
        {
            Owner = this
        };

        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Result))
        {
            return;
        }

        var folderName = prompt.Result.Trim();
        var targetPath = Path.Combine(GetCurrentDestinationPath(destination), folderName);

        var request = new FileOperationRequest
        {
            OperationType = FileOperationType.CreateFolder,
            SourcePaths = new[] { targetPath },
            DestinationPath = null,
            ApplyToAllConflicts = false
        };

        try
        {
            await _fileOperationService.ExecuteAsync(request, progress: null, CancellationToken.None).ConfigureAwait(true);
            StatusTextBlock.Text = $"Folder created: {folderName}";
            RefreshDestinationItems();
            _ = EnsureIndexBuiltAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Create folder failed.\n\n{ex.Message}", "Create Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SourceUpButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedSourceLocation();
        if (selected is null || string.IsNullOrWhiteSpace(_currentSourcePath))
        {
            return;
        }

        var parent = Directory.GetParent(_currentSourcePath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !IsPathWithinRoot(parent, selected.Path))
        {
            _currentSourcePath = selected.Path;
        }
        else
        {
            _currentSourcePath = parent;
        }

        RefreshSourceItems();
    }

    private void DestinationUpButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedDestinationLocation();
        if (selected is null || string.IsNullOrWhiteSpace(_currentDestinationPath))
        {
            return;
        }

        var parent = Directory.GetParent(_currentDestinationPath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !IsPathWithinRoot(parent, selected.Path))
        {
            _currentDestinationPath = selected.Path;
        }
        else
        {
            _currentDestinationPath = parent;
        }

        RefreshDestinationItems();
    }

    private void UpdateSourceNavigationUi(string rootPath, string? currentPath)
    {
        if (SourceCurrentPathTextBlock is not null)
        {
            SourceCurrentPathTextBlock.Text = currentPath ?? rootPath;
        }

        if (SourceUpButton is not null)
        {
            SourceUpButton.IsEnabled = !string.IsNullOrWhiteSpace(currentPath)
                && !PathsEqual(currentPath, rootPath);
        }
    }

    private void UpdateDestinationNavigationUi(string rootPath, string? currentPath)
    {
        if (DestinationCurrentPathTextBlock is not null)
        {
            DestinationCurrentPathTextBlock.Text = currentPath ?? rootPath;
        }

        if (DestinationUpButton is not null)
        {
            DestinationUpButton.IsEnabled = !string.IsNullOrWhiteSpace(currentPath)
                && !PathsEqual(currentPath, rootPath);
        }
    }

    private string GetCurrentDestinationPath(ConfiguredLocation destinationLocation)
    {
        if (!string.IsNullOrWhiteSpace(_currentDestinationPath)
            && Directory.Exists(_currentDestinationPath)
            && IsPathWithinRoot(_currentDestinationPath, destinationLocation.Path))
        {
            return _currentDestinationPath;
        }

        return destinationLocation.Path;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        var leftFull = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rightFull = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshAllViews();
        StatusTextBlock.Text = "Views refreshed.";
    }

    private void ResetLayoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        Width = 1500;
        Height = 900;
        WindowState = WindowState.Normal;
        StatusTextBlock.Text = "Layout reset to default window size.";
        SaveUiState();
    }

    private void RebuildIndexButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new IndexStatusWindow(_indexService)
        {
            Owner = this
        };
        window.ShowDialog();

        RefreshAllViews();
    }

    private void ActivityLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new ActivityLogWindow(_activityLogRepository, _undoService, _fileOperationService, () =>
        {
            RefreshAllViews();
            _ = EnsureIndexBuiltAsync();
        }, () =>
        {
            var verify = VerifyChecksumCheckBox.IsChecked == true;
            return (verify, _checksumThresholdBytes);
        })
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_locationRepository, _indexService, _shellIntegrationService, _appPathsProvider, () =>
        {
            InitializeLocationsFromRepository();
            RefreshAllViews();
            _ = EnsureIndexBuiltAsync();
        }, () =>
        {
            ApplyAppPreferences();
        })
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private string ResolveSourcePath(string display, string sourceRoot)
    {
        if (_sourcePathByDisplay.TryGetValue(display, out var mapped))
        {
            return mapped;
        }

        var name = ExtractNameFromItemDisplay(display);
        var basePath = string.IsNullOrWhiteSpace(_currentSourcePath) ? sourceRoot : _currentSourcePath;
        return Path.Combine(basePath, name);
    }

    private async Task EnsureIndexBuiltAsync()
    {
        if (_isIndexing)
        {
            return;
        }

        _isIndexing = true;
        _indexingCts = new CancellationTokenSource();

        try
        {
            await Task.Yield();

            var activeLocations = _sourceLocations
                .Concat(_destinationLocations)
                .Where(x => x.IsEnabled)
                .ToList();

            if (activeLocations.Count == 0)
            {
                SetIndexingUiState(false, "Index idle: no enabled locations configured.", 0, 1);
                return;
            }

            var token = _indexingCts.Token;
            var completed = 0;

            foreach (var location in activeLocations)
            {
                token.ThrowIfCancellationRequested();
                SetIndexingUiState(true, $"Indexing: {location.Name} ({location.Path})", completed, activeLocations.Count);
                await _indexService.RebuildLocationAsync(location.Id, token).ConfigureAwait(true);
                completed++;
                SetIndexingUiState(true, $"Indexed: {location.Name}", completed, activeLocations.Count);
            }

            SetIndexingUiState(false, $"Index ready. Indexed {completed} location(s).", activeLocations.Count, activeLocations.Count);
            RefreshAllViews();
        }
        catch (OperationCanceledException)
        {
            SetIndexingUiState(false, "Indexing stopped.", 0, 1);
        }
        catch (Exception ex)
        {
            SetIndexingUiState(false, $"Indexing warning: {ex.Message}", 0, 1);
        }
        finally
        {
            _isIndexing = false;
            _indexingCts?.Dispose();
            _indexingCts = null;
        }
    }

    private void SetIndexingUiState(bool isRunning, string message, int completedLocations, int totalLocations)
    {
        if (IndexingNowTextBlock is not null)
        {
            IndexingNowTextBlock.Text = message;
        }

        if (IndexingProgressBar is not null)
        {
            if (totalLocations > 0)
            {
                IndexingProgressBar.IsIndeterminate = false;
                IndexingProgressBar.Value = Math.Clamp((double)completedLocations / totalLocations * 100d, 0d, 100d);
            }
            else
            {
                IndexingProgressBar.IsIndeterminate = isRunning;
                IndexingProgressBar.Value = 0;
            }
        }

        if (CancelIndexingButton is not null)
        {
            CancelIndexingButton.IsEnabled = isRunning;
        }

        if (StatusTextBlock is not null)
        {
            StatusTextBlock.Text = message;
        }
    }

    private void CancelIndexingButton_OnClick(object sender, RoutedEventArgs e)
    {
        _indexingCts?.Cancel();
    }

    private void RefreshShortcuts()
    {
        var shortcuts = _shortcutRepository.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        _shortcutByDisplay.Clear();

        var displays = new List<string>();
        foreach (var shortcut in shortcuts.OrderBy(x => x.SortOrder))
        {
            var marker = shortcut.TargetKind switch
            {
                ShortcutTargetKind.File => "📄",
                ShortcutTargetKind.Folder => "📁",
                ShortcutTargetKind.Program => "🚀",
                _ => "•"
            };
            var display = $"{marker} {shortcut.Label}";
            _shortcutByDisplay[display] = shortcut;
            displays.Add(display);
        }

        ShortcutListBox.ItemsSource = displays;
    }

    private void AddShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        var target = ResolveSelectedPreviewPath();
        if (target is null)
        {
            MessageBox.Show(this, "Select an item first to create a shortcut.", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kind = File.Exists(target)
            ? ShortcutTargetKind.File
            : (Directory.Exists(target) ? ShortcutTargetKind.Folder : ShortcutTargetKind.Program);

        var existing = _shortcutRepository.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(x => x.SortOrder) + 1;

        var shortcut = new ShortcutItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = Path.GetFileName(target),
            TargetPath = target,
            TargetKind = kind,
            SortOrder = nextOrder
        };

        _shortcutRepository.UpsertAsync(shortcut, CancellationToken.None).GetAwaiter().GetResult();
        RefreshShortcuts();
        StatusTextBlock.Text = $"Shortcut added: {shortcut.Label}";
    }

    private void RemoveShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ShortcutListBox.SelectedItem is not string display || !_shortcutByDisplay.TryGetValue(display, out var shortcut))
        {
            MessageBox.Show(this, "Select a shortcut to remove.", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Remove shortcut '{shortcut.Label}'?\n\nThe underlying file or folder will not be deleted.",
            "Remove Shortcut",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _shortcutRepository.DeleteAsync(shortcut.Id, CancellationToken.None).GetAwaiter().GetResult();
        RefreshShortcuts();
        StatusTextBlock.Text = $"Shortcut removed: {shortcut.Label}";
    }

    private void OpenShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSelectedShortcut();
    }

    private void ShortcutListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedShortcut();
    }

    private void OpenSelectedShortcut()
    {
        if (ShortcutListBox.SelectedItem is not string display || !_shortcutByDisplay.TryGetValue(display, out var shortcut))
        {
            MessageBox.Show(this, "Select a shortcut to open.", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(shortcut.TargetPath) && !Directory.Exists(shortcut.TargetPath))
        {
            MessageBox.Show(this, "Shortcut target is unavailable. Check the path or network location.", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PreviewWindow.OpenPathWithShell(shortcut.TargetPath);
    }

    private string? ResolveSelectedPreviewPath()
    {
        if (SourceItemListView.SelectedItem is string sourceDisplay)
        {
            var sourceLocation = GetSelectedSourceLocation();
            if (sourceLocation is not null)
            {
                return ResolveSourcePath(sourceDisplay, sourceLocation.Path);
            }
        }

        if (DestinationItemListView.SelectedItem is string destinationDisplay)
        {
            if (_destinationPathByDisplay.TryGetValue(destinationDisplay, out var mapped))
            {
                return mapped;
            }

            var destinationLocation = GetSelectedDestinationLocation();
            if (destinationLocation is not null)
            {
                var name = ExtractNameFromItemDisplay(destinationDisplay);
                return Path.Combine(destinationLocation.Path, name);
            }
        }

        return null;
    }

    private void AboutButton_OnClick(object sender, RoutedEventArgs e)
    {
        var aboutText =
            "Lui-P File Manager\n\n" +
            "Phase status:\n" +
            "- Four-column shell active\n" +
            "- SQLite operation journaling active\n" +
            "- Activity Log window active\n" +
            "- Copy engine using safe keep-both naming active\n\n" +
            $"Data root: {_appPathsProvider.GetAppDataRoot()}\n" +
            $"Database: {_appPathsProvider.GetDatabasePath()}\n" +
            $"Recovery store: {_appPathsProvider.GetRecoveryRoot()}";

        var window = new SimpleInfoWindow("About Lui-P File Manager", aboutText)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private static long? TryComputeTotalBytes(IReadOnlyList<string> paths)
    {
        try
        {
            long total = 0;

            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    total += new FileInfo(path).Length;
                    continue;
                }

                if (!Directory.Exists(path))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    total += new FileInfo(file).Length;
                }
            }

            return total;
        }
        catch
        {
            return null;
        }
    }
}