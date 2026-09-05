namespace GuraFile.Storage;

public sealed record FileOperationRecoveryReport(
    int RecoveredIntentsCount,
    int ReconciledItemsCount,
    int IndeterminateIntentsCount,
    IReadOnlyList<string> IndeterminateDetails)
{
    public bool HasIndeterminateOperations => IndeterminateIntentsCount > 0;
}

public sealed record FileOperationIntentRecord(
    long Id,
    string CorrelationId,
    string OperationType,
    string CollisionPolicy,
    string Status,
    string CreatedUtc,
    string? CompletedUtc,
    IReadOnlyList<FileOperationIntentItemRecord> Items);

public sealed record FileOperationIntentItemRecord(
    long Id,
    long IntentId,
    string SourcePath,
    string? DestinationDirectory,
    string? TargetName,
    string? ExpectedTargetPath,
    string? ActualTargetPath,
    string? ShellStatus,
    string? CommitStatus,
    string? Error);
