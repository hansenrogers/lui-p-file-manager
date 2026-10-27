using System.Data;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LuiP.FileManager.App.Persistence;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using LuiP.FileManager.Infrastructure.Services;

namespace LuiP.FileManager.App;

public partial class SyncBoardWindow : Window
{
    private const string PersonColumnName = "__person";

    private readonly IFileOperationService _fileOperationService;
    private readonly AppPreferencesStore _preferencesStore;
    private readonly IAppPathsProvider _appPathsProvider;
    private readonly SyncBoardStore _store;
    private readonly DispatcherTimer _autoSyncDebounceTimer;
    private readonly List<SyncBoardJobColumn> _jobs = new();

    private DataTable _matrixTable = new();

    private string _sourceRootPath = string.Empty;
    private string _destinationRootPath = string.Empty;
    private DateTimeOffset? _lastSyncUtc;
    private bool _isSyncRunning;
    private bool _pendingAutoSyncRequested;
    private bool _isSourceWatcherActive;
    private bool _strictProjectFolderGuardEnabled = true;
    private DateTimeOffset? _lastWatcherEventUtc;
    private string _lastWatcherEventSummary = "No events yet.";
    private string _lastAutoSyncTriggerReason = "None yet.";
    private string _lastSyncStatus = "Not synced yet.";
    private FileSystemWatcher? _sourceWatcher;
    private string? _selectedJobId;

    public SyncBoardWindow(IFileOperationService fileOperationService, AppPreferencesStore preferencesStore)
    {
        _fileOperationService = fileOperationService;
        _preferencesStore = preferencesStore;
        _appPathsProvider = new WindowsAppPathsProvider();
        _store = new SyncBoardStore(_appPathsProvider);
        _autoSyncDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSyncDebounceTimer.Tick += AutoSyncDebounceTimer_OnTick;

        InitializeComponent();

        LoadState();
        UpdateHeaderTexts();
        ConfigureSourceWatcher();
        Closed += SyncBoardWindow_OnClosed;
    }

    private void LoadState()
    {
        var state = _store.Load();
        _sourceRootPath = state.SourceRootPath;
        _destinationRootPath = state.DestinationRootPath;
        _lastSyncUtc = state.LastSyncUtc;
        _lastSyncStatus = string.IsNullOrWhiteSpace(state.LastSyncStatus) ? "Not synced yet." : state.LastSyncStatus;
        _strictProjectFolderGuardEnabled = state.StrictProjectFolderGuardEnabled;
        StrictProjectGuardCheckBox.IsChecked = _strictProjectFolderGuardEnabled;

        _jobs.Clear();
        _jobs.AddRange(state.Jobs.Where(job => !string.IsNullOrWhiteSpace(job.JobId)));

        if (_jobs.Count == 0)
        {
            _jobs.Add(new SyncBoardJobColumn
            {
                JobId = Guid.NewGuid().ToString("N"),
                JobName = "NEW JOB",
                ProjectNumber = string.Empty
            });
        }

        BuildMatrixColumns();

        foreach (var person in state.People)
        {
            if (string.IsNullOrWhiteSpace(person.PersonName))
            {
                continue;
            }

            var row = _matrixTable.NewRow();
            row[PersonColumnName] = person.PersonName.Trim();
            foreach (var job in _jobs)
            {
                var enabled = person.JobChecks.TryGetValue(job.JobId, out var checkedValue) && checkedValue;
                row[job.JobId] = enabled;
            }

            _matrixTable.Rows.Add(row);
        }

        if (_matrixTable.Rows.Count == 0)
        {
            AddPersonRow("DB NEW");
        }

        RowsDataGrid.ItemsSource = _matrixTable.DefaultView;
        RebuildGridColumns();
    }

    private void SaveState()
    {
        var people = new List<SyncBoardPersonRow>(_matrixTable.Rows.Count);
        foreach (DataRow row in _matrixTable.Rows)
        {
            var personName = row[PersonColumnName]?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(personName))
            {
                continue;
            }

            var checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in _jobs)
            {
                checks[job.JobId] = row[job.JobId] is bool enabled && enabled;
            }

