using System.IO;
using System.Text.Json;
using System.Windows;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.App;

public partial class ActivityLogWindow : Window
{
    private readonly IActivityLogRepository _activityLogRepository;
    private readonly IUndoService _undoService;
    private readonly IFileOperationService _fileOperationService;
    private readonly Action? _onOperationCompleted;
    private readonly Func<(bool VerifyChecksums, long ThresholdBytes)>? _retryCopyChecksumDefaults;
    private IReadOnlyList<FileOperationRecord> _loadedRecords = Array.Empty<FileOperationRecord>();
    private IReadOnlyList<OperationItemRecord> _loadedItems = Array.Empty<OperationItemRecord>();

    public ActivityLogWindow(
        IActivityLogRepository activityLogRepository,
        IUndoService undoService,
        IFileOperationService fileOperationService,
        Action? onOperationCompleted = null,
        Func<(bool VerifyChecksums, long ThresholdBytes)>? retryCopyChecksumDefaults = null)
    {
        _activityLogRepository = activityLogRepository;
        _undoService = undoService;
        _fileOperationService = fileOperationService;
        _onOperationCompleted = onOperationCompleted;
        _retryCopyChecksumDefaults = retryCopyChecksumDefaults;
        InitializeComponent();
        UpdateOperationActionState();
        UpdateItemActionState();
        _ = LoadOperationsAsync();
    }

