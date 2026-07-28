namespace LuiP.FileManager.Core.Models;

public enum FileOperationType
{
    Copy = 0,
    Move = 1,
    Rename = 2,
    Delete = 3,
    CreateFolder = 4,
    Replace = 5
}

public enum FileOperationStatus
{
    Planned = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    PartialFailed = 4,
    Canceled = 5
}

public enum ConflictResolution
{
    KeepBoth = 0,
    Replace = 1,
    Cancel = 2,
    ReplaceIfSameOtherwiseKeepBoth = 3
}

public sealed class FileOperationRequest
{
    public required FileOperationType OperationType { get; init; }

    public required IReadOnlyList<string> SourcePaths { get; init; }

    public string? DestinationPath { get; init; }

    public ConflictResolution ConflictResolution { get; init; } = ConflictResolution.KeepBoth;

    public bool ApplyToAllConflicts { get; init; }

    public bool VerifyChecksumForLargeFiles { get; init; }

    public long ChecksumThresholdBytes { get; init; } = 32L * 1024L * 1024L;
}

public sealed class FileOperationRecord
{
    public required string OperationId { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public required FileOperationType OperationType { get; init; }

    public required FileOperationStatus Status { get; set; }

    public required int ItemCount { get; init; }

    public required long TotalBytes { get; init; }

    public string? SourcePath { get; init; }

    public string? SourcePathsJson { get; init; }

    public string? DestinationPath { get; init; }

    public bool UndoAvailable { get; set; }

    public DateTimeOffset? UndoExpiryUtc { get; set; }

    public string? ErrorMessage { get; set; }
}