            people.Add(new SyncBoardPersonRow
            {
                PersonName = personName,
                JobChecks = checks
            });
        }

        var state = new SyncBoardState
        {
            SourceRootPath = _sourceRootPath,
            DestinationRootPath = _destinationRootPath,
            ProjectNumber = string.Empty,
            LastSyncUtc = _lastSyncUtc,
            LastSyncStatus = _lastSyncStatus,
            StrictProjectFolderGuardEnabled = _strictProjectFolderGuardEnabled,
            People = people,
            Jobs = _jobs.ToList()
        };

        _store.Save(state);
        UpdateDestinationPreviewText();
    }

    private void BuildMatrixColumns()
    {
        _matrixTable = new DataTable("SyncBoard");
        _matrixTable.Columns.Add(PersonColumnName, typeof(string));

        foreach (var job in _jobs)
        {
            _matrixTable.Columns.Add(job.JobId, typeof(bool));
        }
    }

    private void RebuildGridColumns()
    {
        RowsDataGrid.Columns.Clear();

        var personColumn = new DataGridTextColumn
        {
            Header = "Persons",
            SortMemberPath = PersonColumnName,
            Width = new DataGridLength(220),
            Binding = new Binding($"[{PersonColumnName}]")
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            }
        };
        RowsDataGrid.Columns.Add(personColumn);

        foreach (var job in _jobs)
        {
            var header = new TextBlock
            {
                Text = $"{job.JobName}\n{job.ProjectNumber}",
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold
            };

            if (string.Equals(_selectedJobId, job.JobId, StringComparison.OrdinalIgnoreCase))
            {
                header.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 122, 171));
                header.Foreground = System.Windows.Media.Brushes.White;
                header.Padding = new Thickness(4, 2, 4, 2);
            }

            var jobColumn = new DataGridCheckBoxColumn
            {
                Header = header,
                SortMemberPath = job.JobId,
                Width = new DataGridLength(145),
                Binding = new Binding($"[{job.JobId}]")
                {
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                }
            };

            RowsDataGrid.Columns.Add(jobColumn);
        }
    }

    private bool IsAutoSyncEnabled()
    {
        return _preferencesStore.Load().SyncBoardAutoSyncEnabled;
    }

    private bool IsDeleteMirrorEnabled()
    {
        return _preferencesStore.Load().SyncBoardMirrorDeletesEnabled;
    }

    private void UpdateHeaderTexts()
    {
        var timestamp = _lastSyncUtc.HasValue
            ? _lastSyncUtc.Value.ToLocalTime().ToString("yyyy-MM-dd h:mm:ss tt")
            : "never";

        LastSyncTextBlock.Text = $"Last sync: {timestamp}";
        WatcherStatusTextBlock.Text = $"Watcher: {(_isSourceWatcherActive ? "active" : "inactive")} | Last sync date/time: {timestamp}";
        var watcherEventText = _lastWatcherEventUtc.HasValue
            ? _lastWatcherEventUtc.Value.ToLocalTime().ToString("yyyy-MM-dd h:mm:ss tt")
            : "never";
        WatcherHealthTextBlock.Text = $"Watcher health: last event {watcherEventText} | event {_lastWatcherEventSummary} | auto-sync trigger {_lastAutoSyncTriggerReason}";
        UpdateDestinationPreviewText();
        SourceLocationWarningTextBlock.Visibility = string.IsNullOrWhiteSpace(_sourceRootPath)
            ? Visibility.Visible
            : Visibility.Collapsed;
        JobLocationWarningTextBlock.Visibility = string.IsNullOrWhiteSpace(_destinationRootPath)
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusTextBlock.Text = _lastSyncStatus;
    }

    private void ConfigureSourceWatcher()
    {
        _sourceWatcher?.Dispose();
        _sourceWatcher = null;
        _isSourceWatcherActive = false;

        if (!IsAutoSyncEnabled())
        {
            UpdateHeaderTexts();
            return;
        }

        if (string.IsNullOrWhiteSpace(_sourceRootPath) || !Directory.Exists(_sourceRootPath))
        {
            UpdateHeaderTexts();
            return;
        }

        if (!HasAnyCheckedCells())
        {
            UpdateHeaderTexts();
            return;
        }

        _sourceWatcher = new FileSystemWatcher(_sourceRootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _sourceWatcher.Changed += SourceWatcher_OnChanged;
        _sourceWatcher.Created += SourceWatcher_OnChanged;
        _sourceWatcher.Deleted += SourceWatcher_OnChanged;
        _sourceWatcher.Renamed += SourceWatcher_OnChanged;
        _sourceWatcher.Error += SourceWatcher_OnError;
        _isSourceWatcherActive = true;
        UpdateHeaderTexts();
    }

    private void SourceWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsAutoSyncEnabled())
        {
            return;
        }

        _lastWatcherEventUtc = DateTimeOffset.UtcNow;
        _lastWatcherEventSummary = $"{e.ChangeType}: {Path.GetFileName(e.FullPath)}";

        if (_isSyncRunning)
        {
            _pendingAutoSyncRequested = true;
            _lastAutoSyncTriggerReason = "Queued while sync running";
            Dispatcher.Invoke(UpdateHeaderTexts);
            return;
        }

        Dispatcher.Invoke(() =>
        {
            _autoSyncDebounceTimer.Stop();
            _autoSyncDebounceTimer.Start();
            _lastAutoSyncTriggerReason = $"{e.ChangeType}: {Path.GetFileName(e.FullPath)}";
            StatusTextBlock.Text = "Detected source change. Auto-sync queued...";
            UpdateHeaderTexts();
        });
    }

    private void SourceWatcher_OnError(object sender, ErrorEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _lastWatcherEventUtc = DateTimeOffset.UtcNow;
            _lastWatcherEventSummary = "Watcher error";
            _lastAutoSyncTriggerReason = "Watcher restart after error";
            ConfigureSourceWatcher();
            if (IsAutoSyncEnabled())
            {
                _autoSyncDebounceTimer.Stop();
                _autoSyncDebounceTimer.Start();
                StatusTextBlock.Text = "Watcher restarted after source notification error. Auto-sync queued...";
            }

            UpdateHeaderTexts();
        });
    }

    private async void AutoSyncDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _autoSyncDebounceTimer.Stop();
        _lastAutoSyncTriggerReason = "Debounce timer elapsed";
        await RunSyncAsync(isAutoTrigger: true, dryRun: false).ConfigureAwait(true);
    }

    private async Task RunSyncAsync(bool isAutoTrigger, bool dryRun)
    {
        if (_isSyncRunning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_sourceRootPath)
            || string.IsNullOrWhiteSpace(_destinationRootPath))
        {
            if (!isAutoTrigger)
            {
                MessageBox.Show(this, "Setup source and destination first.", "Sync Board", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        var activePeople = GetActivePeople();
        var activeJobs = _jobs
            .Where(job => !string.IsNullOrWhiteSpace(job.JobName) && !string.IsNullOrWhiteSpace(job.ProjectNumber))
            .ToList();

        if (activePeople.Count == 0 || activeJobs.Count == 0 || !HasAnyCheckedCells())
        {
            if (!isAutoTrigger)
            {
                MessageBox.Show(this, "Add people/jobs and check at least one cell to sync.", "Sync Board", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        _isSyncRunning = !dryRun;
        ManualSyncButton.IsEnabled = dryRun ? ManualSyncButton.IsEnabled : false;

        var syncedCells = 0;
        var copiedFiles = 0;
        var skippedFiles = 0;
        var deletedItems = 0;
        var migratedItems = 0;
        var mirrorDeletes = IsDeleteMirrorEnabled();

        try
        {
            foreach (var person in activePeople)
            {
                var personFolderName = NormalizeFolderSegment(person.PersonName, "DB NEW");
                var personRoot = Path.Combine(_destinationRootPath, personFolderName);
                if (!dryRun)
                {
                    await EnsureTrackedFolderAsync(personRoot).ConfigureAwait(true);
                }

                foreach (var job in activeJobs)
                {
                    if (!person.JobChecks.TryGetValue(job.JobId, out var shouldSync) || !shouldSync)
                    {
                        continue;
                    }

                    var jobFolderName = NormalizeFolderSegment(job.JobName, "NEW JOB");
                    var projectFolderName = NormalizeFolderSegment(job.ProjectNumber, "UNKNOWN_PROJECT");
                    var jobFolder = Path.Combine(personRoot, jobFolderName);
                    EnsurePathWithinProjectGuard(jobFolder, jobFolder, projectFolderName);
                    if (!dryRun)
                    {
                        await EnsureTrackedFolderAsync(jobFolder).ConfigureAwait(true);
                    }

                    var projectFolder = Path.Combine(jobFolder, projectFolderName);
                    EnsurePathWithinProjectGuard(projectFolder, jobFolder, projectFolderName);
                    if (dryRun)
                    {
                        migratedItems += CountLegacyJobFolderItems(jobFolder, projectFolder);
                    }
                    else
                    {
                        migratedItems += await MigrateLegacyJobFolderLayoutAsync(jobFolder, projectFolder).ConfigureAwait(true);
                    }

                    var sourceProjectFolder = ResolveSourceProjectFolder(projectFolderName);
                    if (string.IsNullOrWhiteSpace(sourceProjectFolder) || !Directory.Exists(sourceProjectFolder))
                    {
                        if (mirrorDeletes && Directory.Exists(projectFolder))
                        {
                            if (!dryRun)
                            {
                                await _fileOperationService.ExecuteAsync(new FileOperationRequest
                                {
                                    OperationType = FileOperationType.Delete,
                                    SourcePaths = new[] { projectFolder },
                                    DestinationPath = null
                                }, progress: null, CancellationToken.None).ConfigureAwait(true);
                            }

                            deletedItems++;
                        }

                        continue;
                    }

                    if (!dryRun)
                    {
                        await EnsureTrackedFolderAsync(projectFolder).ConfigureAwait(true);
                    }

                    foreach (var sourceDir in Directory.EnumerateDirectories(sourceProjectFolder, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(sourceProjectFolder, sourceDir);
                        var destinationDir = Path.Combine(projectFolder, relative);
                        EnsurePathWithinProjectGuard(destinationDir, jobFolder, projectFolderName);
                        if (!dryRun)
                        {
                            await EnsureTrackedFolderAsync(destinationDir).ConfigureAwait(true);
                        }
                    }

                    foreach (var sourceFile in Directory.EnumerateFiles(sourceProjectFolder, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(sourceProjectFolder, sourceFile);
                        var destinationFile = Path.Combine(projectFolder, relative);
                        var destinationDir = Path.GetDirectoryName(destinationFile);
                        if (string.IsNullOrWhiteSpace(destinationDir))
                        {
                            continue;
                        }

                        EnsurePathWithinProjectGuard(destinationDir, jobFolder, projectFolderName);
                        EnsurePathWithinProjectGuard(destinationFile, jobFolder, projectFolderName);

                        if (!dryRun)
                        {
                            await EnsureTrackedFolderAsync(destinationDir).ConfigureAwait(true);
                        }

                        if (File.Exists(destinationFile))
                        {
                            if (AreFilesEquivalent(sourceFile, destinationFile))
                            {
                                skippedFiles++;
                                continue;
                            }

                            if (!dryRun)
                            {
                                await _fileOperationService.ExecuteAsync(new FileOperationRequest
                                {
                                    OperationType = FileOperationType.Delete,
                                    SourcePaths = new[] { destinationFile },
                                    DestinationPath = null
                                }, progress: null, CancellationToken.None).ConfigureAwait(true);
                            }
                        }

                        if (!dryRun)
                        {
                            await _fileOperationService.ExecuteAsync(new FileOperationRequest
                            {
                                OperationType = FileOperationType.Copy,
                                SourcePaths = new[] { sourceFile },
                                DestinationPath = destinationDir,
                                ConflictResolution = ConflictResolution.KeepBoth,
                                ApplyToAllConflicts = true,
                                VerifyChecksumForLargeFiles = false
                            }, progress: null, CancellationToken.None).ConfigureAwait(true);
                        }

                        copiedFiles++;
                    }

                    if (mirrorDeletes)
                    {
                        deletedItems += await RemoveDestinationItemsMissingFromSourceAsync(sourceProjectFolder, projectFolder, dryRun, jobFolder, projectFolderName).ConfigureAwait(true);
                    }

                    syncedCells++;
                }
            }

            if (!dryRun)
            {
                _lastSyncUtc = DateTimeOffset.UtcNow;
            }

            _lastSyncStatus = dryRun
                ? $"Dry run: {syncedCells} checked cell(s), {copiedFiles} copy/update actions, {deletedItems} delete actions, {skippedFiles} already current, {migratedItems} legacy move actions."
                : $"Sync complete: {syncedCells} checked cell(s), {copiedFiles} copied, {deletedItems} deleted, {skippedFiles} already current, {migratedItems} moved into project folders.";
            SaveState();
            UpdateHeaderTexts();
        }
        catch (Exception ex)
        {
            _lastSyncStatus = $"Sync failed: {ex.Message}";
            SaveState();
            UpdateHeaderTexts();

            if (!isAutoTrigger)
            {
                MessageBox.Show(this, ex.Message, "Sync Board", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isSyncRunning = false;
            ManualSyncButton.IsEnabled = true;

            if (_pendingAutoSyncRequested && IsAutoSyncEnabled())
            {
                _pendingAutoSyncRequested = false;
                _autoSyncDebounceTimer.Stop();
                _autoSyncDebounceTimer.Start();
                _lastAutoSyncTriggerReason = "Follow-up run after pending source changes";
                StatusTextBlock.Text = "Detected additional source changes. Auto-sync queued...";
                UpdateHeaderTexts();
            }
        }
    }

    private async Task EnsureTrackedFolderAsync(string folderPath)
    {
        if (Directory.Exists(folderPath))
        {
            return;
        }

        await _fileOperationService.ExecuteAsync(new FileOperationRequest
        {
            OperationType = FileOperationType.CreateFolder,
            SourcePaths = new[] { folderPath },
            DestinationPath = null
        }, progress: null, CancellationToken.None).ConfigureAwait(true);
    }

    private static bool AreFilesEquivalent(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);

        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        var delta = (leftInfo.LastWriteTimeUtc - rightInfo.LastWriteTimeUtc).Duration();
        return delta <= TimeSpan.FromSeconds(2);
    }

    private async Task<int> RemoveDestinationItemsMissingFromSourceAsync(string sourceRoot, string destinationRoot, bool dryRun, string jobFolder, string projectFolderName)
    {
        if (!Directory.Exists(destinationRoot))
        {
            return 0;
        }

        var deletedCount = 0;

        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var destinationFile in Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = Path.GetRelativePath(destinationRoot, destinationFile);
            if (sourceFiles.Contains(relative))
            {
                continue;
            }

            EnsurePathWithinProjectGuard(destinationFile, jobFolder, projectFolderName);

            if (!dryRun)
            {
                await _fileOperationService.ExecuteAsync(new FileOperationRequest
                {
                    OperationType = FileOperationType.Delete,
                    SourcePaths = new[] { destinationFile },
                    DestinationPath = null
                }, progress: null, CancellationToken.None).ConfigureAwait(true);
            }

            deletedCount++;
        }

        var sourceDirectories = Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var destinationDirectory in Directory.EnumerateDirectories(destinationRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)
                     .ToList())
        {
            var relative = Path.GetRelativePath(destinationRoot, destinationDirectory);
            if (sourceDirectories.Contains(relative))
            {
                continue;
            }

            if (!Directory.Exists(destinationDirectory))
            {
                continue;
            }

            EnsurePathWithinProjectGuard(destinationDirectory, jobFolder, projectFolderName);

            if (!dryRun)
            {
                await _fileOperationService.ExecuteAsync(new FileOperationRequest
                {
                    OperationType = FileOperationType.Delete,
                    SourcePaths = new[] { destinationDirectory },
                    DestinationPath = null
                }, progress: null, CancellationToken.None).ConfigureAwait(true);
            }

            deletedCount++;
        }

        return deletedCount;
    }

    private string? ResolveSourceProjectFolder(string projectNumber)
    {
        var trimmedProjectNumber = NormalizeFolderSegment(projectNumber, string.Empty);
        if (string.IsNullOrWhiteSpace(trimmedProjectNumber)
            || string.IsNullOrWhiteSpace(_sourceRootPath)
            || !Directory.Exists(_sourceRootPath))
        {
            return null;
        }

        var nestedProjectFolder = Path.Combine(_sourceRootPath, trimmedProjectNumber);
        if (Directory.Exists(nestedProjectFolder))
        {
            return nestedProjectFolder;
        }

        var sourceRootName = new DirectoryInfo(_sourceRootPath).Name.Trim();
        if (string.Equals(sourceRootName, trimmedProjectNumber, StringComparison.OrdinalIgnoreCase))
        {
            return _sourceRootPath;
        }

        return null;
    }

    private static string NormalizeFolderSegment(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        normalized = normalized.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalid, '-');
        }

        normalized = normalized.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private int CountLegacyJobFolderItems(string jobFolder, string projectFolder)
    {
        if (!Directory.Exists(jobFolder))
        {
            return 0;
        }

        var fullProjectFolderPath = Path.GetFullPath(projectFolder);
        return Directory.EnumerateFileSystemEntries(jobFolder, "*", SearchOption.TopDirectoryOnly)
            .Count(path => !string.Equals(Path.GetFullPath(path), fullProjectFolderPath, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsurePathWithinProjectGuard(string targetPath, string jobFolder, string projectFolderName)
    {
        if (!_strictProjectFolderGuardEnabled)
        {
            return;
        }

        var expectedRoot = Path.GetFullPath(Path.Combine(jobFolder, projectFolderName))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTarget = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullTarget, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expectedRootPrefix = expectedRoot + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(expectedRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Strict project folder guard blocked write outside project folder. Target: {targetPath}");
        }
    }

    private void UpdateDestinationPreviewText()
    {
        DestinationPreviewTextBlock.Text = $"Destination preview: {BuildDestinationPreviewText()}";
    }

    private string BuildDestinationPreviewText()
    {
        var person = GetActivePeople().FirstOrDefault();
        var job = _jobs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ProjectNumber))
                  ?? _jobs.FirstOrDefault();

        if (person is null || job is null || string.IsNullOrWhiteSpace(_destinationRootPath))
        {
            return "(select or check a person/job cell)";
        }

        var personFolderName = NormalizeFolderSegment(person.PersonName, "DB NEW");
        var jobFolderName = NormalizeFolderSegment(job.JobName, "NEW JOB");
        var projectFolderName = NormalizeFolderSegment(job.ProjectNumber, "UNKNOWN_PROJECT");
        return Path.Combine(_destinationRootPath, personFolderName, jobFolderName, projectFolderName);
    }

    private async Task<int> MigrateLegacyJobFolderLayoutAsync(string jobFolder, string projectFolder)
    {
        if (!Directory.Exists(jobFolder))
        {
            return 0;
        }

        var fullProjectFolderPath = Path.GetFullPath(projectFolder);
        var legacyEntries = Directory.EnumerateFileSystemEntries(jobFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFullPath(path), fullProjectFolderPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (legacyEntries.Count == 0)
        {
            return 0;
        }

        await EnsureTrackedFolderAsync(projectFolder).ConfigureAwait(true);

        var moved = 0;
        foreach (var entry in legacyEntries)
        {
            await _fileOperationService.ExecuteAsync(new FileOperationRequest
            {
                OperationType = FileOperationType.Move,
                SourcePaths = new[] { entry },
                DestinationPath = projectFolder,
                ConflictResolution = ConflictResolution.KeepBoth,
                ApplyToAllConflicts = true,
                VerifyChecksumForLargeFiles = false
            }, progress: null, CancellationToken.None).ConfigureAwait(true);

            moved++;
        }

        return moved;
    }

    private void AddPersonRow(string personName)
    {
        var row = _matrixTable.NewRow();
        row[PersonColumnName] = personName;
        foreach (var job in _jobs)
        {
            row[job.JobId] = false;
        }

        _matrixTable.Rows.Add(row);
    }

    private List<SyncBoardPersonRow> GetActivePeople()
    {
        var people = new List<SyncBoardPersonRow>(_matrixTable.Rows.Count);
        foreach (DataRow row in _matrixTable.Rows)
        {
            var personName = row[PersonColumnName]?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(personName))
            {
                continue;
            }

            var checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in _jobs)
            {
                checks[job.JobId] = row[job.JobId] is bool enabled && enabled;
            }

            people.Add(new SyncBoardPersonRow
            {
                PersonName = personName,
                JobChecks = checks
            });
        }

        return people;
    }

    private bool HasAnyCheckedCells()
    {
        foreach (DataRow row in _matrixTable.Rows)
        {
            foreach (var job in _jobs)
            {
                if (row[job.JobId] is bool enabled && enabled)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private List<string> GetAllPersonNames()
    {
        var people = new List<string>(_matrixTable.Rows.Count);
        foreach (DataRow row in _matrixTable.Rows)
        {
            var personName = row[PersonColumnName]?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(personName))
            {
                people.Add(personName);
            }
        }

        return people.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void AddPersonButton_OnClick(object sender, RoutedEventArgs e)
    {
        var prompt = new InputPromptWindow("Add Person", "Person name (column A):", "DB NEW")
        {
            Owner = this
        };

        if (prompt.ShowDialog() != true)
        {
            return;
        }

        var personName = prompt.Result?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(personName))
        {
            return;
        }

        AddPersonRow(personName);
        SaveState();
    }

    private void AddJobButton_OnClick(object sender, RoutedEventArgs e)
    {
        var namePrompt = new InputPromptWindow("Add Job", "Job name (row 1):", "NEW JOB")
        {
            Owner = this
        };

        if (namePrompt.ShowDialog() != true)
        {
            return;
        }

        var jobName = namePrompt.Result?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return;
        }

        var numberPrompt = new InputPromptWindow("Add Job", "Project number (row 2):", string.Empty)
        {
            Owner = this
        };

        if (numberPrompt.ShowDialog() != true)
        {
            return;
        }

        var projectNumber = numberPrompt.Result?.Trim() ?? string.Empty;

        var job = new SyncBoardJobColumn
        {
            JobId = Guid.NewGuid().ToString("N"),
            JobName = jobName,
            ProjectNumber = projectNumber
        };

        _jobs.Add(job);
        _matrixTable.Columns.Add(job.JobId, typeof(bool));
        foreach (DataRow row in _matrixTable.Rows)
        {
            row[job.JobId] = false;
        }

        RebuildGridColumns();
        SaveState();
        ConfigureSourceWatcher();
    }

    private void DataGridColumnHeader_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGridColumnHeader header)
        {
            return;
        }

        var sortMember = header.Column?.SortMemberPath;
        if (string.IsNullOrWhiteSpace(sortMember) || sortMember == PersonColumnName)
        {
            return;
        }

        if (_jobs.Any(job => string.Equals(job.JobId, sortMember, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedJobId = sortMember;
            StatusTextBlock.Text = $"Selected job: {GetSelectedJobDisplayName()}";
            RebuildGridColumns();
        }
    }

    private async void RemoveJobButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_jobs.Count == 0)
        {
            MessageBox.Show(this, "There are no jobs to remove.", "Remove Job", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var job = GetSelectedJob();
        if (job is null)
        {
            var defaultName = _jobs[^1].JobName;
            var prompt = new InputPromptWindow("Remove Job", "Type the job name to remove:", defaultName)
            {
                Owner = this
            };

            if (prompt.ShowDialog() != true)
            {
                return;
            }

            var jobNameInput = prompt.Result?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(jobNameInput))
            {
                return;
            }

            job = _jobs.FirstOrDefault(x => string.Equals(x.JobName, jobNameInput, StringComparison.OrdinalIgnoreCase));
        }

        if (job is null)
        {
            MessageBox.Show(this, "Selected job not found.", "Remove Job", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var removeDropboxConfirm = MessageBox.Show(
            this,
            $"Remove job '{job.JobName}' from the board.\n\nDo you also want to remove this job folder from users' Dropbox paths?\n\nYes = remove from board and Dropbox\nNo = remove from board only\nCancel = keep everything",
            "Remove Job",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (removeDropboxConfirm == MessageBoxResult.Cancel)
        {
            return;
        }

        if (removeDropboxConfirm == MessageBoxResult.Yes)
        {
            var personNames = GetAllPersonNames();
            foreach (var personName in personNames)
            {
                var targetFolder = Path.Combine(_destinationRootPath, personName, job.JobName);
                if (!Directory.Exists(targetFolder))
                {
                    continue;
                }

                await _fileOperationService.ExecuteAsync(new FileOperationRequest
                {
                    OperationType = FileOperationType.Delete,
                    SourcePaths = new[] { targetFolder },
                    DestinationPath = null
                }, progress: null, CancellationToken.None).ConfigureAwait(true);
            }
        }

        _jobs.Remove(job);
        if (string.Equals(_selectedJobId, job.JobId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedJobId = null;
        }

        if (_matrixTable.Columns.Contains(job.JobId))
        {
            _matrixTable.Columns.Remove(job.JobId);
        }

        if (_jobs.Count == 0)
        {
            var fallback = new SyncBoardJobColumn
            {
                JobId = Guid.NewGuid().ToString("N"),
                JobName = "NEW JOB",
                ProjectNumber = string.Empty
            };

            _jobs.Add(fallback);
            _matrixTable.Columns.Add(fallback.JobId, typeof(bool));
            foreach (DataRow row in _matrixTable.Rows)
            {
                row[fallback.JobId] = false;
            }
        }

        RebuildGridColumns();
        SaveState();
        ConfigureSourceWatcher();
    }

    private void RemovePersonButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (RowsDataGrid.SelectedItem is not DataRowView selectedRow)
        {
            return;
        }

        _matrixTable.Rows.Remove(selectedRow.Row);

        if (_matrixTable.Rows.Count == 0)
        {
            AddPersonRow("DB NEW");
        }

        SaveState();
    }

    private SyncBoardJobColumn? GetSelectedJob()
    {
        if (string.IsNullOrWhiteSpace(_selectedJobId))
        {
            return null;
        }

        return _jobs.FirstOrDefault(job => string.Equals(job.JobId, _selectedJobId, StringComparison.OrdinalIgnoreCase));
    }

    private string GetSelectedJobDisplayName()
    {
        var job = GetSelectedJob();
        if (job is null)
        {
            return "none";
        }

        return string.IsNullOrWhiteSpace(job.ProjectNumber)
            ? job.JobName
            : $"{job.JobName} ({job.ProjectNumber})";
    }

    private async void ManualSyncButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSyncAsync(isAutoTrigger: false, dryRun: false).ConfigureAwait(true);
    }

    private async void DryRunSyncButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSyncAsync(isAutoTrigger: false, dryRun: true).ConfigureAwait(true);
    }

    private void RefreshBoardButton_OnClick(object sender, RoutedEventArgs e)
    {
        LoadState();
        UpdateHeaderTexts();
        ConfigureSourceWatcher();
    }

    private void SetupButton_OnClick(object sender, RoutedEventArgs e)
    {
        var setup = new SyncBoardSetupWindow(_sourceRootPath, _destinationRootPath)
        {
            Owner = this
        };

        if (setup.ShowDialog() != true)
        {
            return;
        }

        _sourceRootPath = setup.SourceRootPath;
        _destinationRootPath = setup.DestinationRootPath;
        SaveState();
        UpdateHeaderTexts();
        ConfigureSourceWatcher();
    }

    private void StrictProjectGuardCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        _strictProjectFolderGuardEnabled = StrictProjectGuardCheckBox.IsChecked != false;
        SaveState();
        UpdateHeaderTexts();
    }

    private void ExportSyncDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var tempFolder = Path.Combine(Path.GetTempPath(), $"lui-p-syncboard-diagnostics-{stamp}");
            Directory.CreateDirectory(tempFolder);

            var summaryPath = Path.Combine(tempFolder, "syncboard-summary.txt");
            File.WriteAllLines(summaryPath, new[]
            {
                $"Generated: {DateTimeOffset.Now:O}",
                $"Source Root: {_sourceRootPath}",
                $"Destination Root: {_destinationRootPath}",
                $"Strict Guard Enabled: {_strictProjectFolderGuardEnabled}",
                $"Watcher Active: {_isSourceWatcherActive}",
                $"Watcher Last Event: {_lastWatcherEventUtc?.ToLocalTime().ToString("yyyy-MM-dd h:mm:ss tt") ?? "never"}",
                $"Watcher Last Event Summary: {_lastWatcherEventSummary}",
                $"Watcher Last Trigger Reason: {_lastAutoSyncTriggerReason}",
                $"Last Sync UTC: {_lastSyncUtc?.ToString("O") ?? "never"}",
                $"Last Sync Status: {_lastSyncStatus}",
                $"Destination Preview: {BuildDestinationPreviewText()}"
            });

            var configRoot = Path.Combine(_appPathsProvider.GetAppDataRoot(), "config");
            var syncStatePath = Path.Combine(configRoot, "sync-board-state.json");
            var prefsPath = Path.Combine(configRoot, "app-preferences.json");

            if (File.Exists(syncStatePath))
            {
                File.Copy(syncStatePath, Path.Combine(tempFolder, "sync-board-state.json"), overwrite: true);
            }

            if (File.Exists(prefsPath))
            {
                File.Copy(prefsPath, Path.Combine(tempFolder, "app-preferences.json"), overwrite: true);
            }

            var zipPath = Path.Combine(desktop, $"lui-p-syncboard-diagnostics-{stamp}.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(tempFolder, zipPath);
            Directory.Delete(tempFolder, recursive: true);

            MessageBox.Show(this, $"Sync diagnostics exported:\n{zipPath}", "Sync Board", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to export sync diagnostics.\n\n{ex.Message}", "Sync Board", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RowsDataGrid_OnCellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SaveState();
            ConfigureSourceWatcher();
        }, DispatcherPriority.Background);
    }

    private void RowsDataGrid_OnCurrentCellChanged(object? sender, EventArgs e)
    {
        SaveState();
        ConfigureSourceWatcher();
    }

    private void RowsDataGrid_OnSorting(object sender, DataGridSortingEventArgs e)
    {
        if (RowsDataGrid.ItemsSource is not DataView view)
        {
            return;
        }

        var sortMember = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(sortMember))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        var newDirection = e.Column.SortDirection != ListSortDirection.Ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        foreach (var column in RowsDataGrid.Columns)
        {
            if (!ReferenceEquals(column, e.Column))
            {
                column.SortDirection = null;
            }
        }

        e.Column.SortDirection = newDirection;
        var directionText = newDirection == ListSortDirection.Ascending ? "ASC" : "DESC";
        view.Sort = $"[{sortMember}] {directionText}";
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SyncBoardWindow_OnClosed(object? sender, EventArgs e)
    {
        _sourceWatcher?.Dispose();
        _sourceWatcher = null;
        _isSourceWatcherActive = false;
        _autoSyncDebounceTimer.Stop();
    }
}
