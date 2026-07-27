namespace LuiP.FileManager.Core.Models;

public enum ShortcutTargetKind
{
    File = 0,
    Folder = 1,
    Program = 2
}

public sealed class ShortcutItem
{
    public required string Id { get; init; }

    public required string Label { get; set; }

    public required string TargetPath { get; set; }

    public required ShortcutTargetKind TargetKind { get; set; }

    public int SortOrder { get; set; }
}
