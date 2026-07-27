using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.Core.Services;

public interface IFileOperationService
{
    Task<FileOperationRecord> ExecuteAsync(
        FileOperationRequest request,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class FileOperationProgress
{
    public required string OperationId { get; init; }

    public required string CurrentItemPath { get; init; }

    public required int CompletedItems { get; init; }

    public required int TotalItems { get; init; }

    public required long ProcessedBytes { get; init; }

    public required long TotalBytes { get; init; }
}
