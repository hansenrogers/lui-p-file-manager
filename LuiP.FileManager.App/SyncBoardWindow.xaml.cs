using System.Data;
using System.ComponentModel;
using System.IO;
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
    private readonly SyncBoardStore _store;
    private readonly DispatcherTimer _autoSyncDebounceTimer;
    private readonly List<SyncBoardJobColumn> _jobs = new();

    private DataTable _matrixTable = new();

    private string _sourceRootPath = string.Empty;
    private string _destinationRootPath = string.Empty;
    private DateTimeOffset? _lastSyncUtc;
    private bool _isSyncRunning;
    private string _lastSyncStatus = "Not synced yet.";
    private FileSystemWatcher? _sourceWatcher;

    public SyncBoardWindow(IFileOperationService fileOperationService, AppPreferencesStore preferencesStore)
    {
        _fileOperationService = fileOperationService;
        _preferencesStore = preferencesStore;
        _store = new SyncBoardStore(new WindowsAppPathsProvider());
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
            People = people,
            Jobs = _jobs.ToList()
        };

        _store.Save(state);
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

    private void UpdateHeaderTexts()
    {
        var timestamp = _lastSyncUtc.HasValue
            ? _lastSyncUtc.Value.ToLocalTime().ToString("M-d-yyyy h:mm:ss tt")
            : "never";

        LastSyncTextBlock.Text = $"Last sync: {timestamp}";
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

        if (!IsAutoSyncEnabled())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_sourceRootPath) || !Directory.Exists(_sourceRootPath))
        {
            return;
        }

        if (!HasAnyCheckedCells())
        {
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
    }

    private void SourceWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsAutoSyncEnabled() || _isSyncRunning)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            _autoSyncDebounceTimer.Stop();
            _autoSyncDebounceTimer.Start();
            StatusTextBlock.Text = "Detected source change. Auto-sync queued...";
        });
    }

    private async void AutoSyncDebounceTimer_OnTick(object? sender, EventArgs e)
    {
        _autoSyncDebounceTimer.Stop();
        await RunSyncAsync(isAutoTrigger: true).ConfigureAwait(true);
    }

    private async Task RunSyncAsync(bool isAutoTrigger)
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

        _isSyncRunning = true;
        ManualSyncButton.IsEnabled = false;

        var syncedCells = 0;
        var copiedFiles = 0;
        var skippedFiles = 0;

        try
        {
            foreach (var person in activePeople)
            {
                var personRoot = Path.Combine(_destinationRootPath, person.PersonName);
                await EnsureTrackedFolderAsync(personRoot).ConfigureAwait(true);

                foreach (var job in activeJobs)
                {
                    if (!person.JobChecks.TryGetValue(job.JobId, out var shouldSync) || !shouldSync)
                    {
                        continue;
                    }

                    var sourceProjectFolder = Path.Combine(_sourceRootPath, job.ProjectNumber.Trim());
                    if (!Directory.Exists(sourceProjectFolder))
                    {
                        continue;
                    }

                    var jobFolder = Path.Combine(personRoot, job.JobName.Trim());
                    await EnsureTrackedFolderAsync(jobFolder).ConfigureAwait(true);

                    foreach (var sourceDir in Directory.EnumerateDirectories(sourceProjectFolder, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(sourceProjectFolder, sourceDir);
                        var destinationDir = Path.Combine(jobFolder, relative);
                        await EnsureTrackedFolderAsync(destinationDir).ConfigureAwait(true);
                    }

                    foreach (var sourceFile in Directory.EnumerateFiles(sourceProjectFolder, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(sourceProjectFolder, sourceFile);
                        var destinationFile = Path.Combine(jobFolder, relative);
                        var destinationDir = Path.GetDirectoryName(destinationFile);
                        if (string.IsNullOrWhiteSpace(destinationDir))
                        {
                            continue;
                        }

                        await EnsureTrackedFolderAsync(destinationDir).ConfigureAwait(true);

                        if (File.Exists(destinationFile))
                        {
                            if (AreFilesEquivalent(sourceFile, destinationFile))
                            {
                                skippedFiles++;
                                continue;
                            }

                            await _fileOperationService.ExecuteAsync(new FileOperationRequest
                            {
                                OperationType = FileOperationType.Delete,
                                SourcePaths = new[] { destinationFile },
                                DestinationPath = null
                            }, progress: null, CancellationToken.None).ConfigureAwait(true);
                        }

                        await _fileOperationService.ExecuteAsync(new FileOperationRequest
                        {
                            OperationType = FileOperationType.Copy,
                            SourcePaths = new[] { sourceFile },
                            DestinationPath = destinationDir,
                            ConflictResolution = ConflictResolution.KeepBoth,
                            ApplyToAllConflicts = true,
                            VerifyChecksumForLargeFiles = false
                        }, progress: null, CancellationToken.None).ConfigureAwait(true);

                        copiedFiles++;
                    }

                    syncedCells++;
                }
            }

            _lastSyncUtc = DateTimeOffset.UtcNow;
            _lastSyncStatus = $"Sync complete: {syncedCells} checked cell(s), {copiedFiles} copied, {skippedFiles} already current.";
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

    private async void RemoveJobButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_jobs.Count == 0)
        {
            MessageBox.Show(this, "There are no jobs to remove.", "Remove Job", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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

        var job = _jobs.FirstOrDefault(x => string.Equals(x.JobName, jobNameInput, StringComparison.OrdinalIgnoreCase));
        if (job is null)
        {
            MessageBox.Show(this, "Job name not found.", "Remove Job", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private async void ManualSyncButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSyncAsync(isAutoTrigger: false).ConfigureAwait(true);
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

    private void RowsDataGrid_OnCellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(() => SaveState(), DispatcherPriority.Background);
    }

    private void RowsDataGrid_OnCurrentCellChanged(object? sender, EventArgs e)
    {
        SaveState();
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
        _autoSyncDebounceTimer.Stop();
    }
}
