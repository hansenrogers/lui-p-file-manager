using System.IO;
using System.Windows.Input;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
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
    private sealed class LocationListItem
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required string FullPath { get; init; }
    }

    private enum ItemSortMode
    {
        NameAscending = 0,
        NameDescending = 1,
        DateModifiedNewest = 2,
        DateModifiedOldest = 3
    }

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private static readonly ConcurrentDictionary<string, ImageSource?> ShellIconCache = new(StringComparer.OrdinalIgnoreCase);

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
    private readonly Dictionary<string, ConfiguredLocation> _sourceLocationById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConfiguredLocation> _destinationLocationById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceCurrentPathByLocationId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _destinationCurrentPathByLocationId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemSortMode> _sourceSortModeByLocationId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemSortMode> _destinationSortModeByLocationId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateFilterPreset> _sourceDateFilterByLocationId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateFilterPreset> _destinationDateFilterByLocationId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourcePathByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _destinationPathByDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutItem> _shortcutById = new(StringComparer.Ordinal);

    private DateFilterPreset _sourceDateFilter = DateFilterPreset.AllDates;
    private DateFilterPreset _destinationDateFilter = DateFilterPreset.AllDates;
    private bool _sourceShowModifiedColumn = true;
    private bool _destinationShowModifiedColumn = true;
    private bool _isSourceLocationsColumnCollapsed;
    private bool _isDestinationLocationsColumnCollapsed;
    private ItemSortMode _sourceSortMode = ItemSortMode.NameAscending;
    private ItemSortMode _destinationSortMode = ItemSortMode.NameAscending;
    private static readonly GridLength SourceLocationsExpandedWidth = new(2, GridUnitType.Star);
    private static readonly GridLength DestinationLocationsExpandedWidth = new(2, GridUnitType.Star);
    private static readonly GridLength LocationToggleColumnWidth = new(16);
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
    private bool _isApplyingSavedLocationState;
    private string? _activeSourceLocationId;
    private string? _activeDestinationLocationId;
    private TimeSpan? _lastIndexDuration;
    private UiState? _loadedUiState;
    private PreviewWindow? _previewWindow;

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
        ApplyLocationColumnVisibility();

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
            ApplySelectedLocationStateFromCurrentSelection();
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

        _loadedUiState = state;

        _sourceShowModifiedColumn = state.SourceShowModifiedColumn;
        _destinationShowModifiedColumn = state.DestinationShowModifiedColumn;
        LoadLocationViewDictionaries(state);

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
        if (_previewWindow is not null)
        {
            _previewWindow.Close();
            _previewWindow = null;
        }

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
        CaptureActiveLocationViewState();

        var state = new UiState
        {
            Width = Width,
            Height = Height,
            Left = Left,
            Top = Top,
            IsMaximized = WindowState == WindowState.Maximized,
            SelectedSourceLocationId = GetSelectedSourceLocation()?.Id,
            SelectedDestinationLocationId = GetSelectedDestinationLocation()?.Id,
            SourceShowModifiedColumn = _sourceShowModifiedColumn,
            DestinationShowModifiedColumn = _destinationShowModifiedColumn,
            SourceLocationViews = BuildLocationViewStateList(
                _sourceLocations,
                _sourceCurrentPathByLocationId,
                _sourceSortModeByLocationId,
                _sourceDateFilterByLocationId),
            DestinationLocationViews = BuildLocationViewStateList(
                _destinationLocations,
                _destinationCurrentPathByLocationId,
                _destinationSortModeByLocationId,
                _destinationDateFilterByLocationId)
        };

        _uiStateStore.Save(state);
    }

    private void LoadLocationViewDictionaries(UiState state)
    {
        _sourceCurrentPathByLocationId.Clear();
        _destinationCurrentPathByLocationId.Clear();
        _sourceSortModeByLocationId.Clear();
        _destinationSortModeByLocationId.Clear();
        _sourceDateFilterByLocationId.Clear();
        _destinationDateFilterByLocationId.Clear();

        foreach (var row in state.SourceLocationViews)
        {
            if (string.IsNullOrWhiteSpace(row.LocationId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.CurrentPath))
            {
                _sourceCurrentPathByLocationId[row.LocationId] = row.CurrentPath;
            }

            _sourceSortModeByLocationId[row.LocationId] = ParseSortMode(row.SortMode);
            _sourceDateFilterByLocationId[row.LocationId] = ParseDateFilter(row.DateFilter);
        }

        foreach (var row in state.DestinationLocationViews)
        {
            if (string.IsNullOrWhiteSpace(row.LocationId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.CurrentPath))
            {
                _destinationCurrentPathByLocationId[row.LocationId] = row.CurrentPath;
            }

            _destinationSortModeByLocationId[row.LocationId] = ParseSortMode(row.SortMode);
            _destinationDateFilterByLocationId[row.LocationId] = ParseDateFilter(row.DateFilter);
        }
    }

    private static List<LocationViewState> BuildLocationViewStateList(
        IEnumerable<ConfiguredLocation> locations,
        IReadOnlyDictionary<string, string> pathMap,
        IReadOnlyDictionary<string, ItemSortMode> sortMap,
        IReadOnlyDictionary<string, DateFilterPreset> filterMap)
    {
        var rows = new List<LocationViewState>();

        foreach (var location in locations)
        {
            pathMap.TryGetValue(location.Id, out var path);
            sortMap.TryGetValue(location.Id, out var sortMode);
            filterMap.TryGetValue(location.Id, out var dateFilter);

            rows.Add(new LocationViewState
            {
                LocationId = location.Id,
                CurrentPath = path,
                SortMode = (int)sortMode,
                DateFilter = (int)dateFilter
            });
        }

        return rows;
    }

    private static ItemSortMode ParseSortMode(int raw)
    {
        return Enum.IsDefined(typeof(ItemSortMode), raw)
            ? (ItemSortMode)raw
            : ItemSortMode.NameAscending;
    }

    private static DateFilterPreset ParseDateFilter(int raw)
    {
        return Enum.IsDefined(typeof(DateFilterPreset), raw)
            ? (DateFilterPreset)raw
            : DateFilterPreset.AllDates;
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
        _sourceLocationById.Clear();

        _destinationLocations.Clear();
        _destinationLocations.AddRange(destinations.Where(x => x.IsEnabled).OrderBy(x => x.SortOrder));
        _destinationLocationById.Clear();

        var sourceItems = new List<LocationListItem>(_sourceLocations.Count);
        foreach (var source in _sourceLocations)
        {
            _sourceLocationById[source.Id] = source;
            sourceItems.Add(new LocationListItem
            {
                Id = source.Id,
                DisplayName = source.Name,
                FullPath = source.Path
            });
        }

        var destinationItems = new List<LocationListItem>(_destinationLocations.Count);
        foreach (var destination in _destinationLocations)
        {
            _destinationLocationById[destination.Id] = destination;
            destinationItems.Add(new LocationListItem
            {
                Id = destination.Id,
                DisplayName = destination.Name,
                FullPath = destination.Path
            });
        }

        SourceLocationListView.ItemsSource = sourceItems;
        DestinationLocationListView.ItemsSource = destinationItems;

        PruneStaleLocationViewState();

        var restoredSourceId = _loadedUiState?.SelectedSourceLocationId;
        var restoredDestinationId = _loadedUiState?.SelectedDestinationLocationId;

        if (SourceLocationListView.Items.Count > 0 && !SelectLocationById(SourceLocationListView, restoredSourceId))
        {
            SourceLocationListView.SelectedIndex = 0;
        }

        if (DestinationLocationListView.Items.Count > 0 && !SelectLocationById(DestinationLocationListView, restoredDestinationId))
        {
            DestinationLocationListView.SelectedIndex = 0;
        }
    }

    private void PruneStaleLocationViewState()
    {
        var sourceIds = _sourceLocations.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var destinationIds = _destinationLocations.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        RemoveStaleKeys(_sourceCurrentPathByLocationId, sourceIds);
        RemoveStaleKeys(_sourceSortModeByLocationId, sourceIds);
        RemoveStaleKeys(_sourceDateFilterByLocationId, sourceIds);

        RemoveStaleKeys(_destinationCurrentPathByLocationId, destinationIds);
        RemoveStaleKeys(_destinationSortModeByLocationId, destinationIds);
        RemoveStaleKeys(_destinationDateFilterByLocationId, destinationIds);
    }

    private static void RemoveStaleKeys<T>(Dictionary<string, T> map, IReadOnlySet<string> validIds)
    {
        var stale = map.Keys.Where(id => !validIds.Contains(id)).ToList();
        foreach (var id in stale)
        {
            map.Remove(id);
        }
    }

    private static bool SelectLocationById(System.Windows.Controls.ListView listView, string? locationId)
    {
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return false;
        }

        var row = listView.Items
            .OfType<LocationListItem>()
            .FirstOrDefault(item => string.Equals(item.Id, locationId, StringComparison.Ordinal));

        if (row is null)
        {
            return false;
        }

        listView.SelectedItem = row;
        return true;
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
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentSourcePath) || !Directory.Exists(_currentSourcePath) || !IsPathWithinRoot(_currentSourcePath, selectedRoot))
        {
            _currentSourcePath = selectedRoot;
        }

        if (!string.IsNullOrWhiteSpace(_activeSourceLocationId))
        {
            _sourceCurrentPathByLocationId[_activeSourceLocationId] = _currentSourcePath;
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
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentDestinationPath) || !Directory.Exists(_currentDestinationPath) || !IsPathWithinRoot(_currentDestinationPath, selectedRoot))
        {
            _currentDestinationPath = selectedRoot;
        }

        if (!string.IsNullOrWhiteSpace(_activeDestinationLocationId))
        {
            _destinationCurrentPathByLocationId[_activeDestinationLocationId] = _currentDestinationPath;
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
        return GetSelectedLocation(SourceLocationListView, _sourceLocationById);
    }

    private ConfiguredLocation? GetSelectedDestinationLocation()
    {
        return GetSelectedLocation(DestinationLocationListView, _destinationLocationById);
    }

    private static ConfiguredLocation? GetSelectedLocation(System.Windows.Controls.ListView listView, IReadOnlyDictionary<string, ConfiguredLocation> map)
    {
        if (listView is null)
        {
            return null;
        }

        if (listView.SelectedItem is not LocationListItem selected)
        {
            return null;
        }

        return map.TryGetValue(selected.Id, out var configured) ? configured : null;
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
            TypeIcon = GetWindowsShellIcon(fullPath, isFolder, ShortcutTargetKind.File),
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

        return modifiedUtc.Value.ToLocalTime().ToString("MM-dd-yyyy h:mm tt");
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
        if (!_isUiInitialized || _isApplyingSavedLocationState)
        {
            return;
        }

        _sourceDateFilter = ParseDateFilterPreset(SourceDateFilterComboBox.SelectedIndex);
        if (!string.IsNullOrWhiteSpace(_activeSourceLocationId))
        {
            _sourceDateFilterByLocationId[_activeSourceLocationId] = _sourceDateFilter;
        }
        RefreshSourceItems();
    }

    private void DestinationDateFilterComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized || _isApplyingSavedLocationState)
        {
            return;
        }

        _destinationDateFilter = ParseDateFilterPreset(DestinationDateFilterComboBox.SelectedIndex);
        if (!string.IsNullOrWhiteSpace(_activeDestinationLocationId))
        {
            _destinationDateFilterByLocationId[_activeDestinationLocationId] = _destinationDateFilter;
        }
        RefreshDestinationItems();
    }

    private void SourceItemColumnHeader_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        if (e.OriginalSource is not GridViewColumnHeader header || header.Column?.Header is null)
        {
            return;
        }

        var headerText = header.Column.Header.ToString();
        if (string.IsNullOrWhiteSpace(headerText))
        {
            return;
        }

        switch (headerText)
        {
            case "Name":
                _sourceSortMode = _sourceSortMode == ItemSortMode.NameAscending
                    ? ItemSortMode.NameDescending
                    : ItemSortMode.NameAscending;
                break;
            case "Modified":
                _sourceSortMode = _sourceSortMode == ItemSortMode.DateModifiedNewest
                    ? ItemSortMode.DateModifiedOldest
                    : ItemSortMode.DateModifiedNewest;
                break;
            default:
                return;
        }

        if (!string.IsNullOrWhiteSpace(_activeSourceLocationId))
        {
            _sourceSortModeByLocationId[_activeSourceLocationId] = _sourceSortMode;
        }

        RefreshSourceItems();
    }

    private void DestinationItemColumnHeader_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        if (e.OriginalSource is not GridViewColumnHeader header || header.Column?.Header is null)
        {
            return;
        }

        var headerText = header.Column.Header.ToString();
        if (string.IsNullOrWhiteSpace(headerText))
        {
            return;
        }

        switch (headerText)
        {
            case "Name":
                _destinationSortMode = _destinationSortMode == ItemSortMode.NameAscending
                    ? ItemSortMode.NameDescending
                    : ItemSortMode.NameAscending;
                break;
            case "Modified":
                _destinationSortMode = _destinationSortMode == ItemSortMode.DateModifiedNewest
                    ? ItemSortMode.DateModifiedOldest
                    : ItemSortMode.DateModifiedNewest;
                break;
            default:
                return;
        }

        if (!string.IsNullOrWhiteSpace(_activeDestinationLocationId))
        {
            _destinationSortModeByLocationId[_activeDestinationLocationId] = _destinationSortMode;
        }

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

    private static int ToDateFilterIndex(DateFilterPreset preset)
    {
        return preset switch
        {
            DateFilterPreset.Today => 1,
            DateFilterPreset.Last7Days => 2,
            DateFilterPreset.Last30Days => 3,
            DateFilterPreset.ThisYear => 4,
            _ => 0
        };
    }

    private void ApplySourceDateFilterSelection()
    {
        if (SourceDateFilterComboBox is null)
        {
            return;
        }

        _isApplyingSavedLocationState = true;
        try
        {
            SourceDateFilterComboBox.SelectedIndex = ToDateFilterIndex(_sourceDateFilter);
        }
        finally
        {
            _isApplyingSavedLocationState = false;
        }
    }

    private void ApplyDestinationDateFilterSelection()
    {
        if (DestinationDateFilterComboBox is null)
        {
            return;
        }

        _isApplyingSavedLocationState = true;
        try
        {
            DestinationDateFilterComboBox.SelectedIndex = ToDateFilterIndex(_destinationDateFilter);
        }
        finally
        {
            _isApplyingSavedLocationState = false;
        }
    }

    private static string ResolveStoredPath(IReadOnlyDictionary<string, string> map, string locationId, string fallbackRoot)
    {
        if (map.TryGetValue(locationId, out var storedPath)
            && !string.IsNullOrWhiteSpace(storedPath)
            && Directory.Exists(storedPath)
            && IsPathWithinRoot(storedPath, fallbackRoot))
        {
            return storedPath;
        }

        return fallbackRoot;
    }

    private void SaveSourceViewStateForActiveLocation()
    {
        if (string.IsNullOrWhiteSpace(_activeSourceLocationId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_currentSourcePath))
        {
            _sourceCurrentPathByLocationId[_activeSourceLocationId] = _currentSourcePath;
        }

        _sourceSortModeByLocationId[_activeSourceLocationId] = _sourceSortMode;
        _sourceDateFilterByLocationId[_activeSourceLocationId] = _sourceDateFilter;
    }

    private void SaveDestinationViewStateForActiveLocation()
    {
        if (string.IsNullOrWhiteSpace(_activeDestinationLocationId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_currentDestinationPath))
        {
            _destinationCurrentPathByLocationId[_activeDestinationLocationId] = _currentDestinationPath;
        }

        _destinationSortModeByLocationId[_activeDestinationLocationId] = _destinationSortMode;
        _destinationDateFilterByLocationId[_activeDestinationLocationId] = _destinationDateFilter;
    }

    private void CaptureActiveLocationViewState()
    {
        SaveSourceViewStateForActiveLocation();
        SaveDestinationViewStateForActiveLocation();
    }

    private void ApplySelectedLocationStateFromCurrentSelection()
    {
        var source = GetSelectedSourceLocation();
        _activeSourceLocationId = source?.Id;
        if (source is not null)
        {
            _sourceSortMode = _sourceSortModeByLocationId.TryGetValue(source.Id, out var storedSort)
                ? storedSort
                : ItemSortMode.NameAscending;
            _sourceDateFilter = _sourceDateFilterByLocationId.TryGetValue(source.Id, out var storedFilter)
                ? storedFilter
                : DateFilterPreset.AllDates;
            _currentSourcePath = ResolveStoredPath(_sourceCurrentPathByLocationId, source.Id, source.Path);
            ApplySourceDateFilterSelection();
            if (SelectedSourceRootTextBlock is not null)
            {
                SelectedSourceRootTextBlock.Text = $"Selected: {source.Name}";
                SelectedSourceRootTextBlock.ToolTip = source.Path;
            }
        }

        var destination = GetSelectedDestinationLocation();
        _activeDestinationLocationId = destination?.Id;
        if (destination is not null)
        {
            _destinationSortMode = _destinationSortModeByLocationId.TryGetValue(destination.Id, out var storedSort)
                ? storedSort
                : ItemSortMode.NameAscending;
            _destinationDateFilter = _destinationDateFilterByLocationId.TryGetValue(destination.Id, out var storedFilter)
                ? storedFilter
                : DateFilterPreset.AllDates;
            _currentDestinationPath = ResolveStoredPath(_destinationCurrentPathByLocationId, destination.Id, destination.Path);
            ApplyDestinationDateFilterSelection();
            if (SelectedDestinationRootTextBlock is not null)
            {
                SelectedDestinationRootTextBlock.Text = $"Selected: {destination.Name}";
                SelectedDestinationRootTextBlock.ToolTip = destination.Path;
            }
        }
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

        SaveSourceViewStateForActiveLocation();

        var selected = GetSelectedSourceLocation();
        if (selected is not null)
        {
            _activeSourceLocationId = selected.Id;
            _sourceSortMode = _sourceSortModeByLocationId.TryGetValue(selected.Id, out var storedSortMode)
                ? storedSortMode
                : ItemSortMode.NameAscending;
            _sourceDateFilter = _sourceDateFilterByLocationId.TryGetValue(selected.Id, out var storedFilter)
                ? storedFilter
                : DateFilterPreset.AllDates;
            _currentSourcePath = ResolveStoredPath(_sourceCurrentPathByLocationId, selected.Id, selected.Path);
            ApplySourceDateFilterSelection();
        }
        else
        {
            _activeSourceLocationId = null;
            _currentSourcePath = null;
        }

        if (SelectedSourceRootTextBlock is not null)
        {
            SelectedSourceRootTextBlock.Text = selected is null
                ? "Selected: none"
                : $"Selected: {selected.Name}";
            SelectedSourceRootTextBlock.ToolTip = selected?.Path;
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

        SaveDestinationViewStateForActiveLocation();

        var selected = GetSelectedDestinationLocation();
        if (selected is not null)
        {
            _activeDestinationLocationId = selected.Id;
            _destinationSortMode = _destinationSortModeByLocationId.TryGetValue(selected.Id, out var storedSortMode)
                ? storedSortMode
                : ItemSortMode.NameAscending;
            _destinationDateFilter = _destinationDateFilterByLocationId.TryGetValue(selected.Id, out var storedFilter)
                ? storedFilter
                : DateFilterPreset.AllDates;
            _currentDestinationPath = ResolveStoredPath(_destinationCurrentPathByLocationId, selected.Id, selected.Path);
            ApplyDestinationDateFilterSelection();
        }
        else
        {
            _activeDestinationLocationId = null;
            _currentDestinationPath = null;
        }

        if (SelectedDestinationRootTextBlock is not null)
        {
            SelectedDestinationRootTextBlock.Text = selected is null
                ? "Selected: none"
                : $"Selected: {selected.Name}";
            SelectedDestinationRootTextBlock.ToolTip = selected?.Path;
        }
        RefreshDestinationItems();
        UpdateTransferContextBar();
    }

    private void SourceAddLocationButton_OnClick(object sender, RoutedEventArgs e)
    {
        AddLocationByBrowsing(LocationKind.Source);
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
        AddLocationByBrowsing(LocationKind.Destination);
    }

    private void AddLocationByBrowsing(LocationKind kind)
    {
        var selected = kind == LocationKind.Source
            ? GetSelectedSourceLocation()
            : GetSelectedDestinationLocation();

        var initialDirectory = selected?.Path;
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
        {
            initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = kind == LocationKind.Source ? "Select Source Folder" : "Select Destination Folder",
            InitialDirectory = initialDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var selectedPath = Path.GetFullPath(dialog.FolderName);
        var existing = _locationRepository.GetByKindAsync(kind, CancellationToken.None).GetAwaiter().GetResult();
        var existingMatch = existing.FirstOrDefault(x => PathsEqual(x.Path, selectedPath));

        if (existingMatch is null)
        {
            var trimmed = selectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folderName = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                folderName = selectedPath;
            }

            var newLocation = new ConfiguredLocation
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = folderName,
                Path = selectedPath,
                Kind = kind,
                IsEnabled = true,
                SortOrder = existing.Count == 0 ? 1 : existing.Max(x => x.SortOrder) + 1
            };

            _locationRepository.UpsertAsync(newLocation, CancellationToken.None).GetAwaiter().GetResult();
            existingMatch = newLocation;
        }

        InitializeLocationsFromRepository();
        ConfigureIndexWatchers();

        var didSelect = SelectLocationByPath(kind, existingMatch.Path);
        if (!didSelect)
        {
            RefreshAllViews();
        }

        _ = EnsureIndexBuiltAsync();
        StatusTextBlock.Text = kind == LocationKind.Source
            ? $"Source location selected: {existingMatch.Name}"
            : $"Destination location selected: {existingMatch.Name}";
    }

    private bool SelectLocationByPath(LocationKind kind, string path)
    {
        if (kind == LocationKind.Source)
        {
            var match = _sourceLocationById.Values.FirstOrDefault(location => PathsEqual(location.Path, path));
            if (match is not null)
            {
                SourceLocationListView.SelectedItem = SourceLocationListView.Items
                    .OfType<LocationListItem>()
                    .FirstOrDefault(item => string.Equals(item.Id, match.Id, StringComparison.Ordinal));
                return true;
            }

            return false;
        }

        var destinationMatch = _destinationLocationById.Values.FirstOrDefault(location => PathsEqual(location.Path, path));
        if (destinationMatch is not null)
        {
            DestinationLocationListView.SelectedItem = DestinationLocationListView.Items
                .OfType<LocationListItem>()
                .FirstOrDefault(item => string.Equals(item.Id, destinationMatch.Id, StringComparison.Ordinal));
            return true;
        }

        return false;
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
            if (!string.IsNullOrWhiteSpace(_activeSourceLocationId))
            {
                _sourceCurrentPathByLocationId[_activeSourceLocationId] = _currentSourcePath;
            }
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
            if (!string.IsNullOrWhiteSpace(_activeDestinationLocationId))
            {
                _destinationCurrentPathByLocationId[_activeDestinationLocationId] = _currentDestinationPath;
            }
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
        var selectedFiles = GetActiveSelectedPreviewFiles();
        if (selectedFiles.Count == 0)
        {
            MessageBox.Show(this, "Select a file from Source Items or Destination Items first.", "Preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selectedFiles.Count >= 2)
        {
            OpenOrReusePreviewWindow(selectedFiles[0], selectedFiles[1]);
            return;
        }

        OpenOrReusePreviewWindow(selectedFiles[0]);
    }

    private void CompareSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selectedFiles = GetActiveSelectedPreviewFiles();
        if (selectedFiles.Count < 2)
        {
            MessageBox.Show(this, "Select two files to compare.", "Compare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenOrReusePreviewWindow(selectedFiles[0], selectedFiles[1]);
    }

    private void OpenOrReusePreviewWindow(string primaryPath, string? secondaryPath = null)
    {
        if (_previewWindow is null || !_previewWindow.IsLoaded)
        {
            _previewWindow = new PreviewWindow(primaryPath, secondaryPath)
            {
                Owner = this
            };
            _previewWindow.Closed += (_, _) => _previewWindow = null;
            _previewWindow.Show();
            return;
        }

        _previewWindow.SetPaths(primaryPath, secondaryPath);
        if (_previewWindow.WindowState == WindowState.Minimized)
        {
            _previewWindow.WindowState = WindowState.Normal;
        }

        _previewWindow.Activate();
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

    private void SourceItemListView_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView listView)
        {
            ShowColumnHeaderMenuForList(listView, isSource: true, e);
        }
    }

    private void DestinationItemListView_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView listView)
        {
            ShowColumnHeaderMenuForList(listView, isSource: false, e);
        }
    }

    private void ShowColumnHeaderMenuForList(ListView listView, bool isSource, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var header = FindVisualParent<GridViewColumnHeader>(source);
        if (header is null)
        {
            return;
        }

        var isModifiedVisible = isSource ? _sourceShowModifiedColumn : _destinationShowModifiedColumn;

        var toggleModified = new MenuItem
        {
            Header = isModifiedVisible ? "Hide Modified Column" : "Show Modified Column",
            IsCheckable = true,
            IsChecked = isModifiedVisible
        };
        toggleModified.Click += (_, _) =>
        {
            if (isSource)
            {
                _sourceShowModifiedColumn = !_sourceShowModifiedColumn;
            }
            else
            {
                _destinationShowModifiedColumn = !_destinationShowModifiedColumn;
            }

            ApplyColumnVisibility();
        };

        var nameInfo = new MenuItem
        {
            Header = "Name column is always visible",
            IsEnabled = false
        };

        var menu = new ContextMenu();
        menu.Items.Add(toggleModified);
        menu.Items.Add(new Separator());
        menu.Items.Add(nameInfo);
        menu.PlacementTarget = header;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void DeleteSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var sourceSelectedPaths = GetSelectedSourcePaths();
        var destinationSelectedPaths = GetSelectedDestinationPaths();

        var deleteFromSource = SourceItemListView.IsKeyboardFocusWithin
            ? sourceSelectedPaths.Count > 0
            : DestinationItemListView.IsKeyboardFocusWithin
                ? false
                : sourceSelectedPaths.Count > 0;

        IReadOnlyList<string> selectedFullPaths;
        ConfiguredLocation? selectedLocation;

        if (deleteFromSource)
        {
            selectedFullPaths = sourceSelectedPaths;
            selectedLocation = GetSelectedSourceLocation();
        }
        else if (destinationSelectedPaths.Count > 0)
        {
            selectedFullPaths = destinationSelectedPaths;
            selectedLocation = GetSelectedDestinationLocation();
        }
        else
        {
            MessageBox.Show(this, "Select one or more items to delete.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selectedLocation is null)
        {
            MessageBox.Show(this, "Select a source or destination location first.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var totalBytes = TryComputeTotalBytes(selectedFullPaths);

        var confirmation = new DeleteConfirmationWindow(selectedLocation.Path, selectedFullPaths.Count, totalBytes)
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

    private void CreateFolderButton_OnClick(object sender, RoutedEventArgs e)
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
        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "Folder name contains invalid characters.", "Create Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targetPath = Path.Combine(GetCurrentDestinationPath(destination), folderName);

        try
        {
            Directory.CreateDirectory(targetPath);
            DestinationSearchTextBox.Text = string.Empty;
            RefreshDestinationItems();
            SelectDestinationItemByPath(targetPath);
            _ = EnsureIndexBuiltAsync();
            StatusTextBlock.Text = $"Folder ready: {targetPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Create folder failed.\n\n{ex.Message}", "Create Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectDestinationItemByPath(string fullPath)
    {
        if (DestinationItemListView.ItemsSource is not IEnumerable<FileItemRow> rows)
        {
            return;
        }

        var selected = rows.FirstOrDefault(x => PathsEqual(x.FullPath, fullPath));
        if (selected is null)
        {
            return;
        }

        DestinationItemListView.SelectedItem = selected;
        DestinationItemListView.ScrollIntoView(selected);
    }

    private void UpdateSourceNavigationUi(string rootPath, string? currentPath)
    {
        if (SourceCurrentPathTextBlock is not null)
        {
            SourceCurrentPathTextBlock.Text = currentPath ?? rootPath;
        }
    }

    private void UpdateDestinationNavigationUi(string rootPath, string? currentPath)
    {
        if (DestinationCurrentPathTextBlock is not null)
        {
            DestinationCurrentPathTextBlock.Text = currentPath ?? rootPath;
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
        _isSourceLocationsColumnCollapsed = false;
        _isDestinationLocationsColumnCollapsed = false;
        ApplyLocationColumnVisibility();
        StatusTextBlock.Text = "Layout reset to default window size.";
        SaveUiState();
    }

    private void ToggleSourceLocationsColumnButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isSourceLocationsColumnCollapsed = !_isSourceLocationsColumnCollapsed;
        ApplyLocationColumnVisibility();
    }

    private void ToggleDestinationLocationsColumnButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isDestinationLocationsColumnCollapsed = !_isDestinationLocationsColumnCollapsed;
        ApplyLocationColumnVisibility();
    }

    private void ApplyLocationColumnVisibility()
    {
        if (SourceLocationsColumn is not null)
        {
            SourceLocationsColumn.MinWidth = _isSourceLocationsColumnCollapsed ? 0 : 220;
            SourceLocationsColumn.Width = _isSourceLocationsColumnCollapsed ? new GridLength(0) : SourceLocationsExpandedWidth;
        }

        if (SourceLocationSplitterColumn is not null)
        {
            SourceLocationSplitterColumn.Width = LocationToggleColumnWidth;
        }

        if (SourceLocationsGroupBox is not null)
        {
            SourceLocationsGroupBox.Visibility = _isSourceLocationsColumnCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        if (SourceLocationsGridSplitter is not null)
        {
            SourceLocationsGridSplitter.Visibility = _isSourceLocationsColumnCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        if (SourceLocationsCollapseButton is not null)
        {
            SourceLocationsCollapseButton.Content = _isSourceLocationsColumnCollapsed ? ">" : "<";
            SourceLocationsCollapseButton.ToolTip = _isSourceLocationsColumnCollapsed ? "Expand source locations" : "Collapse source locations";
        }

        if (DestinationLocationsColumn is not null)
        {
            DestinationLocationsColumn.MinWidth = _isDestinationLocationsColumnCollapsed ? 0 : 220;
            DestinationLocationsColumn.Width = _isDestinationLocationsColumnCollapsed ? new GridLength(0) : DestinationLocationsExpandedWidth;
        }

        if (DestinationLocationSplitterColumn is not null)
        {
            DestinationLocationSplitterColumn.Width = LocationToggleColumnWidth;
        }

        if (DestinationLocationsGroupBox is not null)
        {
            DestinationLocationsGroupBox.Visibility = _isDestinationLocationsColumnCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        if (DestinationLocationsGridSplitter is not null)
        {
            DestinationLocationsGridSplitter.Visibility = _isDestinationLocationsColumnCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        if (DestinationLocationsCollapseButton is not null)
        {
            DestinationLocationsCollapseButton.Content = _isDestinationLocationsColumnCollapsed ? "<" : ">";
            DestinationLocationsCollapseButton.ToolTip = _isDestinationLocationsColumnCollapsed ? "Expand destination locations" : "Collapse destination locations";
        }
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
            HeaderIndexingTextBlock.Text = isRunning ? "Indexing..." : "Indexing";
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
                HeaderLastIndexedTextBlock.Text = "Last indexed: not yet";
                HeaderLastIndexedTextBlock.Foreground = System.Windows.Media.Brushes.SteelBlue;
                return;
            }

            var localTime = latest.ToLocalTime();
            HeaderLastIndexedTextBlock.Text = $"Last indexed: {localTime:MM-dd-yyyy h:mm:ss tt}";
            HeaderLastIndexedTextBlock.Foreground = System.Windows.Media.Brushes.SteelBlue;
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
            TransferDestinationTextBlock.Text = "Going To: No destination selected";
        }
        else
        {
            var effectiveDestinationPath = GetCurrentDestinationPath(destination);
            TransferDestinationTextBlock.Text = $"Going To: {effectiveDestinationPath}";
        }

        if (SourceItemListView.SelectedItems.Count == 0)
        {
            TransferSelectedTextBlock.Text = "Moving: None";
            return;
        }

        var sourceLocation = GetSelectedSourceLocation();
        if (sourceLocation is null)
        {
            TransferSelectedTextBlock.Text = $"Moving: {SourceItemListView.SelectedItems.Count} item(s) selected";
            return;
        }

        if (SourceItemListView.SelectedItems.Count == 1 && SourceItemListView.SelectedItem is FileItemRow selectedItem)
        {
            TransferSelectedTextBlock.Text = $"Moving: {selectedItem.FullPath}";
            return;
        }

        TransferSelectedTextBlock.Text = $"Moving: {SourceItemListView.SelectedItems.Count} item(s) selected";
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
                    Icon = GetWindowsShellIcon(shortcut.TargetPath, shortcut.TargetKind == ShortcutTargetKind.Folder, shortcut.TargetKind, smallIcon: false)
                });
            }
            catch
            {
                items.Add(new ShortcutListItem
                {
                    Id = shortcut.Id,
                    Label = shortcut.Label,
                    FullPath = shortcut.TargetPath,
                    Icon = GetWindowsShellIcon(shortcut.TargetPath, false, ShortcutTargetKind.File, smallIcon: false)
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

    private void ShortcutListBox_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        item.IsSelected = true;
        item.Focus();
    }

    private void ShortcutListBox_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        item.IsSelected = true;
        item.Focus();

        if (GetSelectedShortcut() is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = item,
            Placement = PlacementMode.MousePoint
        };

        menu.Items.Add(new MenuItem { Header = "Rename Shortcut" });
        ((MenuItem)menu.Items[0]).Click += ShortcutRenameMenuItem_OnClick;

        menu.Items.Add(new MenuItem { Header = "Open" });
        ((MenuItem)menu.Items[1]).Click += OpenShortcutButton_OnClick;

        menu.Items.Add(new MenuItem { Header = "Remove" });
        ((MenuItem)menu.Items[2]).Click += RemoveShortcutButton_OnClick;

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ShortcutRenameMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var shortcut = GetSelectedShortcut();
        if (shortcut is null)
        {
            MessageBox.Show(this, "Select a shortcut to rename.", "Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var prompt = new InputPromptWindow("Rename Shortcut", "Enter shortcut display name:", shortcut.Label)
        {
            Owner = this
        };

        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Result))
        {
            return;
        }

        var newLabel = prompt.Result.Trim();
        if (string.Equals(newLabel, shortcut.Label, StringComparison.Ordinal))
        {
            return;
        }

        shortcut.Label = newLabel;
        _shortcutRepository.UpsertAsync(shortcut, CancellationToken.None).GetAwaiter().GetResult();
        RefreshShortcuts();
        StatusTextBlock.Text = $"Shortcut renamed: {newLabel}";
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

    private static ImageSource? GetWindowsShellIcon(string path, bool isFolder, ShortcutTargetKind kind, bool smallIcon = true)
    {
        var key = isFolder
            ? $"folder:{(smallIcon ? "small" : "large")}"
            : kind == ShortcutTargetKind.Program
                ? $"program:{path}:{(smallIcon ? "small" : "large")}"
                : $"file:{Path.GetExtension(path).ToLowerInvariant()}:{(smallIcon ? "small" : "large")}";

        return ShellIconCache.GetOrAdd(key, _ => LoadWindowsShellIcon(path, isFolder, smallIcon));
    }

    private static ImageSource? LoadWindowsShellIcon(string path, bool isFolder, bool smallIcon)
    {
        var attributes = isFolder ? FileAttributeDirectory : FileAttributeNormal;
        var iconSizeFlag = smallIcon ? ShgfiSmallIcon : ShgfiLargeIcon;
        var flags = ShgfiIcon | iconSizeFlag | ShgfiUseFileAttributes;

        var queryPath = path;
        if (string.IsNullOrWhiteSpace(queryPath))
        {
            queryPath = isFolder ? "folder" : "file";
        }

        var result = SHGetFileInfo(queryPath, attributes, out var shFileInfo, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || shFileInfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var icon = Imaging.CreateBitmapSourceFromHIcon(
                shFileInfo.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(smallIcon ? 16 : 48, smallIcon ? 16 : 48));
            icon.Freeze();
            return icon;
        }
        finally
        {
            _ = DestroyIcon(shFileInfo.hIcon);
        }
    }

    private sealed class ShortcutListItem
    {
        public string Id { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;

        public ImageSource? Icon { get; init; }
    }

    private string? ResolveSelectedPreviewPath()
    {
        var selectedFiles = GetActiveSelectedPreviewFiles();
        if (selectedFiles.Count > 0)
        {
            return selectedFiles[0];
        }

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

    private List<string> GetActiveSelectedPreviewFiles()
    {
        List<string> candidates;

        if (SourceItemListView.IsKeyboardFocusWithin)
        {
            candidates = GetSelectedSourcePaths();
        }
        else if (DestinationItemListView.IsKeyboardFocusWithin)
        {
            candidates = GetSelectedDestinationPaths();
        }
        else if (SourceItemListView.SelectedItems.Count > 0)
        {
            candidates = GetSelectedSourcePaths();
        }
        else
        {
            candidates = GetSelectedDestinationPaths();
        }

        return candidates
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
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
            "- Rebuild Index refreshes indexed search data\n" +
            "- Click Name/Modified headers to sort items\n" +
            "- Right-click item headers to show/hide columns\n\n" +
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

    private void ApplyColumnVisibility()
    {
        if (SourceNameColumn is not null)
        {
            if (SourceNameColumn.Width <= 0)
            {
                SourceNameColumn.Width = DefaultNameColumnWidth;
            }
        }

        if (SourceModifiedColumn is not null)
        {
            SourceModifiedColumn.Width = _sourceShowModifiedColumn
                ? (SourceModifiedColumn.Width <= 0 ? DefaultModifiedColumnWidth : SourceModifiedColumn.Width)
                : 0;
        }

        if (DestinationNameColumn is not null)
        {
            if (DestinationNameColumn.Width <= 0)
            {
                DestinationNameColumn.Width = DefaultNameColumnWidth;
            }
        }

        if (DestinationModifiedColumn is not null)
        {
            DestinationModifiedColumn.Width = _destinationShowModifiedColumn
                ? (DestinationModifiedColumn.Width <= 0 ? DefaultModifiedColumnWidth : DestinationModifiedColumn.Width)
                : 0;
        }
    }

    private sealed class FileItemRow
    {
        public string FullPath { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public bool IsFolder { get; init; }

        public ImageSource? TypeIcon { get; init; }

        public DateTimeOffset? ModifiedUtc { get; init; }

        public string ModifiedDisplay { get; init; } = string.Empty;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        out SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}