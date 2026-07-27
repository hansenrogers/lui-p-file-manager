using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.Infrastructure.Services;

public sealed class StubFileOperationService : IFileOperationService
{
    public Task<FileOperationRecord> ExecuteAsync(
        FileOperationRequest request,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var totalItems = request.SourcePaths.Count;

        var record = new FileOperationRecord
        {
            OperationId = operationId,
            StartedUtc = DateTimeOffset.UtcNow,
            OperationType = request.OperationType,
            Status = FileOperationStatus.Planned,
            ItemCount = totalItems,
            TotalBytes = 0,
            SourcePath = request.SourcePaths.Count > 0 ? request.SourcePaths[0] : null,
            DestinationPath = request.DestinationPath,
            UndoAvailable = false
        };

        progress?.Report(new FileOperationProgress
        {
            OperationId = operationId,
            CurrentItemPath = totalItems > 0 ? request.SourcePaths[0] : string.Empty,
            CompletedItems = 0,
            TotalItems = totalItems,
            ProcessedBytes = 0,
            TotalBytes = 0
        });

        return Task.FromResult(record);
    }
}
