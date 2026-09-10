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
    private readonly Func<string, FileIdentity> _readIdentity;

    public FileOperationCrashRecoveryService(
        string databasePath,
        ManagedRootScanner scanner,
        FileOperationIndexCommitter committer,
        DiagnosticLogger? diagnosticLogger = null)
        : this(databasePath, scanner, committer, diagnosticLogger, null)
    {
    }

    internal FileOperationCrashRecoveryService(
        string databasePath,
        ManagedRootScanner scanner,
        FileOperationIndexCommitter committer,
        DiagnosticLogger? diagnosticLogger,
        Func<string, FileIdentity>? readIdentity)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        _diagnosticLogger = diagnosticLogger ?? DiagnosticLogger.Default;
        _readIdentity = readIdentity ?? FileIdentityReader.Read;
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

                if (string.Equals(intent.Status, "indeterminate", StringComparison.OrdinalIgnoreCase))
                {
                    indeterminateCount++;
                    if (intent.Items.Count > 0)
                    {
                        foreach (var item in intent.Items)
                        {
                            var target = item.ActualTargetPath ?? item.ExpectedTargetPath;
                            var desc = !string.IsNullOrWhiteSpace(item.Error)
                                ? item.Error
                                : $"{item.SourcePath} -> {target}";
                            indeterminateDetails.Add($"[{intent.OperationType}/indeterminate] {desc}");
                        }
                    }
                    else
                    {
                        indeterminateDetails.Add($"[{intent.OperationType}/indeterminate] (intent {intent.Id})");
                    }
                    continue;
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

                            if (string.Equals(item.ShellStatus, "skipped", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(item.ShellStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(item.ShellStatus, "canceled", StringComparison.OrdinalIgnoreCase))
                            {
                                itemUpdates.Add((source, "failed", item.Error ?? "操作已跳过或未执行。"));
                                continue;
                            }

                            var sourceExists = File.Exists(source) || Directory.Exists(source);
                            var targetExists = File.Exists(target) || Directory.Exists(target);

                            if (targetExists && !sourceExists)
                            {
                                var normalizedTarget = SafeFileOperationExecutor.Normalize(target);
                                var targetDiskId = _readIdentity(normalizedTarget);
                                var snapshot = _committer.QuerySourceSnapshot(connection, source);

                                var sourceOriginalIdentity = snapshot.DiskIdentity.IsStable
                                    ? snapshot.DiskIdentity
                                    : (snapshot.DbIdentity?.IsStable == true ? snapshot.DbIdentity : null);

                                bool canShortCircuit = false;
                                if (targetDiskId.IsStable && IsTargetAlreadyIndexedWithIdentity(connection, normalizedTarget, targetDiskId))
                                {
                                    var targetFileId = GetIndexedFileId(connection, normalizedTarget, targetDiskId);
                                    var targetUserTags = targetFileId.HasValue
                                        ? LoadUserTagsForFile(connection, targetFileId.Value)
                                        : Array.Empty<string>();
                                    var targetHasAllSourceTags = snapshot.UserTags.All(tag => targetUserTags.Contains(tag, StringComparer.OrdinalIgnoreCase));

                                    if (sourceOriginalIdentity != null && sourceOriginalIdentity.IsStable)
                                    {
                                        var isSameStableIdentity = string.Equals(targetDiskId.VolumeId, sourceOriginalIdentity.VolumeId, StringComparison.OrdinalIgnoreCase) &&
                                                                   string.Equals(targetDiskId.FileId, sourceOriginalIdentity.FileId, StringComparison.OrdinalIgnoreCase);
                                        if (isSameStableIdentity && targetHasAllSourceTags)
                                        {
                                            canShortCircuit = true;
                                        }
                                    }
                                    else if (sourceOriginalIdentity == null && snapshot.UserTags.Count == 0)
                                    {
                                        // 同卷移动后扫描器已将节点路径更新至目标，源路径在数据库中已无记录
                                        canShortCircuit = true;
                                    }
                                }

                                if (canShortCircuit)
                                {
                                    itemUpdates.Add((source, "committed", null));
                                    reconciledItemsCount++;
                                    continue;
                                }

                                // 检查跨卷移动或身份不一致场景下目标是否已被外部替换或已存在用户标签
                                var isSameIdentity = sourceOriginalIdentity != null && sourceOriginalIdentity.IsStable && targetDiskId.IsStable &&
                                                     string.Equals(targetDiskId.VolumeId, sourceOriginalIdentity.VolumeId, StringComparison.OrdinalIgnoreCase) &&
                                                     string.Equals(targetDiskId.FileId, sourceOriginalIdentity.FileId, StringComparison.OrdinalIgnoreCase);

                                if (!isSameIdentity)
                                {
                                    long? targetNodeId = null;
                                    if (targetDiskId.IsStable)
                                    {
                                        targetNodeId = GetIndexedFileId(connection, normalizedTarget, targetDiskId);
                                        if (!targetNodeId.HasValue)
                                        {
                                            using var findIdCmd = connection.CreateCommand();
                                            findIdCmd.CommandText =
                                                """
                                                SELECT id FROM files
                                                WHERE volume_id = $vol AND file_id = $fid AND is_online = 1
                                                ORDER BY id DESC LIMIT 1;
                                                """;
                                            findIdCmd.Parameters.AddWithValue("$vol", targetDiskId.VolumeId);
                                            findIdCmd.Parameters.AddWithValue("$fid", targetDiskId.FileId);
                                            var foundId = findIdCmd.ExecuteScalar();
                                            if (foundId is long id)
                                            {
                                                targetNodeId = id;
                                            }
                                        }
                                    }

                                    if (!targetNodeId.HasValue)
                                    {
                                        using var findTargetCmd = connection.CreateCommand();
                                        findTargetCmd.CommandText =
                                            """
                                            SELECT id FROM files
                                            WHERE normalized_path = $path COLLATE NOCASE AND is_online = 1
                                            ORDER BY id DESC LIMIT 1;
                                            """;
                                        findTargetCmd.Parameters.AddWithValue("$path", normalizedTarget);
                                        var found = findTargetCmd.ExecuteScalar();
                                        if (found is long idVal)
                                        {
                                            targetNodeId = idVal;
                                        }
                                    }

                                    var existingTargetUserTags = targetNodeId.HasValue
                                        ? LoadUserTagsForFile(connection, targetNodeId.Value)
                                        : Array.Empty<string>();

                                    if (existingTargetUserTags.Count > 0)
                                    {
                                        var targetTagSet = new HashSet<string>(existingTargetUserTags, StringComparer.OrdinalIgnoreCase);
                                        var sourceTagSet = new HashSet<string>(snapshot.UserTags, StringComparer.OrdinalIgnoreCase);
                                        if (!targetTagSet.SetEquals(sourceTagSet))
                                        {
                                            intentHasIndeterminate = true;
                                            itemUpdates.Add((source, "indeterminate", "目标路径已被具有用户标签的文件占用，未执行覆盖，请人工核实。"));
                                            indeterminateDetails.Add($"[{intent.OperationType}/target_occupied_with_tags] {source} -> {target}");
                                            continue;
                                        }
                                    }
                                }

                                // Shell move succeeded before crash, reconcile index without writing to disk
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
                                var srcId = _readIdentity(source);
                                var dstId = _readIdentity(target);

                                if (srcId.IsStable && dstId.IsStable &&
                                    string.Equals(srcId.VolumeId, dstId.VolumeId, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(srcId.FileId, dstId.FileId, StringComparison.OrdinalIgnoreCase))
                                {
                                    var normalizedTarget = SafeFileOperationExecutor.Normalize(target);
                                    if (IsTargetAlreadyIndexedWithIdentity(connection, normalizedTarget, dstId))
                                    {
                                        itemUpdates.Add((source, "committed", null));
                                        reconciledItemsCount++;
                                        continue;
                                    }

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

                            // 若 intent.Status 为 pending：
                            // 物理写盘后崩溃可能导致状态停留在 pending 但目标已落盘，绝不能静默判为未修改
                            if (string.Equals(intent.Status, "pending", StringComparison.OrdinalIgnoreCase))
                            {
                                if (targetExists)
                                {
                                    intentHasIndeterminate = true;
                                    var err = "执行状态未决：操作记录为 pending 但目标文件已存在，未执行覆盖，请核实目标文件及标签";
                                    itemUpdates.Add((source, "indeterminate", err));
                                    indeterminateDetails.Add($"[{intent.OperationType}/pending_target_exists] {source} -> {target}");
                                }
                                else
                                {
                                    itemUpdates.Add((source, "failed", "操作在执行前中断，已安全放弃，未修改目标文件。"));
                                }
                                continue;
                            }

                            // 若 item 状态在崩溃前已记录为 skipped、failed 或 canceled，绝不继承源标签
                            if (string.Equals(item.ShellStatus, "skipped", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(item.ShellStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(item.ShellStatus, "canceled", StringComparison.OrdinalIgnoreCase))
                            {
                                itemUpdates.Add((source, "failed", item.Error ?? "文件已跳过或未执行。"));
                                continue;
                            }

                            // 只有在 intent.Status == "shell_completed" 且 item 未被跳过/失败的前提下，才继续执行索引对账
                            if (!string.Equals(intent.Status, "shell_completed", StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(item.ShellStatus, "completed", StringComparison.OrdinalIgnoreCase))
                            {
                                itemUpdates.Add((source, "failed", "Shell 操作未成功完成，已安全放弃。"));
                                continue;
                            }

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
                WHERE status IN ('pending', 'shell_completed', 'indeterminate')
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

    private static long? GetIndexedFileId(SqliteConnection connection, string normalizedTarget, FileIdentity targetDiskId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, volume_id, file_id
            FROM files
            WHERE normalized_path = $path COLLATE NOCASE AND is_online = 1
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$path", normalizedTarget);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var dbId = reader.GetInt64(0);
            var dbVol = reader.GetString(1);
            var dbFid = reader.GetString(2);
            if (string.Equals(dbVol, targetDiskId.VolumeId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(dbFid, targetDiskId.FileId, StringComparison.OrdinalIgnoreCase))
            {
                return dbId;
            }
        }
        return null;
    }

    private static bool IsTargetAlreadyIndexedWithIdentity(SqliteConnection connection, string normalizedTarget, FileIdentity targetDiskId)
    {
        return GetIndexedFileId(connection, normalizedTarget, targetDiskId).HasValue;
    }

    private static IReadOnlyList<string> LoadUserTagsForFile(SqliteConnection connection, long fileId)
    {
        var tags = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT t.name
            FROM tags t
            INNER JOIN file_tags ft ON t.id = ft.tag_id
            WHERE ft.file_id = $fileId AND ft.source = 'user'
            ORDER BY t.name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$fileId", fileId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }
        return tags;
    }
}
