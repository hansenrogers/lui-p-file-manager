using System.IO;
using System.Windows.Input;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Collections.Specialized;
using System.Collections.Concurrent;
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
    private enum ItemSortMode
    {
        NameAscending = 0,
        NameDescending = 1,
        DateModifiedNewest = 2,
        DateModifiedOldest = 3
    }

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
    private readonly DispatcherTimer _indexReconcileTimer;

    private readonly List<ConfiguredLocation> _sourceLocations = new();
    private readonly List<ConfiguredLocation> _destinationLocations = new();
    private readonly Dictionary<string, ConfiguredLocation> _sourceLocationByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConfiguredLocation> _destinationLocationByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourcePathByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _destinationPathByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutItem> _shortcutById = new(StringComparer.Ordinal);

    private DateFilterPreset _sourceDateFilter = DateFilterPreset.AllDates;
    private DateFilterPreset _destinationDateFilter = DateFilterPreset.AllDates;
    private ItemSortMode _sourceSortMode = ItemSortMode.NameAscending;
    private ItemSortMode _destinationSortMode = ItemSortMode.NameAscending;
    private const double DefaultNameColumnWidth = 260;
    private const double DefaultModifiedColumnWidth = 170;
    private List<string> _pendingCutPaths = new();
    private bool _verifyLargeFileChecksumByDefault;
    private long _checksumThresholdBytes = 32L * 1024L * 1024L;
    private string? _currentSourcePath;
    private string? _currentDestinationPath;
    private CancellationTokenSource? _indexingCts;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _watcherDebounceByLocation = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _indexWatchers = new();
    private bool _isIndexing;
    private bool _hasStartedAutoIndex;
    private bool _isUiInitialized;
    private bool _startupInitialized;
    private TimeSpan? _lastIndexDuration;

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
        _indexReconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _indexReconcileTimer.Tick += IndexReconcileTimer_OnTick;

        InitializeComponent();

        SizeChanged += MainWindow_OnUiChanged;
        LocationChanged += MainWindow_OnUiChanged;
        StateChanged += MainWindow_OnUiChanged;
        Closed += MainWindow_OnClosed;
        Loaded += MainWindow_OnLoaded;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_startupInitialized)
        {
            _startupInitialized = true;

            RestoreUiState();
            ApplyAppPreferences();
            InitializeLocationsFromRepository();
            ConfigureIndexWatchers();
            RefreshShortcuts();
            _isUiInitialized = true;
            ApplyColumnVisibility();
            RefreshAllViews();
            UpdateTransferContextBar();
        }

        _ = RefreshLastIndexedStateAsync();
        _indexReconcileTimer.Start();
        StartBackgroundIndexingIfIdle();
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
        _indexReconcileTimer.Stop();
        DisposeIndexWatchers();
        foreach (var pair in _watcherDebounceByLocation)
        {
            pair.Value.Cancel();
            pair.Value.Dispose();
        }
        _watcherDebounceByLocation.Clear();
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
        _verifyLargeFileChecksumByDefault = preferences.VerifyLargeFileChecksumByDefault;
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
                .GetResult()
                .Where(x => MatchesDateFilter(x.ModifiedUtc, _sourceDateFilter));

            var sortedSearchResults = SortSearchResults(searchResults, _sourceSortMode).ToList();

            var rows = sortedSearchResults
                .Select(result => CreateItemRow(result.FullPath, result.Name, result.IsFolder, result.ModifiedUtc))
                .ToList();

            SourceItemListView.ItemsSource = rows;
            UpdateTransferContextBar();
            return;
        }

        var fsEntries = Directory.EnumerateFileSystemEntries(_currentSourcePath)
            .Where(path => MatchesDateFilter(path, _sourceDateFilter));
        var sortedEntries = SortPathEntries(fsEntries, _sourceSortMode).Take(500).ToList();
        var items = new List<FileItemRow>(sortedEntries.Count);
        foreach (var entry in sortedEntries)
        {
            items.Add(CreateItemRow(entry));
        }

        SourceItemListView.ItemsSource = items;
        UpdateTransferContextBar();
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
                .GetResult()
                .Where(x => MatchesDateFilter(x.ModifiedUtc, _destinationDateFilter));

            var sortedSearchResults = SortSearchResults(searchResults, _destinationSortMode).ToList();

            var rows = sortedSearchResults
                .Select(result => CreateItemRow(result.FullPath, result.Name, result.IsFolder, result.ModifiedUtc))
                .ToList();

            DestinationItemListView.ItemsSource = rows;
            UpdateTransferContextBar();
            return;
        }

        var fsEntries = Directory.EnumerateFileSystemEntries(_currentDestinationPath)
            .Where(path => MatchesDateFilter(path, _destinationDateFilter));
        var sortedEntries = SortPathEntries(fsEntries, _destinationSortMode).Take(500).ToList();
        var items = new List<FileItemRow>(sortedEntries.Count);
        foreach (var entry in sortedEntries)
        {
            items.Add(CreateItemRow(entry));
        }

        DestinationItemListView.ItemsSource = items;
        UpdateTransferContextBar();
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

    private static FileItemRow CreateItemRow(string fullPath)
    {
        var isFolder = Directory.Exists(fullPath);
        var name = Path.GetFileName(fullPath);
        return CreateItemRow(fullPath, name, isFolder, TryGetLastWriteUtc(fullPath));
    }

    private static FileItemRow CreateItemRow(string fullPath, string name, bool isFolder, DateTimeOffset? modifiedUtc)
    {
        return new FileItemRow
        {
            FullPath = fullPath,
            Name = name,
            IsFolder = isFolder,
            TypeGlyph = isFolder ? "📁" : "📄",
            ModifiedUtc = modifiedUtc,
            ModifiedDisplay = FormatModifiedForDisplay(modifiedUtc)
        };
    }

    private static string ExtractNameFromItemDisplay(string itemDisplay)
    {
        var parts = itemDisplay.Split(' ', 2);
        var value = parts.Length == 2 ? parts[1] : itemDisplay;

        var modifiedToken = value.IndexOf(" | Modified:", StringComparison.Ordinal);
        if (modifiedToken >= 0)
        {
            value = value[..modifiedToken];
        }

        return value;
    }

    private static string FormatModifiedForDisplay(DateTimeOffset? modifiedUtc)
    {
        if (!modifiedUtc.HasValue)
        {
            return "Unknown";
        }

        return modifiedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd h:mm tt");
    }

    private void SourceSearchTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        RefreshSourceItems();
    }

    private void DestinationSearchTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        RefreshDestinationItems();
    }

    private void SourceItemListView_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateTransferContextBar();
    }

    private void DestinationItemListView_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateTransferContextBar();
    }

    private void SourceDateFilterComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _sourceDateFilter = ParseDateFilterPreset(SourceDateFilterComboBox.SelectedIndex);
        RefreshSourceItems();
    }

    private void DestinationDateFilterComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _destinationDateFilter = ParseDateFilterPreset(DestinationDateFilterComboBox.SelectedIndex);
        RefreshDestinationItems();
    }

    private void SourceSortNameAscButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _sourceSortMode = ItemSortMode.NameAscending;
        RefreshSourceItems();
    }

    private void SourceSortNameDescButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _sourceSortMode = ItemSortMode.NameDescending;
        RefreshSourceItems();
    }

    private void SourceSortDateNewestButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _sourceSortMode = ItemSortMode.DateModifiedNewest;
        RefreshSourceItems();
    }

    private void SourceSortDateOldestButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _sourceSortMode = ItemSortMode.DateModifiedOldest;
        RefreshSourceItems();
    }

    private void DestinationSortNameAscButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _destinationSortMode = ItemSortMode.NameAscending;
        RefreshDestinationItems();
    }

    private void DestinationSortNameDescButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _destinationSortMode = ItemSortMode.NameDescending;
        RefreshDestinationItems();
    }

    private void DestinationSortDateNewestButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _destinationSortMode = ItemSortMode.DateModifiedNewest;
        RefreshDestinationItems();
    }

    private void DestinationSortDateOldestButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        _destinationSortMode = ItemSortMode.DateModifiedOldest;
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

    private static IEnumerable<IndexedSearchResult> SortSearchResults(IEnumerable<IndexedSearchResult> results, ItemSortMode sortMode)
    {
        return sortMode switch
        {
            ItemSortMode.NameDescending => results.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase),
            ItemSortMode.DateModifiedNewest => results
                .OrderByDescending(x => x.ModifiedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            ItemSortMode.DateModifiedOldest => results
                .OrderBy(x => x.ModifiedUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => results.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IEnumerable<string> SortPathEntries(IEnumerable<string> paths, ItemSortMode sortMode)
    {
        return sortMode switch
        {
            ItemSortMode.NameDescending => paths.OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase),
            ItemSortMode.DateModifiedNewest => paths
                .OrderByDescending(path => TryGetLastWriteUtc(path) ?? DateTimeOffset.MinValue)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase),
            ItemSortMode.DateModifiedOldest => paths
                .OrderBy(path => TryGetLastWriteUtc(path) ?? DateTimeOffset.MaxValue)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase),
            _ => paths.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool MatchesDateFilter(string path, DateFilterPreset preset)
    {
        return MatchesDateFilter(TryGetLastWriteUtc(path), preset);
    }

    private static bool MatchesDateFilter(DateTimeOffset? modifiedUtc, DateFilterPreset preset)
    {
        if (preset == DateFilterPreset.AllDates)
        {
            return true;
        }

        if (!modifiedUtc.HasValue)
        {
            return false;
        }

        var modifiedLocal = modifiedUtc.Value.ToLocalTime().Date;
        var nowLocal = DateTimeOffset.Now;
        var today = nowLocal.Date;

        return preset switch
        {
            DateFilterPreset.Today => modifiedLocal == today,
            DateFilterPreset.Last7Days => modifiedLocal >= today.AddDays(-6),
            DateFilterPreset.Last30Days => modifiedLocal >= today.AddDays(-29),
            DateFilterPreset.ThisYear => modifiedLocal.Year == nowLocal.Year,
            _ => true
        };
    }

    private static DateTimeOffset? TryGetLastWriteUtc(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }

            if (Directory.Exists(path))
            {
                return Directory.GetLastWriteTimeUtc(path);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private void SourceLocationListView_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        var selected = GetSelectedSourceLocation();
        _currentSourcePath = selected?.Path;
        if (SelectedSourceRootTextBlock is not null)
        {
            SelectedSourceRootTextBlock.Text = selected is null
                ? "Selected: none"
                : $"Selected: {selected.Name} ({selected.Path})";
        }
        RefreshSourceItems();
        UpdateTransferContextBar();
    }

    private void DestinationLocationListView_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        var selected = GetSelectedDestinationLocation();
        _currentDestinationPath = selected?.Path;
        if (SelectedDestinationRootTextBlock is not null)
        {
            SelectedDestinationRootTextBlock.Text = selected is null
                ? "Selected: none"
                : $"Selected: {selected.Name} ({selected.Path})";
        }
        RefreshDestinationItems();
        UpdateTransferContextBar();
    }

    private void SourceAddLocationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var editor = new LocationEditorWindow(_locationRepository, LocationKind.Source)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true)
        {
            InitializeLocationsFromRepository();
            ConfigureIndexWatchers();
            RefreshAllViews();
            _ = EnsureIndexBuiltAsync();
            StatusTextBlock.Text = "Source locations updated.";
        }
    }

    private void SourceRemoveLocationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedSourceLocation();
        if (selected is null)
        {
            MessageBox.Show(this, "Select a source location to remove.", "Source Locations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Remove source location '{selected.Name}'?\n\nPath: {selected.Path}",
            "Remove Source Location",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _locationRepository.DeleteAsync(selected.Id, LocationKind.Source, CancellationToken.None).GetAwaiter().GetResult();
        InitializeLocationsFromRepository();
        ConfigureIndexWatchers();
        RefreshAllViews();
        _ = EnsureIndexBuiltAsync();
        StatusTextBlock.Text = $"Removed source location: {selected.Name}";
    }

    private void DestinationAddLocationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var editor = new LocationEditorWindow(_locationRepository, LocationKind.Destination)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true)
        {
            InitializeLocationsFromRepository();
            ConfigureIndexWatchers();
            RefreshAllViews();
            _ = EnsureIndexBuiltAsync();
            StatusTextBlock.Text = "Destination locations updated.";
        }
    }

    private void DestinationRemoveLocationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedDestinationLocation();
        if (selected is null)
        {
            MessageBox.Show(this, "Select a destination location to remove.", "Destination Locations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Remove destination location '{selected.Name}'?\n\nPath: {selected.Path}",
            "Remove Destination Location",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _locationRepository.DeleteAsync(selected.Id, LocationKind.Destination, CancellationToken.None).GetAwaiter().GetResult();
        InitializeLocationsFromRepository();
        ConfigureIndexWatchers();
        RefreshAllViews();
        _ = EnsureIndexBuiltAsync();
        StatusTextBlock.Text = $"Removed destination location: {selected.Name}";
    }

    private void SourceItemListView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SourceItemListView.SelectedItem is not FileItemRow selectedItem)
        {
            return;
        }

        var path = selectedItem.FullPath;
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
        if (DestinationItemListView.SelectedItem is not FileItemRow selectedItem)
        {
            return;
        }

        var path = selectedItem.FullPath;

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

        if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                PreviewWindow.OpenPathWithShell(path);
                StatusTextBlock.Text = "Opened PDF in default app.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Unable to open PDF.\n\n{ex.Message}", "Open PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

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
        var destinationLocation = GetSelectedDestinationLocation();

        if (destinationLocation is null)
        {
            MessageBox.Show(this, "Select both a source location and destination location.", "Copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SourceItemListView.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Select one or more source items to copy.", "Copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedFullPaths = GetSelectedSourcePaths();

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
                VerifyChecksumForLargeFiles = _verifyLargeFileChecksumByDefault,
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

        var selected = SourceItemListView.SelectedItems.OfType<FileItemRow>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var fullPaths = selected.Select(item => item.FullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

        var selectedFullPaths = GetSelectedSourcePaths();

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
            ShowPropertiesForSelection();
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
        if (GetSelectedSourceLocation() is null)
        {
            return;
        }

        var paths = SourceItemListView.SelectedItems
            .OfType<FileItemRow>()
            .Select(item => item.FullPath)
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
        if (SourceItemListView.IsKeyboardFocusWithin && SourceItemListView.SelectedItem is FileItemRow focusedSourceItem)
        {
            var sourcePath = focusedSourceItem.FullPath;
            if (File.Exists(sourcePath) || Directory.Exists(sourcePath))
            {
                PreviewWindow.OpenPathWithShell(sourcePath);
            }
            return;
        }

        if (DestinationItemListView.IsKeyboardFocusWithin && DestinationItemListView.SelectedItem is FileItemRow focusedDestinationItem)
        {
            var destinationPath = focusedDestinationItem.FullPath;
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                PreviewWindow.OpenPathWithShell(destinationPath);
            }

            return;
        }

        if (SourceItemListView.SelectedItem is FileItemRow sourceItem)
        {
            var sourcePath = sourceItem.FullPath;
            if (File.Exists(sourcePath) || Directory.Exists(sourcePath))
            {
                PreviewWindow.OpenPathWithShell(sourcePath);
            }
            return;
        }

        if (DestinationItemListView.SelectedItem is FileItemRow destinationItem)
        {
            var destinationPath = destinationItem.FullPath;
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                PreviewWindow.OpenPathWithShell(destinationPath);
            }
        }
    }

    private void RenameSelectedItem()
    {
        var sourceLocation = GetSelectedSourceLocation();
        if (sourceLocation is null || SourceItemListView.SelectedItem is not FileItemRow selectedItem)
        {
            return;
        }

        var path = selectedItem.FullPath;
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
        UpdateTransferContextBar();
    }

    private void PropertiesButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowPropertiesForSelection();
    }

    private void SourceContextOpen_OnClick(object sender, RoutedEventArgs e)
    {
        SourceItemListView.Focus();
        OpenSelectedItem();
    }

    private void SourceContextPreview_OnClick(object sender, RoutedEventArgs e)
    {
        SourceItemListView.Focus();
        PreviewSelectedButton_OnClick(this, new RoutedEventArgs());
    }

    private void SourceContextProperties_OnClick(object sender, RoutedEventArgs e)
    {
        SourceItemListView.Focus();
        ShowPropertiesForSelection();
    }

    private void DestinationContextOpen_OnClick(object sender, RoutedEventArgs e)
    {
        DestinationItemListView.Focus();
        OpenSelectedItem();
    }

    private void DestinationContextPreview_OnClick(object sender, RoutedEventArgs e)
    {
        DestinationItemListView.Focus();
        PreviewSelectedButton_OnClick(this, new RoutedEventArgs());
    }

    private void DestinationContextProperties_OnClick(object sender, RoutedEventArgs e)
    {
        DestinationItemListView.Focus();
        ShowPropertiesForSelection();
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
        var window = new ActivityLogWindow(_activityLogRepository, _undoService, _fileOperationService, _appPathsProvider.GetDiagnosticsLogRoot(), () =>
        {
            RefreshAllViews();
            _ = EnsureIndexBuiltAsync();
        }, () =>
        {
            return (_verifyLargeFileChecksumByDefault, _checksumThresholdBytes);
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
            ConfigureIndexWatchers();
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
        var runStartedUtc = DateTimeOffset.UtcNow;

        try
        {
            SetIndexingUiState(true, "Index startup scan beginning...", 0, 0);
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
                await Task.Run(async () =>
                {
                    await _indexService.RebuildLocationAsync(location.Id, token).ConfigureAwait(false);
                }, token).ConfigureAwait(true);
                completed++;
                SetIndexingUiState(true, $"Indexed: {location.Name}", completed, activeLocations.Count);
            }

            SetIndexingUiState(false, $"Index ready. Indexed {completed} location(s).", activeLocations.Count, activeLocations.Count);
            RefreshAllViews();
            _lastIndexDuration = DateTimeOffset.UtcNow - runStartedUtc;
            await RefreshLastIndexedStateAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetIndexingUiState(false, "Indexing stopped.", 0, 1);
            await RefreshLastIndexedStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendIndexDiagnostics("Auto index run failed", ex);
            SetIndexingUiState(false, "Index issue logged. Open Activity Log for details.", 0, 1);
            await RefreshLastIndexedStateAsync().ConfigureAwait(true);
        }
        finally
        {
            _isIndexing = false;
            _indexingCts?.Dispose();
            _indexingCts = null;
        }
    }

    private void StartBackgroundIndexingIfIdle()
    {
        if (_hasStartedAutoIndex)
        {
            return;
        }

        _hasStartedAutoIndex = true;
        SetIndexingUiState(true, "Index startup scan queued...", 0, 0);
        _ = EnsureIndexBuiltAsync();
    }

    private void ConfigureIndexWatchers()
    {
        DisposeIndexWatchers();

        foreach (var location in _sourceLocations.Concat(_destinationLocations).Where(x => x.IsEnabled))
        {
            try
            {
                if (!Directory.Exists(location.Path))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(location.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                };

                watcher.Changed += (_, _) => ScheduleLocationReindex(location);
                watcher.Created += (_, _) => ScheduleLocationReindex(location);
                watcher.Deleted += (_, _) => ScheduleLocationReindex(location);
                watcher.Renamed += (_, _) => ScheduleLocationReindex(location);
                watcher.Error += (_, _) => ScheduleLocationReindex(location);
                watcher.EnableRaisingEvents = true;
                _indexWatchers.Add(watcher);
            }
            catch
            {
                // If a watcher cannot attach (path/network/transient), periodic reconcile still refreshes index state.
            }
        }
    }

    private void DisposeIndexWatchers()
    {
        foreach (var watcher in _indexWatchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Ignore cleanup errors during shutdown/reconfigure.
            }
        }

        _indexWatchers.Clear();
    }

    private void ScheduleLocationReindex(ConfiguredLocation location)
    {
        var cts = new CancellationTokenSource();
        _watcherDebounceByLocation.AddOrUpdate(location.Id, cts, (_, existing) =>
        {
            existing.Cancel();
            existing.Dispose();
            return cts;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);

                if (_isIndexing)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    _ = ReindexSingleLocationAsync(location, "Auto index update");
                });
            }
            catch (OperationCanceledException)
            {
                // Debounced by a newer change event.
            }
            finally
            {
                if (_watcherDebounceByLocation.TryGetValue(location.Id, out var latest) && ReferenceEquals(latest, cts))
                {
                    _watcherDebounceByLocation.TryRemove(location.Id, out _);
                }

                cts.Dispose();
            }
        });
    }

    private void ShowPropertiesForSelection()
    {
        var paths = ResolveActiveSelectionPaths();
        if (paths.Count == 0)
        {
            return;
        }

        if (paths.Count == 1)
        {
            var details = BuildPathProperties(paths[0]);
            var singleWindow = new SimpleInfoWindow("Item Properties", details)
            {
                Owner = this
            };
            singleWindow.ShowDialog();
            return;
        }

        var existing = paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
        var totalBytes = TryComputeTotalBytes(existing);

        var lines = new List<string>
        {
            $"Selected items: {paths.Count}",
            $"Existing items: {existing.Count}",
            $"Estimated total bytes: {(totalBytes.HasValue ? totalBytes.Value.ToString("N0") : "Unavailable")}",
            string.Empty,
            "Sample paths:"
        };

        foreach (var path in paths.Take(8))
        {
            lines.Add($"- {path}");
        }

        if (paths.Count > 8)
        {
            lines.Add($"- ... and {paths.Count - 8} more");
        }

        var multiWindow = new SimpleInfoWindow("Selection Properties", string.Join(Environment.NewLine, lines))
        {
            Owner = this
        };
        multiWindow.ShowDialog();
    }

    private List<string> ResolveActiveSelectionPaths()
    {
        if (SourceItemListView.IsKeyboardFocusWithin)
        {
            var sourceLocation = GetSelectedSourceLocation();
            if (sourceLocation is not null)
            {
                return SourceItemListView.SelectedItems
                    .OfType<string>()
                    .Select(display => ResolveSourcePath(display, sourceLocation.Path))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        if (DestinationItemListView.IsKeyboardFocusWithin)
        {
            var destination = new List<string>();
            foreach (var display in DestinationItemListView.SelectedItems.OfType<string>())
            {
                if (_destinationPathByDisplay.TryGetValue(display, out var mapped))
                {
                    destination.Add(mapped);
                    continue;
                }

                var destinationLocation = GetSelectedDestinationLocation();
                if (destinationLocation is not null)
                {
                    var name = ExtractNameFromItemDisplay(display);
                    destination.Add(Path.Combine(GetCurrentDestinationPath(destinationLocation), name));
                }
            }

            return destination
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var fallback = ResolveSelectedPreviewPath();
        return fallback is null ? new List<string>() : new List<string> { fallback };
    }

    private static string BuildPathProperties(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return $"Path does not exist:{Environment.NewLine}{path}";
        }

        var isFile = File.Exists(path);
        var info = isFile ? new FileInfo(path) as FileSystemInfo : new DirectoryInfo(path);

        var lines = new List<string>
        {
            $"Name: {info.Name}",
            $"Full Path: {info.FullName}",
            $"Type: {(isFile ? "File" : "Folder")}",
            $"Created: {info.CreationTime}",
            $"Modified: {info.LastWriteTime}"
        };

        if (isFile)
        {
            var fileInfo = (FileInfo)info;
            lines.Add($"Size: {fileInfo.Length:N0} bytes");
            lines.Add($"Extension: {fileInfo.Extension}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task ReindexSingleLocationAsync(ConfiguredLocation location, string reason)
    {
        if (_isIndexing)
        {
            return;
        }

        _isIndexing = true;
        var runStartedUtc = DateTimeOffset.UtcNow;
        try
        {
            SetIndexingUiState(true, $"{reason}: {location.Name}", 0, 1);
            await Task.Run(async () =>
            {
                await _indexService.RebuildLocationAsync(location.Id, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(true);
            SetIndexingUiState(false, $"Index updated: {location.Name}", 1, 1);
            RefreshAllViews();
            _lastIndexDuration = DateTimeOffset.UtcNow - runStartedUtc;
            await RefreshLastIndexedStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendIndexDiagnostics($"Index update failed for location '{location.Name}'", ex);
            SetIndexingUiState(false, "Index update issue logged. Open Activity Log for details.", 0, 1);
            await RefreshLastIndexedStateAsync().ConfigureAwait(true);
        }
        finally
        {
            _isIndexing = false;
        }
    }

    private void IndexReconcileTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isIndexing)
        {
            return;
        }

        _ = EnsureIndexBuiltAsync();
    }

    private void SetIndexingUiState(bool isRunning, string message, int completedLocations, int totalLocations)
    {
        if (HeaderIndexingTextBlock is not null)
        {
            HeaderIndexingTextBlock.Text = message;
        }

        if (HeaderIndexingProgressBar is not null)
        {
            if (totalLocations > 0)
            {
                HeaderIndexingProgressBar.IsIndeterminate = false;
                HeaderIndexingProgressBar.Value = Math.Clamp((double)completedLocations / totalLocations * 100d, 0d, 100d);
            }
            else
            {
                HeaderIndexingProgressBar.IsIndeterminate = isRunning;
                HeaderIndexingProgressBar.Value = 0;
            }
        }

        if (HeaderIndexingDot is not null)
        {
            HeaderIndexingDot.Fill = isRunning
                ? System.Windows.Media.Brushes.OrangeRed
                : System.Windows.Media.Brushes.ForestGreen;
        }
    }

    private void AppendIndexDiagnostics(string context, Exception ex)
    {
        try
        {
            var logRoot = _appPathsProvider.GetDiagnosticsLogRoot();
            Directory.CreateDirectory(logRoot);
            var logPath = Path.Combine(logRoot, "indexing.log");
            var entry =
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // Best-effort diagnostics write; keep UI responsive if disk logging fails.
        }
    }

    private async Task RefreshLastIndexedStateAsync()
    {
        try
        {
            var statuses = await _indexService.GetLocationStatusesAsync(CancellationToken.None).ConfigureAwait(true);
            var latest = statuses
                .Where(x => x.LastIndexedUtc.HasValue)
                .Select(x => x.LastIndexedUtc!.Value)
                .DefaultIfEmpty()
                .Max();

            if (HeaderLastIndexedTextBlock is null)
            {
                return;
            }

            if (latest == default)
            {
                HeaderLastIndexedTextBlock.Text = _lastIndexDuration is null
                    ? "Last indexed: not yet"
                    : $"Last indexed: not yet | Last scan duration: {FormatDuration(_lastIndexDuration.Value)}";
                HeaderLastIndexedTextBlock.Foreground = _lastIndexDuration is null
                    ? System.Windows.Media.Brushes.SteelBlue
                    : GetDurationBrush(_lastIndexDuration.Value);
                return;
            }

            var localTime = latest.ToLocalTime();
            HeaderLastIndexedTextBlock.Text = _lastIndexDuration is null
                ? $"Last indexed: {localTime:yyyy-MM-dd h:mm:ss tt}"
                : $"Last indexed: {localTime:yyyy-MM-dd h:mm:ss tt} | Last scan duration: {FormatDuration(_lastIndexDuration.Value)}";
            HeaderLastIndexedTextBlock.Foreground = _lastIndexDuration is null
                ? System.Windows.Media.Brushes.SteelBlue
                : GetDurationBrush(_lastIndexDuration.Value);
        }
        catch
        {
            if (HeaderLastIndexedTextBlock is not null)
            {
                HeaderLastIndexedTextBlock.Text = "Last indexed: unavailable";
                HeaderLastIndexedTextBlock.Foreground = System.Windows.Media.Brushes.DimGray;
            }
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return "<1s";
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{Math.Round(duration.TotalSeconds)}s";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
    }

    private static System.Windows.Media.Brush GetDurationBrush(TimeSpan duration)
    {
        if (duration.TotalSeconds < 30)
        {
            return System.Windows.Media.Brushes.ForestGreen;
        }

        if (duration.TotalMinutes < 2)
        {
            return System.Windows.Media.Brushes.Goldenrod;
        }

        return System.Windows.Media.Brushes.IndianRed;
    }

    private void CancelIndexingButton_OnClick(object sender, RoutedEventArgs e)
    {
        _indexingCts?.Cancel();
    }

    private void UpdateTransferContextBar()
    {
        if (TransferSelectedTextBlock is null || TransferDestinationTextBlock is null)
        {
            return;
        }

        var destination = GetSelectedDestinationLocation();
        if (destination is null)
        {
            TransferDestinationTextBlock.Text = "No destination selected";
        }
        else
        {
            var effectiveDestinationPath = GetCurrentDestinationPath(destination);
            TransferDestinationTextBlock.Text = effectiveDestinationPath;
        }

        if (SourceItemListView.SelectedItems.Count == 0)
        {
            TransferSelectedTextBlock.Text = "None";
            return;
        }

        var sourceLocation = GetSelectedSourceLocation();
        if (sourceLocation is null)
        {
            TransferSelectedTextBlock.Text = $"{SourceItemListView.SelectedItems.Count} item(s) selected";
            return;
        }

        if (SourceItemListView.SelectedItems.Count == 1 && SourceItemListView.SelectedItem is FileItemRow selectedItem)
        {
            TransferSelectedTextBlock.Text = selectedItem.FullPath;
            return;
        }

        TransferSelectedTextBlock.Text = $"{SourceItemListView.SelectedItems.Count} item(s) selected";
    }

    private void RefreshShortcuts()
    {
        var shortcuts = _shortcutRepository.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        _shortcutById.Clear();

        var items = new List<ShortcutListItem>();
        foreach (var shortcut in shortcuts.OrderBy(x => x.SortOrder))
        {
            try
            {
                _shortcutById[shortcut.Id] = shortcut;
                items.Add(new ShortcutListItem
                {
                    Id = shortcut.Id,
                    Label = shortcut.Label,
                    FullPath = shortcut.TargetPath,
                    Glyph = GetShortcutGlyph(shortcut.TargetKind)
                });
            }
            catch
            {
                items.Add(new ShortcutListItem
                {
                    Id = shortcut.Id,
                    Label = shortcut.Label,
                    FullPath = shortcut.TargetPath,
                    Glyph = "•"
                });
            }
        }

        ShortcutListBox.ItemsSource = items;
    }

    private void AddShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        var target = ResolveSelectedPreviewPath();
        if (target is null)
        {
            MessageBox.Show(this, "Select an item first to create a shortcut.", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var added = AddShortcutsFromPaths(new[] { target });
        StatusTextBlock.Text = added > 0
            ? $"Shortcut added: {Path.GetFileName(target)}"
            : "Shortcut already exists.";
    }

    private void RemoveShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        var shortcut = GetSelectedShortcut();
        if (shortcut is null)
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
        var shortcut = GetSelectedShortcut();
        if (shortcut is null)
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

    private ShortcutItem? GetSelectedShortcut()
    {
        if (ShortcutListBox.SelectedItem is not ShortcutListItem selected)
        {
            return null;
        }

        return _shortcutById.TryGetValue(selected.Id, out var shortcut)
            ? shortcut
            : null;
    }

    private void ShortcutListBox_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ShortcutListBox_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
        {
            return;
        }

        var added = AddShortcutsFromPaths(dropped);
        StatusTextBlock.Text = added > 0
            ? $"Shortcut(s) added: {added}"
            : "No new shortcuts were added.";
    }

    private int AddShortcutsFromPaths(IEnumerable<string> paths)
    {
        var validPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validPaths.Count == 0)
        {
            return 0;
        }

        var existing = _shortcutRepository.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        var existingPaths = new HashSet<string>(existing.Select(x => x.TargetPath), StringComparer.OrdinalIgnoreCase);
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(x => x.SortOrder) + 1;
        var added = 0;

        foreach (var path in validPaths)
        {
            if (existingPaths.Contains(path))
            {
                continue;
            }

            var shortcut = new ShortcutItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = BuildShortcutLabel(path),
                TargetPath = path,
                TargetKind = GetShortcutTargetKind(path),
                SortOrder = nextOrder++
            };

            _shortcutRepository.UpsertAsync(shortcut, CancellationToken.None).GetAwaiter().GetResult();
            existingPaths.Add(path);
            added++;
        }

        if (added > 0)
        {
            RefreshShortcuts();
        }

        return added;
    }

    private static string BuildShortcutLabel(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }

    private static ShortcutTargetKind GetShortcutTargetKind(string path)
    {
        if (Directory.Exists(path))
        {
            return ShortcutTargetKind.Folder;
        }

        if (!File.Exists(path))
        {
            return ShortcutTargetKind.Program;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            ? ShortcutTargetKind.Program
            : ShortcutTargetKind.File;
    }

    private static string GetShortcutGlyph(ShortcutTargetKind kind)
    {
        return kind switch
        {
            ShortcutTargetKind.Folder => "📁",
            ShortcutTargetKind.Program => "🚀",
            ShortcutTargetKind.File => "📄",
            _ => "•"
        };
    }

    private sealed class ShortcutListItem
    {
        public string Id { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;

        public string Glyph { get; init; } = "•";
    }

    private string? ResolveSelectedPreviewPath()
    {
        if (SourceItemListView.SelectedItem is FileItemRow sourceItem)
        {
            return sourceItem.FullPath;
        }

        if (DestinationItemListView.SelectedItem is FileItemRow destinationItem)
        {
            return destinationItem.FullPath;
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

    private void HelpButton_OnClick(object sender, RoutedEventArgs e)
    {
        var helpText =
            "How To Use Lui-P File Manager\n\n" +
            "1) Pick locations\n" +
            "- Choose a source root in Source Locations\n" +
            "- Choose a destination root in Destination Locations\n\n" +
            "2) Browse and select\n" +
            "- Browse/select source files or folders in Source Items\n" +
            "- Browse destination content in Destination Items\n" +
            "- Double-click folders to drill in\n\n" +
            "3) Copy safely\n" +
            "- Click Copy -> Destination or drag from Source Items to Destination Items\n" +
            "- Conflicts prompt Replace / Keep Both / Cancel\n" +
            "- Enable large-file checksum verification in Settings\n\n" +
            "4) Search and filters\n" +
            "- Use search/date filters in Columns B and D\n" +
            "- Rebuild Index refreshes indexed search data\n\n" +
            "5) Preview and properties\n" +
            "- Preview Selected opens a safe preview window\n" +
            "- Properties button (or Alt+Enter) shows item details\n\n" +
            "6) Undo and operation history\n" +
            "- Open Activity Log to review, retry, and undo eligible operations\n\n" +
            "Keyboard shortcuts\n" +
            "- Ctrl+C/Ctrl+X/Ctrl+V/Ctrl+A\n" +
            "- F2 rename, Delete, Shift+Delete, Enter open, Alt+Enter properties\n\n" +
            "Indexing status\n" +
            "- Top indicator is always visible\n" +
            "- Duration colors: Green < 30s, Yellow < 2m, Red >= 2m\n" +
            "- Last indexed date/time and scan duration appear under the indicator.";

        var window = new SimpleInfoWindow("Help - How To Use", helpText)
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

    private List<string> GetSelectedSourcePaths()
    {
        return SourceItemListView.SelectedItems
            .OfType<FileItemRow>()
            .Select(item => item.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> GetSelectedDestinationPaths()
    {
        return DestinationItemListView.SelectedItems
            .OfType<FileItemRow>()
            .Select(item => item.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SourceColumnVisibility_OnChanged(object sender, RoutedEventArgs e)
    {
        ApplyColumnVisibility();
    }

    private void DestinationColumnVisibility_OnChanged(object sender, RoutedEventArgs e)
    {
        ApplyColumnVisibility();
    }

    private void ApplyColumnVisibility()
    {
        if (SourceNameColumn is not null)
        {
            SourceNameColumn.Width = SourceShowNameColumnCheckBox?.IsChecked == true ? DefaultNameColumnWidth : 0;
        }

        if (SourceModifiedColumn is not null)
        {
            SourceModifiedColumn.Width = SourceShowModifiedColumnCheckBox?.IsChecked == true ? DefaultModifiedColumnWidth : 0;
        }

        if (DestinationNameColumn is not null)
        {
            DestinationNameColumn.Width = DestinationShowNameColumnCheckBox?.IsChecked == true ? DefaultNameColumnWidth : 0;
        }

        if (DestinationModifiedColumn is not null)
        {
            DestinationModifiedColumn.Width = DestinationShowModifiedColumnCheckBox?.IsChecked == true ? DefaultModifiedColumnWidth : 0;
        }
    }

    private sealed class FileItemRow
    {
        public string FullPath { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public bool IsFolder { get; init; }

        public string TypeGlyph { get; init; } = "•";

        public DateTimeOffset? ModifiedUtc { get; init; }

        public string ModifiedDisplay { get; init; } = string.Empty;
    }
}