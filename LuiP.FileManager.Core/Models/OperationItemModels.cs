namespace LuiP.FileManager.Core.Models;

public enum OperationItemKind
{
    CopyCreatedFile = 0,
    DeleteMovedToRecovery = 1,
    MoveCompleted = 2,
    RenameCompleted = 3,
    CreateFolderCreated = 4,
    FolderTransferCompleted = 5,
    Unsupported = 99
}

public sealed class OperationItemRecord
{
    public required string ItemId { get; init; }

    public required string OperationId { get; init; }

    public required OperationItemKind Kind { get; init; }

    public required string SourcePath { get; init; }

    public string? DestinationPath { get; init; }

    public string? RecoveryPath { get; init; }

    public long? DestinationSizeBytes { get; init; }

    public DateTimeOffset? DestinationLastWriteUtc { get; init; }
}

public sealed class UndoResult
{
    public required bool Succeeded { get; init; }

    public required string Message { get; init; }

    public required int UndoneItems { get; init; }

    public required int TotalItems { get; init; }
}
