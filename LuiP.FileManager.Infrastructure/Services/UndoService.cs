using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class UndoService : IUndoService
{
    private readonly IActivityLogRepository _activityLogRepository;

    public UndoService(IActivityLogRepository activityLogRepository)
    {
        _activityLogRepository = activityLogRepository;
    }

    public async Task<UndoResult> UndoAsync(string operationId, CancellationToken cancellationToken)
    {
        var operation = await _activityLogRepository.GetByOperationIdAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return new UndoResult
            {
                Succeeded = false,
                Message = "The selected operation no longer exists.",
                UndoneItems = 0,
                TotalItems = 0
            };
        }

        if (!operation.UndoAvailable)
        {
            return new UndoResult
            {
                Succeeded = false,
                Message = "Undo is not available for this operation.",
                UndoneItems = 0,
                TotalItems = 0
            };
        }

        var items = await _activityLogRepository.GetOperationItemsAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new UndoResult
            {
                Succeeded = false,
                Message = "No operation details were recorded for safe undo.",
                UndoneItems = 0,
                TotalItems = 0
            };
        }

        var undone = 0;
        var failures = new List<string>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                switch (item.Kind)
                {
                    case OperationItemKind.CopyCreatedFile:
                        if (item.DestinationPath is null || !File.Exists(item.DestinationPath))
                        {
                            failures.Add($"Missing destination file: {item.DestinationPath}");
                            break;
                        }

                        var info = new FileInfo(item.DestinationPath);
                        if (item.DestinationSizeBytes.HasValue && info.Length != item.DestinationSizeBytes.Value)
                        {
                            failures.Add($"Destination changed in size: {item.DestinationPath}");
                            break;
                        }

                        if (item.DestinationLastWriteUtc.HasValue)
                        {
                            var delta = (info.LastWriteTimeUtc - item.DestinationLastWriteUtc.Value.UtcDateTime).Duration();
                            if (delta > TimeSpan.FromSeconds(2))
                            {
                                failures.Add($"Destination timestamp changed: {item.DestinationPath}");
                                break;
                            }
                        }

                        File.Delete(item.DestinationPath);
                        undone++;
                        break;

                    case OperationItemKind.DeleteMovedToRecovery:
                        if (item.RecoveryPath is null || (!File.Exists(item.RecoveryPath) && !Directory.Exists(item.RecoveryPath)))
                        {
                            failures.Add($"Recovery item missing: {item.RecoveryPath}");
                            break;
                        }

                        if (File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath))
                        {
                            failures.Add($"Original location already occupied: {item.SourcePath}");
                            break;
                        }

                        if (File.Exists(item.RecoveryPath))
                        {
                            File.Move(item.RecoveryPath, item.SourcePath);
                        }
                        else
                        {
                            Directory.Move(item.RecoveryPath, item.SourcePath);
                        }

                        undone++;
                        break;

                    case OperationItemKind.MoveCompleted:
                        if (string.IsNullOrWhiteSpace(item.DestinationPath))
                        {
                            failures.Add("Move undo is missing destination metadata.");
                            break;
                        }

                        if (!File.Exists(item.DestinationPath) && !Directory.Exists(item.DestinationPath))
                        {
                            failures.Add($"Moved item no longer exists: {item.DestinationPath}");
                            break;
                        }

                        if (File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath))
                        {
                            failures.Add($"Original path is already occupied: {item.SourcePath}");
                            break;
                        }

                        if (File.Exists(item.DestinationPath))
                        {
                            File.Move(item.DestinationPath, item.SourcePath);
                        }
                        else
                        {
                            Directory.Move(item.DestinationPath, item.SourcePath);
                        }

                        undone++;
                        break;

                    case OperationItemKind.RenameCompleted:
                        if (string.IsNullOrWhiteSpace(item.DestinationPath))
                        {
                            failures.Add("Rename undo is missing destination metadata.");
                            break;
                        }

                        if (!File.Exists(item.DestinationPath) && !Directory.Exists(item.DestinationPath))
                        {
                            failures.Add($"Renamed item no longer exists: {item.DestinationPath}");
                            break;
                        }

                        if (File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath))
                        {
                            failures.Add($"Original rename path is already occupied: {item.SourcePath}");
                            break;
                        }

                        if (File.Exists(item.DestinationPath))
                        {
                            File.Move(item.DestinationPath, item.SourcePath);
                        }
                        else
                        {
                            Directory.Move(item.DestinationPath, item.SourcePath);
                        }

                        undone++;
                        break;

                    case OperationItemKind.CreateFolderCreated:
                        if (!Directory.Exists(item.SourcePath))
                        {
                            failures.Add($"Created folder no longer exists: {item.SourcePath}");
                            break;
                        }

                        var hasChildren = Directory.EnumerateFileSystemEntries(item.SourcePath).Any();
                        if (hasChildren)
                        {
                            failures.Add($"Cannot undo folder creation because the folder now contains data: {item.SourcePath}");
                            break;
                        }

                        Directory.Delete(item.SourcePath, recursive: false);
                        undone++;
                        break;

                    case OperationItemKind.FolderTransferCompleted:
                        if (string.IsNullOrWhiteSpace(item.DestinationPath))
                        {
                            failures.Add("Folder transfer undo is missing destination metadata.");
                            break;
                        }

                        if (!Directory.Exists(item.DestinationPath))
                        {
                            failures.Add($"Transferred folder no longer exists: {item.DestinationPath}");
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(operation.DestinationPath))
                        {
                            var destinationRoot = Path.GetFullPath(operation.DestinationPath);
                            var destinationFolder = Path.GetFullPath(item.DestinationPath);
                            if (!destinationFolder.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                failures.Add($"Refusing to undo folder transfer outside operation destination root: {item.DestinationPath}");
                                break;
                            }
                        }

                        Directory.Delete(item.DestinationPath, recursive: true);
                        undone++;
                        break;

                    default:
                        failures.Add($"Unsupported undo type for item: {item.SourcePath}");
                        break;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{item.SourcePath}: {ex.Message}");
            }
        }

        var allSucceeded = failures.Count == 0;
        var note = allSucceeded
            ? $"Undone successfully at {DateTimeOffset.UtcNow:O}."
            : $"Undo attempted at {DateTimeOffset.UtcNow:O}; {failures.Count} item(s) could not be safely undone.";

        await _activityLogRepository.MarkUndoConsumedAsync(operationId, note, cancellationToken).ConfigureAwait(false);

        if (allSucceeded)
        {
            return new UndoResult
            {
                Succeeded = true,
                Message = $"Undo completed. {undone} item(s) were restored or removed safely.",
                UndoneItems = undone,
                TotalItems = items.Count
            };
        }

        var failurePreview = string.Join("; ", failures.Take(3));
        return new UndoResult
        {
            Succeeded = false,
            Message = $"Undo partially completed. {undone}/{items.Count} item(s) were undone. Issues: {failurePreview}",
            UndoneItems = undone,
            TotalItems = items.Count
        };
    }
}
