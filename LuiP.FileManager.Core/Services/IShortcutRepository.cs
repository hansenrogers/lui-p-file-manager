using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.Core.Services;

public interface IShortcutRepository
{
    Task<IReadOnlyList<ShortcutItem>> GetAllAsync(CancellationToken cancellationToken);

    Task UpsertAsync(ShortcutItem item, CancellationToken cancellationToken);

    Task DeleteAsync(string id, CancellationToken cancellationToken);
}
