using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.Core.Services;

public interface ILocationRepository
{
    Task<IReadOnlyList<ConfiguredLocation>> GetByKindAsync(LocationKind kind, CancellationToken cancellationToken);

    Task UpsertAsync(ConfiguredLocation location, CancellationToken cancellationToken);

    Task DeleteAsync(string id, LocationKind kind, CancellationToken cancellationToken);
}
