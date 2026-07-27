namespace LuiP.FileManager.Core.Models;

public enum LocationKind
{
    Source = 0,
    Destination = 1
}

public sealed class ConfiguredLocation
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public required string Path { get; set; }

    public required LocationKind Kind { get; init; }

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }
}
