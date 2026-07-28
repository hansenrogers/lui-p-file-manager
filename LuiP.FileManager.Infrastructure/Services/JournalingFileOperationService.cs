using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using System.Security.Cryptography;
using System.Text.Json;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class JournalingFileOperationService : IFileOperationService
{
    private readonly IActivityLogRepository _activityLogRepository;
    private readonly IAppPathsProvider _appPathsProvider;

    public JournalingFileOperationService(IActivityLogRepository activityLogRepository, IAppPathsProvider appPathsProvider)
    {
        _activityLogRepository = activityLogRepository;
        _appPathsProvider = appPathsProvider;
    }

    public async Task<FileOperationRecord> ExecuteAsync(
        FileOperationRequest request,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.SourcePaths.Count == 0)
        {
            throw new ArgumentException("At least one source path is required.", nameof(request));
        }

        if (request.OperationType is not FileOperationType.Copy
            and not FileOperationType.Delete
            and not FileOperationType.Move
            and not FileOperationType.Rename
            and not FileOperationType.CreateFolder)
        {
            throw new NotSupportedException("Copy, delete, move, rename, and create-folder operations are enabled in this phase.");
        }

        if ((request.OperationType == FileOperationType.Copy
            || request.OperationType == FileOperationType.Move
            || request.OperationType == FileOperationType.Rename)
            && string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw new ArgumentException("Destination path is required for copy/move/rename operations.", nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            Directory.CreateDirectory(request.DestinationPath);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        var sourcePathSummary = BuildSourcePathSummary(request.SourcePaths);
        var sourcePathsJson = JsonSerializer.Serialize(request.SourcePaths);
        var pathSizes = request.SourcePaths.ToDictionary(path => path, EstimatePathBytes, StringComparer.OrdinalIgnoreCase);
        var estimatedTotalBytes = pathSizes.Values.Sum();
        var totalProgressUnits = estimatedTotalBytes > 0 ? estimatedTotalBytes : request.SourcePaths.Count;

        var record = new FileOperationRecord
        {
            OperationId = operationId,
            StartedUtc = started,
            CompletedUtc = null,
            OperationType = request.OperationType,
            Status = FileOperationStatus.Planned,
            ItemCount = request.SourcePaths.Count,
            TotalBytes = estimatedTotalBytes,
            SourcePath = sourcePathSummary,
            SourcePathsJson = sourcePathsJson,
            DestinationPath = request.DestinationPath,
            UndoAvailable = false,
            UndoExpiryUtc = started.AddDays(30)
        };

        await _activityLogRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);

        record.Status = FileOperationStatus.Running;
        await _activityLogRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);

        var completedItems = 0;
        long processedBytes = 0;

        try
        {
            foreach (var sourcePath in request.SourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (request.OperationType == FileOperationType.Copy && File.Exists(sourcePath))
                {
                    var destinationFile = await ResolveDestinationFilePathAsync(
                        request.DestinationPath!,
                        sourcePath,
                        request.ConflictResolution,
                        operationId,
                        cancellationToken).ConfigureAwait(false);

                    if (destinationFile is null)
                    {
                        throw new OperationCanceledException("Copy canceled because conflict resolution was set to Cancel.");
                    }

                    await CopyFileStreamedAsync(sourcePath, destinationFile, cancellationToken).ConfigureAwait(false);

                    if (request.VerifyChecksumForLargeFiles)
                    {
                        var sourceLength = new FileInfo(sourcePath).Length;
                        if (sourceLength >= request.ChecksumThresholdBytes)
                        {
                            await VerifyCopyChecksumAsync(sourcePath, destinationFile, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    var destinationInfo = new FileInfo(destinationFile);
                    await _activityLogRepository.AddOperationItemAsync(
                        new OperationItemRecord
                        {
                            ItemId = Guid.NewGuid().ToString("N"),
                            OperationId = operationId,
                            Kind = OperationItemKind.CopyCreatedFile,
                            SourcePath = sourcePath,
                            DestinationPath = destinationFile,
                            RecoveryPath = null,
                            DestinationSizeBytes = destinationInfo.Length,
                            DestinationLastWriteUtc = destinationInfo.LastWriteTimeUtc
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else if (request.OperationType == FileOperationType.Copy && Directory.Exists(sourcePath))
                {
                    var rootFolderName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    var destinationDirectory = BuildSafeDestinationPath(request.DestinationPath!, rootFolderName);
                    await CopyDirectoryRecursiveAsync(sourcePath, destinationDirectory, cancellationToken).ConfigureAwait(false);

                    await _activityLogRepository.AddOperationItemAsync(
                        new OperationItemRecord
                        {
                            ItemId = Guid.NewGuid().ToString("N"),
                            OperationId = operationId,
                            Kind = OperationItemKind.Unsupported,
                            SourcePath = sourcePath,
                            DestinationPath = destinationDirectory,
                            RecoveryPath = null,
                            DestinationSizeBytes = null,
                            DestinationLastWriteUtc = null
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else if (request.OperationType == FileOperationType.Delete)
                {
                    var recoveryPath = await MoveToRecoveryAsync(sourcePath, operationId, cancellationToken).ConfigureAwait(false);
                    await _activityLogRepository.AddOperationItemAsync(
                        new OperationItemRecord
                        {
                            ItemId = Guid.NewGuid().ToString("N"),
                            OperationId = operationId,
                            Kind = OperationItemKind.DeleteMovedToRecovery,
                            SourcePath = sourcePath,
                            DestinationPath = null,
                            RecoveryPath = recoveryPath,
                            DestinationSizeBytes = null,
                            DestinationLastWriteUtc = null
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else if (request.OperationType == FileOperationType.Move)
                {
                    if (string.IsNullOrWhiteSpace(request.DestinationPath))
                    {
                        throw new InvalidOperationException("Destination path is required for move operations.");
                    }

                    var moveResult = MoveItem(sourcePath, request.DestinationPath);
                    await _activityLogRepository.AddOperationItemAsync(
                        new OperationItemRecord
                        {
                            ItemId = Guid.NewGuid().ToString("N"),
                            OperationId = operationId,
                            Kind = OperationItemKind.MoveCompleted,
                            SourcePath = moveResult.OriginalPath,
                            DestinationPath = moveResult.NewPath,
                            RecoveryPath = null,
                            DestinationSizeBytes = null,
                            DestinationLastWriteUtc = null
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else if (request.OperationType == FileOperationType.Rename)
                {
                    if (string.IsNullOrWhiteSpace(request.DestinationPath))
                    {
                        throw new InvalidOperationException("Destination path is required for rename operations.");
                    }

                    var renameSource = sourcePath;
                    var renameTarget = request.DestinationPath;

                    if (File.Exists(renameSource))
                    {
                        File.Move(renameSource, renameTarget);
                    }
                    else if (Directory.Exists(renameSource))
                    {
                        Directory.Move(renameSource, renameTarget);
                    }
                    else
                    {
                        throw new FileNotFoundException("Source path does not exist for rename.", renameSource);
                    }

                    await _activityLogRepository.AddOperationItemAsync(
                        new OperationItemRecord
                        {
                            ItemId = Guid.NewGuid().ToString("N"),
                            OperationId = operationId,
                            Kind = OperationItemKind.RenameCompleted,
                            SourcePath = renameSource,
                            DestinationPath = renameTarget,
                            RecoveryPath = null,
                            DestinationSizeBytes = null,
                            DestinationLastWriteUtc = null
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else if (request.OperationType == FileOperationType.CreateFolder)
                {
                    var targetFolder = sourcePath;
                    if (string.IsNullOrWhiteSpace(targetFolder))
                    {
                        throw new InvalidOperationException("Create-folder path was empty.");
                    }

                    Directory.CreateDirectory(targetFolder);
                    await _activityLogRepository.AddOperationItemAsync(
                        new OperationItemRecord
                        {
                            ItemId = Guid.NewGuid().ToString("N"),
                            OperationId = operationId,
                            Kind = OperationItemKind.CreateFolderCreated,
                            SourcePath = targetFolder,
                            DestinationPath = null,
                            RecoveryPath = null,
                            DestinationSizeBytes = null,
                            DestinationLastWriteUtc = null
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw new FileNotFoundException("Source path does not exist.", sourcePath);
                }

                completedItems++;
                processedBytes += pathSizes.TryGetValue(sourcePath, out var itemSize) ? itemSize : 0;
                var processedProgressUnits = estimatedTotalBytes > 0
                    ? Math.Min(processedBytes, totalProgressUnits)
                    : completedItems;

                progress?.Report(new FileOperationProgress
                {
                    OperationId = operationId,
                    CurrentItemPath = sourcePath,
                    CompletedItems = completedItems,
                    TotalItems = request.SourcePaths.Count,
                    ProcessedBytes = processedProgressUnits,
                    TotalBytes = totalProgressUnits
                });
            }

            record.Status = FileOperationStatus.Completed;
            record.CompletedUtc = DateTimeOffset.UtcNow;
            record.UndoAvailable = true;
            await _activityLogRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            return record;
        }
        catch (OperationCanceledException)
        {
            record.Status = FileOperationStatus.Canceled;
            record.CompletedUtc = DateTimeOffset.UtcNow;
            record.ErrorMessage = "Operation canceled by user.";
            await _activityLogRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            record.Status = FileOperationStatus.Failed;
            record.CompletedUtc = DateTimeOffset.UtcNow;
            record.ErrorMessage = ex.Message;
            await _activityLogRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static string BuildSafeDestinationPath(string destinationRoot, string itemName)
    {
        var candidatePath = Path.Combine(destinationRoot, itemName);
        if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
        {
            return candidatePath;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(itemName);
        var extension = Path.GetExtension(itemName);
        var attempt = 2;

        while (true)
        {
            var proposedName = $"{nameWithoutExtension} ({attempt}){extension}";
            candidatePath = Path.Combine(destinationRoot, proposedName);
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidatePath;
            }

            attempt++;
        }
    }

    private async Task<string?> ResolveDestinationFilePathAsync(
        string destinationRoot,
        string sourcePath,
        ConflictResolution resolution,
        string operationId,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(sourcePath);
        var exactPath = Path.Combine(destinationRoot, fileName);

        if (!File.Exists(exactPath))
        {
            return exactPath;
        }

        return resolution switch
        {
            ConflictResolution.KeepBoth => BuildSafeDestinationPath(destinationRoot, fileName),
            ConflictResolution.Replace => BackupAndReturnReplaceTarget(exactPath, operationId),
            ConflictResolution.ReplaceIfSameOtherwiseKeepBoth => await ResolveSmartCopyConflictAsync(sourcePath, destinationRoot, exactPath, fileName, operationId, cancellationToken).ConfigureAwait(false),
            ConflictResolution.Cancel => null,
            _ => BuildSafeDestinationPath(destinationRoot, fileName)
        };
    }

    private async Task<string> ResolveSmartCopyConflictAsync(
        string sourcePath,
        string destinationRoot,
        string exactPath,
        string fileName,
        string operationId,
        CancellationToken cancellationToken)
    {
        var filesMatch = await AreFilesContentEqualAsync(sourcePath, exactPath, cancellationToken).ConfigureAwait(false);
        if (filesMatch)
        {
            return BackupAndReturnReplaceTarget(exactPath, operationId);
        }

        return BuildSafeDestinationPath(destinationRoot, fileName);
    }

    private static async Task<bool> AreFilesContentEqualAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);
        if (sourceInfo.Length != destinationInfo.Length)
        {
            return false;
        }

        var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        var destinationHash = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
        return sourceHash.AsSpan().SequenceEqual(destinationHash);
    }

    private string BackupAndReturnReplaceTarget(string destinationFilePath, string operationId)
    {
        var recoveryRoot = _appPathsProvider.GetRecoveryRoot();
        var replaceBackupRoot = Path.Combine(recoveryRoot, operationId, "replace-backup");
        Directory.CreateDirectory(replaceBackupRoot);

        var backupPath = BuildSafeDestinationPath(replaceBackupRoot, Path.GetFileName(destinationFilePath));
        File.Move(destinationFilePath, backupPath);
        return destinationFilePath;
    }

    private static async Task CopyDirectoryRecursiveAsync(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            await CopyFileStreamedAsync(file, destinationFile, cancellationToken).ConfigureAwait(false);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationChild = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            await CopyDirectoryRecursiveAsync(directory, destinationChild, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyFileStreamedAsync(string sourceFile, string destinationFile, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;

        await using var sourceStream = new FileStream(
            sourceFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var destinationStream = new FileStream(
            destinationFile,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await sourceStream.CopyToAsync(destinationStream, bufferSize, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyCopyChecksumAsync(string sourceFile, string destinationFile, CancellationToken cancellationToken)
    {
        var sourceHash = await ComputeSha256Async(sourceFile, cancellationToken).ConfigureAwait(false);
        var destinationHash = await ComputeSha256Async(destinationFile, cancellationToken).ConfigureAwait(false);
        if (!sourceHash.AsSpan().SequenceEqual(destinationHash))
        {
            throw new IOException($"Checksum verification failed for copied file: {destinationFile}");
        }
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> MoveToRecoveryAsync(string sourcePath, string operationId, CancellationToken cancellationToken)
    {
        var recoveryRoot = _appPathsProvider.GetRecoveryRoot();
        var operationRecoveryRoot = Path.Combine(recoveryRoot, operationId);
        Directory.CreateDirectory(operationRecoveryRoot);

        var itemName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var recoveryTarget = BuildSafeDestinationPath(operationRecoveryRoot, itemName);

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, recoveryTarget);
            await Task.CompletedTask;
            return recoveryTarget;
        }

        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, recoveryTarget);
            await Task.CompletedTask;
            return recoveryTarget;
        }

        throw new FileNotFoundException("Source path does not exist.", sourcePath);
    }

    private static (string OriginalPath, string NewPath) MoveItem(string sourcePath, string destinationRoot)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source path does not exist.", sourcePath);
        }

        Directory.CreateDirectory(destinationRoot);
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destinationPath = BuildSafeDestinationPath(destinationRoot, name);

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath);
        }
        else
        {
            Directory.Move(sourcePath, destinationPath);
        }

        return (sourcePath, destinationPath);
    }

    private static long EstimatePathBytes(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // Skip unreadable files in size estimation; operation execution still reports hard errors later.
            }
        }

        return total;
    }

    private static string? BuildSourcePathSummary(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0)
        {
            return null;
        }

        if (sourcePaths.Count == 1)
        {
            return sourcePaths[0];
        }

        return $"{sourcePaths[0]} (+{sourcePaths.Count - 1} more)";
    }
}
