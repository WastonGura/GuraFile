using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

[SupportedOSPlatform("windows")]
public sealed class FileOperationCrashRecoveryService
{
    private readonly string _databasePath;
    private readonly ManagedRootScanner _scanner;
    private readonly FileOperationIndexCommitter _committer;
    private readonly DiagnosticLogger _diagnosticLogger;

    public FileOperationCrashRecoveryService(
        string databasePath,
        ManagedRootScanner scanner,
        FileOperationIndexCommitter committer,
        DiagnosticLogger? diagnosticLogger = null)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _diagnosticLogger = diagnosticLogger ?? DiagnosticLogger.Default;
    }

    public async Task<FileOperationRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        return await _scanner.ExecuteWriteAsync(() =>
        {
            using var connection = SqliteDatabase.Open(_databasePath);

            var pendingIntents = LoadPendingIntents(connection);
            if (pendingIntents.Count == 0)
            {
                return new FileOperationRecoveryReport(0, 0, 0, Array.Empty<string>());
            }

            _diagnosticLogger.LogWarning(
                DiagnosticCategory.FileOperation,
                "FileOperationRecoveryDetected",
                correlationId: null,
                status: DiagnosticResultStatus.Started,
                message: $"检测到 {pendingIntents.Count} 个未决或中断的文件操作意图，启动安全恢复对齐。",
                properties: new Dictionary<string, object?>
                {
                    ["intent_count"] = pendingIntents.Count,
                    ["intent_ids"] = pendingIntents.Select(i => i.Id).ToArray()
                });

            var roots = FileOperationIndexCommitter.LoadRoots(connection);
            int recoveredCount = 0;
            int indeterminateCount = 0;
            int reconciledItemsCount = 0;
            var indeterminateDetails = new List<string>();

            foreach (var intent in pendingIntents)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                bool intentHasIndeterminate = false;
                var itemUpdates = new List<(string SourcePath, string CommitStatus, string? Error)>();

                switch (intent.OperationType.ToLowerInvariant())
                {
                    case "move":
                    case "rename":
                        foreach (var item in intent.Items)
                        {
                            var source = item.SourcePath;
                            var target = item.ActualTargetPath ?? item.ExpectedTargetPath;

                            if (string.IsNullOrWhiteSpace(target))
                            {
                                intentHasIndeterminate = true;
                                itemUpdates.Add((source, "indeterminate", "缺少目标路径信息，无法安全判定。"));
                                indeterminateDetails.Add($"[{intent.OperationType}/missing_target] {source}");
                                continue;
                            }

                            var sourceExists = File.Exists(source) || Directory.Exists(source);
                            var targetExists = File.Exists(target) || Directory.Exists(target);

                            if (targetExists && !sourceExists)
                            {
                                // Shell move succeeded before crash, reconcile index without writing to disk
                                var snapshot = _committer.QuerySourceSnapshot(connection, source);
                                var commitResult = _committer.CommitSingleItem(connection, roots, source, target, isMove: true, snapshot);
                                if (commitResult.Succeeded)
                                {
                                    itemUpdates.Add((source, "committed", null));
                                    reconciledItemsCount++;
                                }
                                else
                                {
                                    itemUpdates.Add((source, "failed", commitResult.Error));
                                }
                            }
                            else if (sourceExists && !targetExists)
                            {
                                // Crash occurred before Shell move executed; original file is untouched on disk
                                // Absolute prohibition: never write or move file during recovery
                                itemUpdates.Add((source, "failed", "操作在执行前中断，源文件未发生变动，已放弃该操作。"));
                            }
                            else if (sourceExists && targetExists)
                            {
                                // Both source and target exist on disk: check identities
                                var srcId = FileIdentityReader.Read(source);
                                var dstId = FileIdentityReader.Read(target);

                                if (srcId.IsStable && dstId.IsStable &&
                                    string.Equals(srcId.VolumeId, dstId.VolumeId, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(srcId.FileId, dstId.FileId, StringComparison.OrdinalIgnoreCase))
                                {
                                    var snapshot = _committer.QuerySourceSnapshot(connection, source);
                                    var commitResult = _committer.CommitSingleItem(connection, roots, source, target, isMove: true, snapshot);
                                    if (commitResult.Succeeded)
                                    {
                                        itemUpdates.Add((source, "committed", null));
                                        reconciledItemsCount++;
                                    }
                                    else
                                    {
                                        itemUpdates.Add((source, "failed", commitResult.Error));
                                    }
                                }
                                else
                                {
                                    // Ambiguity / Conflict: do not delete or overwrite either file
                                    intentHasIndeterminate = true;
                                    itemUpdates.Add((source, "indeterminate", $"源路径与目标路径均存在且身份不同，可能发生冲突：{source} -> {target}"));
                                    indeterminateDetails.Add($"[{intent.OperationType}/conflict] {source} -> {target}");
                                }
                            }
                            else
                            {
                                // Neither exists on disk: ambiguous, do not delete index record
                                intentHasIndeterminate = true;
                                itemUpdates.Add((source, "indeterminate", $"源路径与目标路径均不存在于磁盘：{source} -> {target}"));
                                indeterminateDetails.Add($"[{intent.OperationType}/missing] {source} -> {target}");
                            }
                        }
                        break;

                    case "copy":
                        foreach (var item in intent.Items)
                        {
                            var source = item.SourcePath;
                            var target = item.ActualTargetPath ?? item.ExpectedTargetPath;

                            if (string.IsNullOrWhiteSpace(target))
                            {
                                itemUpdates.Add((source, "failed", "缺少目标路径信息。"));
                                continue;
                            }

                            var targetExists = File.Exists(target) || Directory.Exists(target);
                            if (targetExists)
                            {
                                // Copied before crash; reconcile index
                                var snapshot = _committer.QuerySourceSnapshot(connection, source);
                                var commitResult = _committer.CommitSingleItem(connection, roots, source, target, isMove: false, snapshot);
                                if (commitResult.Succeeded)
                                {
                                    itemUpdates.Add((source, "committed", null));
                                    reconciledItemsCount++;
                                }
                                else
                                {
                                    itemUpdates.Add((source, "failed", commitResult.Error));
                                }
                            }
                            else
                            {
                                // Target not copied; never replay copy write
                                itemUpdates.Add((source, "failed", "复制操作未执行，已跳过。"));
                            }
                        }
                        break;

                    case "recycle_bin_delete":
                        foreach (var item in intent.Items)
                        {
                            var source = item.SourcePath;
                            var sourceExists = File.Exists(source) || Directory.Exists(source);

                            if (!sourceExists)
                            {
                                // Deleted to recycle bin before crash; mark offline in index
                                var matchingRoot = FileOperationIndexCommitter.FindMatchingRoot(roots, source);
                                if (matchingRoot is not null)
                                {
                                    var commitResult = FileOperationIndexCommitter.CommitSingleDeleteItem(connection, matchingRoot, source);
                                    if (commitResult.Succeeded)
                                    {
                                        itemUpdates.Add((source, "committed", null));
                                        reconciledItemsCount++;
                                    }
                                    else
                                    {
                                        itemUpdates.Add((source, "failed", commitResult.Error));
                                    }
                                }
                                else
                                {
                                    itemUpdates.Add((source, "failed", $"源路径未在任何在线管理根目录范围内：{source}"));
                                }
                            }
                            else
                            {
                                // Source still exists on disk; never delete during recovery
                                itemUpdates.Add((source, "failed", "删除操作未执行，源文件保留在线。"));
                            }
                        }
                        break;

                    default:
                        intentHasIndeterminate = true;
                        indeterminateDetails.Add($"[unknown_type] {intent.OperationType} (intent {intent.Id})");
                        break;
                }

                if (intentHasIndeterminate)
                {
                    _committer.UpdateIntentIndeterminate(connection, intent.Id, itemUpdates);
                    indeterminateCount++;
                }
                else
                {
                    _committer.UpdateIntentCommitted(connection, intent.Id, itemUpdates);
                    recoveredCount++;
                }
            }

            _committer.PurgeCommittedIntents(connection);

            var report = new FileOperationRecoveryReport(
                recoveredCount,
                reconciledItemsCount,
                indeterminateCount,
                indeterminateDetails);

            _diagnosticLogger.LogInfo(
                DiagnosticCategory.FileOperation,
                "FileOperationRecoveryCompleted",
                correlationId: null,
                status: indeterminateCount > 0 ? DiagnosticResultStatus.Success : DiagnosticResultStatus.Success,
                message: $"文件操作恢复对齐完成：已恢复 {recoveredCount} 个，对齐文件 {reconciledItemsCount} 个，需检查(indeterminate) {indeterminateCount} 个。",
                properties: new Dictionary<string, object?>
                {
                    ["recovered_count"] = recoveredCount,
                    ["reconciled_items_count"] = reconciledItemsCount,
                    ["indeterminate_count"] = indeterminateCount,
                    ["indeterminate_details"] = indeterminateDetails
                });

            return report;
        }, cancellationToken);
    }

    private static IReadOnlyList<FileOperationIntentRecord> LoadPendingIntents(SqliteConnection connection)
    {
        var intents = new List<FileOperationIntentRecord>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT id, correlation_id, operation_type, collision_policy, status, created_utc, completed_utc
                FROM file_operation_intents
                WHERE status IN ('pending', 'shell_completed')
                ORDER BY id ASC;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                intents.Add(new FileOperationIntentRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    Array.Empty<FileOperationIntentItemRecord>()));
            }
        }

        var result = new List<FileOperationIntentRecord>(intents.Count);
        foreach (var intent in intents)
        {
            var items = new List<FileOperationIntentItemRecord>();
            using (var itemCmd = connection.CreateCommand())
            {
                itemCmd.CommandText =
                    """
                    SELECT id, intent_id, source_path, destination_directory, target_name, expected_target_path, actual_target_path, shell_status, commit_status, error
                    FROM file_operation_intent_items
                    WHERE intent_id = $intentId
                    ORDER BY id ASC;
                    """;
                itemCmd.Parameters.AddWithValue("$intentId", intent.Id);
                using var itemReader = itemCmd.ExecuteReader();
                while (itemReader.Read())
                {
                    items.Add(new FileOperationIntentItemRecord(
                        itemReader.GetInt64(0),
                        itemReader.GetInt64(1),
                        itemReader.GetString(2),
                        itemReader.IsDBNull(3) ? null : itemReader.GetString(3),
                        itemReader.IsDBNull(4) ? null : itemReader.GetString(4),
                        itemReader.IsDBNull(5) ? null : itemReader.GetString(5),
                        itemReader.IsDBNull(6) ? null : itemReader.GetString(6),
                        itemReader.IsDBNull(7) ? null : itemReader.GetString(7),
                        itemReader.IsDBNull(8) ? "pending" : itemReader.GetString(8),
                        itemReader.IsDBNull(9) ? null : itemReader.GetString(9)));
                }
            }

            result.Add(intent with { Items = items });
        }

        return result;
    }
}
