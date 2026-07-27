using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.Core.Services;

public interface IActivityLogRepository
{
    Task UpsertAsync(FileOperationRecord operation, CancellationToken cancellationToken);

    Task<FileOperationRecord?> GetByOperationIdAsync(string operationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileOperationRecord>> GetRecentAsync(int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileOperationRecord>> GetInterruptedAsync(CancellationToken cancellationToken);

    Task AddOperationItemAsync(OperationItemRecord item, CancellationToken cancellationToken);

    Task<IReadOnlyList<OperationItemRecord>> GetOperationItemsAsync(string operationId, CancellationToken cancellationToken);

    Task MarkUndoConsumedAsync(string operationId, string note, CancellationToken cancellationToken);
}
