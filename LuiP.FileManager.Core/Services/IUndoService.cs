using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.Core.Services;

public interface IUndoService
{
    Task<UndoResult> UndoAsync(string operationId, CancellationToken cancellationToken);
}