    private async Task LoadOperationsAsync()
    {
        try
        {
            _loadedRecords = await _activityLogRepository.GetRecentAsync(1000, CancellationToken.None).ConfigureAwait(true);
            ApplyFilters();
            if (OperationsDataGrid.SelectedItem is not FileOperationRecord)
            {
                OperationItemsDataGrid.ItemsSource = Array.Empty<OperationItemRecord>();
            }

            UpdateOperationActionState();
            UpdateItemActionState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Unable to load Activity Log entries.\n\n{ex.Message}",
                "Activity Log",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SearchTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void StatusFilterComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await LoadOperationsAsync().ConfigureAwait(true);
    }

    private async void OperationsDataGrid_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateOperationActionState();

        if (OperationsDataGrid.SelectedItem is not FileOperationRecord selected)
        {
            _loadedItems = Array.Empty<OperationItemRecord>();
            OperationItemsDataGrid.ItemsSource = _loadedItems;
            UpdateItemActionState();
            return;
        }

        try
        {
            _loadedItems = await _activityLogRepository.GetOperationItemsAsync(selected.OperationId, CancellationToken.None).ConfigureAwait(true);
            OperationItemsDataGrid.ItemsSource = _loadedItems.ToList();
            UpdateItemActionState();
        }
        catch (Exception ex)
        {
            _loadedItems = Array.Empty<OperationItemRecord>();
            OperationItemsDataGrid.ItemsSource = _loadedItems;
            UpdateItemActionState();
            MessageBox.Show(this, $"Unable to load operation items.\n\n{ex.Message}", "Activity Log", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RetryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (OperationsDataGrid.SelectedItem is not FileOperationRecord selected)
        {
            MessageBox.Show(this, "Select a failed or canceled operation to retry.", "Retry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.Status is not FileOperationStatus.Failed
            and not FileOperationStatus.Canceled
            and not FileOperationStatus.PartialFailed)
        {
            MessageBox.Show(this, "Retry is only available for failed, canceled, or partial-failed operations.", "Retry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Retry operation {selected.OperationId}?\n\nA new operation record will be created.",
            "Retry Confirmation",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        RetryButton.IsEnabled = false;
        UndoButton.IsEnabled = false;

        try
        {
            var retryRequest = await BuildRetryRequestAsync(selected).ConfigureAwait(true);
            using var cts = new CancellationTokenSource();
            var progressWindow = new OperationProgressWindow("Retry Progress", () => cts.Cancel())
            {
                Owner = this
            };

            var progress = new Progress<FileOperationProgress>(progressWindow.UpdateProgress);
            progressWindow.Show();

            FileOperationRecord retried;
            try
            {
                retried = await _fileOperationService.ExecuteAsync(retryRequest, progress, cts.Token).ConfigureAwait(true);
                progressWindow.MarkCompleted("Retry completed.");
            }
            finally
            {
                if (progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }
            }

            MessageBox.Show(this, $"Retry started and completed as operation {retried.OperationId}.", "Retry", MessageBoxButton.OK, MessageBoxImage.Information);
            _onOperationCompleted?.Invoke();
            await LoadOperationsAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "Retry was canceled.", "Retry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Retry failed.\n\n{ex.Message}", "Retry", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateOperationActionState();
        }
    }

    private async void UndoButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (OperationsDataGrid.SelectedItem is not FileOperationRecord selected)
        {
            MessageBox.Show(this, "Select an operation to undo.", "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!selected.UndoAvailable)
        {
            MessageBox.Show(this, "Undo is not available for this operation.", "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Undo operation {selected.OperationId}?\n\nThis will only proceed for items that remain safe to reverse.",
            "Undo Confirmation",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        UndoButton.IsEnabled = false;
        try
        {
            var result = await _undoService.UndoAsync(selected.OperationId, CancellationToken.None).ConfigureAwait(true);
            var icon = result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning;
            MessageBox.Show(this, result.Message, "Undo", MessageBoxButton.OK, icon);
            await LoadOperationsAsync().ConfigureAwait(true);
        }
        finally
        {
            UpdateOperationActionState();
        }
    }

    private void ApplyFilters()
    {
        var text = (SearchTextBox.Text ?? string.Empty).Trim();
        var status = GetSelectedStatusFilter();

        IEnumerable<FileOperationRecord> query = _loadedRecords;

        if (!string.IsNullOrWhiteSpace(text))
        {
            query = query.Where(x =>
                (x.OperationId?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || (x.SourcePath?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || (x.DestinationPath?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || (x.ErrorMessage?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        var filtered = query.ToList();
        OperationsDataGrid.ItemsSource = filtered;

        if (filtered.Count == 0)
        {
            _loadedItems = Array.Empty<OperationItemRecord>();
            OperationItemsDataGrid.ItemsSource = _loadedItems;
        }

        UpdateOperationActionState();
        UpdateItemActionState();
    }

    private FileOperationStatus? GetSelectedStatusFilter()
    {
        if (StatusFilterComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
        {
            return null;
        }

        var text = item.Content?.ToString() ?? string.Empty;
        return text switch
        {
            "Completed" => FileOperationStatus.Completed,
            "Failed" => FileOperationStatus.Failed,
            "Canceled" => FileOperationStatus.Canceled,
            "PartialFailed" => FileOperationStatus.PartialFailed,
            "Running" => FileOperationStatus.Running,
            "Planned" => FileOperationStatus.Planned,
            _ => null
        };
    }

    private async Task<FileOperationRequest> BuildRetryRequestAsync(FileOperationRecord operation)
    {
        var checksumDefaults = _retryCopyChecksumDefaults?.Invoke() ?? (false, 32L * 1024L * 1024L);
        var items = await _activityLogRepository.GetOperationItemsAsync(operation.OperationId, CancellationToken.None).ConfigureAwait(true);
        var sourcePaths = DeserializeSourcePaths(operation.SourcePathsJson)
            .Concat(items
            .Select(x => x.SourcePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var isRetryableFailedState = operation.Status is FileOperationStatus.Failed
            or FileOperationStatus.Canceled
            or FileOperationStatus.PartialFailed;

        if (isRetryableFailedState && sourcePaths.Count > 1)
        {
            var picker = new RetryItemSelectionWindow(sourcePaths)
            {
                Owner = this
            };

            if (picker.ShowDialog() != true)
            {
                throw new OperationCanceledException("Retry item selection was canceled.");
            }

            sourcePaths = picker.SelectedPaths.ToList();
        }

        if (sourcePaths.Count == 0 && !string.IsNullOrWhiteSpace(operation.SourcePath))
        {
            sourcePaths.Add(operation.SourcePath);
        }

        if (operation.OperationType == FileOperationType.Rename)
        {
            var renameItem = items.FirstOrDefault(x => x.Kind == OperationItemKind.RenameCompleted);
            var sourcePath = renameItem?.SourcePath ?? operation.SourcePath;
            var destinationPath = renameItem?.DestinationPath ?? operation.DestinationPath;
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new InvalidOperationException("Retry metadata for rename is incomplete.");
            }

            return new FileOperationRequest
            {
                OperationType = FileOperationType.Rename,
                SourcePaths = new[] { sourcePath },
                DestinationPath = destinationPath,
                ConflictResolution = ConflictResolution.KeepBoth,
                ApplyToAllConflicts = true
            };
        }

        if (sourcePaths.Count == 0)
        {
            throw new InvalidOperationException("Retry metadata is missing source paths.");
        }

        if ((operation.OperationType == FileOperationType.Copy || operation.OperationType == FileOperationType.Move)
            && string.IsNullOrWhiteSpace(operation.DestinationPath))
        {
            throw new InvalidOperationException("Retry metadata is missing a destination path.");
        }

        return new FileOperationRequest
        {
            OperationType = operation.OperationType,
            SourcePaths = sourcePaths,
            DestinationPath = operation.DestinationPath,
            ConflictResolution = ConflictResolution.KeepBoth,
            ApplyToAllConflicts = true,
            VerifyChecksumForLargeFiles = operation.OperationType == FileOperationType.Copy
                && checksumDefaults.VerifyChecksums,
            ChecksumThresholdBytes = operation.OperationType == FileOperationType.Copy
                ? Math.Max(1L, checksumDefaults.ThresholdBytes)
                : 32L * 1024L * 1024L
        };
    }

    private static IReadOnlyList<string> DeserializeSourcePaths(string? sourcePathsJson)
    {
        if (string.IsNullOrWhiteSpace(sourcePathsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(sourcePathsJson);
            return parsed?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList() ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void ExportCsvButton_OnClick(object sender, RoutedEventArgs e)
    {
        var rows = (OperationsDataGrid.ItemsSource as IEnumerable<FileOperationRecord>)?.ToList() ?? new List<FileOperationRecord>();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Nothing to export.", "Export CSV", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"lui-p-activity-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        using var writer = new StreamWriter(outputPath, false);
        writer.WriteLine("StartedUtc,OperationId,Type,Status,Items,Source,Destination,UndoAvailable,UndoExpiryUtc,Error");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",",
                Csv(row.StartedUtc.ToString("O")),
                Csv(row.OperationId),
                Csv(row.OperationType.ToString()),
                Csv(row.Status.ToString()),
                Csv(row.ItemCount.ToString()),
                Csv(row.SourcePath ?? string.Empty),
                Csv(row.DestinationPath ?? string.Empty),
                Csv(row.UndoAvailable ? "Yes" : "No"),
                Csv(row.UndoExpiryUtc?.ToString("O") ?? string.Empty),
                Csv(row.ErrorMessage ?? string.Empty)));
        }

        MessageBox.Show(this, $"CSV exported to:\n{outputPath}", "Export CSV", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportReportButton_OnClick(object sender, RoutedEventArgs e)
    {
        var rows = (OperationsDataGrid.ItemsSource as IEnumerable<FileOperationRecord>)?.ToList() ?? new List<FileOperationRecord>();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Nothing to export.", "Export Report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"lui-p-support-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        using var writer = new StreamWriter(outputPath, false);
        writer.WriteLine("Lui-P File Manager Support Report");
        writer.WriteLine($"Generated: {DateTimeOffset.Now:O}");
        writer.WriteLine();

        foreach (var row in rows)
        {
            writer.WriteLine($"Operation ID: {row.OperationId}");
            writer.WriteLine($"Started (UTC): {row.StartedUtc:O}");
            writer.WriteLine($"Type/Status: {row.OperationType} / {row.Status}");
            writer.WriteLine($"Items: {row.ItemCount}");
            writer.WriteLine($"Source: {row.SourcePath}");
            writer.WriteLine($"Destination: {row.DestinationPath}");
            writer.WriteLine($"Undo Available: {row.UndoAvailable}");
            writer.WriteLine($"Undo Until (UTC): {row.UndoExpiryUtc:O}");
            writer.WriteLine($"Error: {row.ErrorMessage}");
            writer.WriteLine(new string('-', 72));
        }

        MessageBox.Show(this, $"Report exported to:\n{outputPath}", "Export Report", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenItemSourceButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSelectedItemPath(x => x.SourcePath, "source");
    }

    private void OpenItemDestinationButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSelectedItemPath(x => x.DestinationPath, "destination");
    }

    private void OpenItemRecoveryButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenSelectedItemPath(x => x.RecoveryPath, "recovery");
    }

    private void CopySelectedItemPathButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (OperationItemsDataGrid.SelectedItem is not OperationItemRecord item)
        {
            MessageBox.Show(this, "Select an operation item first.", "Operation Items", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = item.DestinationPath ?? item.RecoveryPath ?? item.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Selected operation item does not contain a usable path.", "Operation Items", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(path);
        MessageBox.Show(this, "Path copied to clipboard.", "Operation Items", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenSelectedItemPath(Func<OperationItemRecord, string?> selector, string label)
    {
        if (OperationItemsDataGrid.SelectedItem is not OperationItemRecord item)
        {
            MessageBox.Show(this, "Select an operation item first.", "Operation Items", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = selector(item);
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, $"This item has no {label} path.", "Operation Items", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show(this, $"The {label} path no longer exists:\n{path}", "Operation Items", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            PreviewWindow.OpenPathWithShell(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to open {label} path.\n\n{ex.Message}", "Operation Items", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OperationItemsDataGrid_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateItemActionState();
    }

    private void OperationItemsDataGrid_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (OperationItemsDataGrid.SelectedItem is not OperationItemRecord item)
        {
            return;
        }

        var bestPath = item.DestinationPath ?? item.RecoveryPath ?? item.SourcePath;
        if (string.IsNullOrWhiteSpace(bestPath))
        {
            return;
        }

        OpenSelectedItemPath(_ => bestPath, "selected");
    }

    private void UpdateOperationActionState()
    {
        var selected = OperationsDataGrid.SelectedItem as FileOperationRecord;
        if (selected is null)
        {
            RetryButton.IsEnabled = false;
            UndoButton.IsEnabled = false;
            return;
        }

        RetryButton.IsEnabled = selected.Status is FileOperationStatus.Failed
            or FileOperationStatus.Canceled
            or FileOperationStatus.PartialFailed;

        UndoButton.IsEnabled = selected.UndoAvailable;
    }

    private void UpdateItemActionState()
    {
        if (OperationItemsDataGrid.SelectedItem is not OperationItemRecord item)
        {
            OpenItemSourceButton.IsEnabled = false;
            OpenItemDestinationButton.IsEnabled = false;
            OpenItemRecoveryButton.IsEnabled = false;
            CopySelectedItemPathButton.IsEnabled = false;
            return;
        }

        OpenItemSourceButton.IsEnabled = !string.IsNullOrWhiteSpace(item.SourcePath);
        OpenItemDestinationButton.IsEnabled = !string.IsNullOrWhiteSpace(item.DestinationPath);
        OpenItemRecoveryButton.IsEnabled = !string.IsNullOrWhiteSpace(item.RecoveryPath);
        CopySelectedItemPathButton.IsEnabled = !string.IsNullOrWhiteSpace(item.SourcePath)
            || !string.IsNullOrWhiteSpace(item.DestinationPath)
            || !string.IsNullOrWhiteSpace(item.RecoveryPath);
    }

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
