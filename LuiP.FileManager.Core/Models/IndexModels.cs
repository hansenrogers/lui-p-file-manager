namespace LuiP.FileManager.Core.Models;

public enum DateFilterPreset
{
    AllDates = 0,
    Today = 1,
    Last7Days = 2,
    Last30Days = 3,
    ThisYear = 4
}

public sealed class IndexLocationStatus
{
    public required string LocationId { get; init; }

    public required LocationKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? LastIndexedUtc { get; init; }

    public string? LastError { get; init; }

    public long ItemCount { get; init; }
}

public sealed class IndexedSearchResult
{
    public required string LocationId { get; init; }

    public required LocationKind Kind { get; init; }

    public required string FullPath { get; init; }

    public required string Name { get; init; }

    public required bool IsFolder { get; init; }

    public long? SizeBytes { get; init; }

    public DateTimeOffset? ModifiedUtc { get; init; }
}
