using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.Core.Services;

public interface IIndexService
{
    Task RebuildAllAsync(CancellationToken cancellationToken);

    Task RebuildLocationAsync(string locationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexLocationStatus>> GetLocationStatusesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexedSearchResult>> SearchAsync(
        LocationKind kind,
        string locationId,
        string query,
        DateFilterPreset dateFilter,
        int maxResults,
        CancellationToken cancellationToken);
}
