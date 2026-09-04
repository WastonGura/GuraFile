using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public sealed record FileOperationCommitItemResult(
    string SourcePath,
    string? ActualTargetPath,
    FileOperationItemStatus Status,
    string? Error = null,
    bool IsCanceled = false,
    long? PersistedFileId = null,
    IReadOnlyList<string>? UserTags = null,
    IReadOnlyList<string>? AutomaticTags = null)
{
    public bool Succeeded => Status == FileOperationItemStatus.Completed;

    public static FileOperationCommitItemResult FromItemResult(FileOperationItemResult item) =>
        new(item.SourcePath, item.ActualTargetPath, item.Status, item.Error, item.IsCanceled);
}

public sealed record FileOperationCommitBatchResult(
    IReadOnlyList<FileOperationCommitItemResult> Items,
    bool IsCanceled = false)
{
    public int TotalCount => Items.Count;
    public int SucceededCount => Items.Count(i => i.Status == FileOperationItemStatus.Completed);
    public int FailedCount => Items.Count(i => i.Status == FileOperationItemStatus.Failed);
    public int SkippedCount => Items.Count(i => i.Status == FileOperationItemStatus.Skipped);
    public int CanceledCount => Items.Count(i => i.Status == FileOperationItemStatus.Canceled);
}

[SupportedOSPlatform("windows")]
public sealed class FileOperationIndexCommitter
{
    private readonly SafeFileOperationExecutor? _executor;
    private readonly Func<string, FileIdentity> _readIdentity;
    private readonly Func<string, FileTypeClassification> _classify;
    private readonly Func<string, FileAttributes> _getAttributes;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;

    public FileOperationIndexCommitter(
        ManagedRootScanner scanner,
        SafeFileOperationExecutor? executor = null)
        : this(
            scanner.DatabasePath,
            scanner,
            executor ?? new SafeFileOperationExecutor(),
            FileIdentityReader.Read,
            new FileTypeClassifier().Classify,
            File.GetAttributes,
            File.Exists,
            Directory.Exists)
    {
    }

    internal FileOperationIndexCommitter(
        string databasePath,
        ManagedRootScanner scanner,
        SafeFileOperationExecutor? executor,
        Func<string, FileIdentity> readIdentity,
        Func<string, FileTypeClassification> classify,
        Func<string, FileAttributes> getAttributes,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(classify);
        ArgumentNullException.ThrowIfNull(getAttributes);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);

        DatabasePath = Path.GetFullPath(databasePath);
        Scanner = scanner;
        _executor = executor;
        _readIdentity = readIdentity;
        _classify = classify;
        _getAttributes = getAttributes;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
    }

    public string DatabasePath { get; }
    public ManagedRootScanner Scanner { get; }

    public async Task<FileOperationCommitBatchResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<string>? onlineRootPaths = null,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (_executor is null)
        {
            throw new InvalidOperationException("SafeFileOperationExecutor 未配置。");
        }

        var effectiveRoots = ResolveOnlineRoots(onlineRootPaths);
        var snapshots = TakeSourceSnapshots(sourcePaths);
        var correlationId = $"op-copy-{Guid.NewGuid():N}";

        long intentId;
        using (var connection = SqliteDatabase.Open(DatabasePath))
        {
            var intentItems = sourcePaths.Select(sp =>
            {
                var normSource = SafeFileOperationExecutor.Normalize(sp);
                var targetName = Path.GetFileName(normSource);
                var expectedTarget = Path.Combine(destinationDirectory, targetName);
                return (normSource, (string?)destinationDirectory, (string?)targetName, (string?)expectedTarget);
            }).ToList();

            intentId = InsertIntent(connection, correlationId, "copy", MapCollisionPolicy(collisionPolicy), intentItems);
        }

        FileOperationBatchResult batchResult;
        try
        {
            batchResult = await _executor.CopyAsync(
                sourcePaths,
                destinationDirectory,
                effectiveRoots,
                collisionPolicy,
                ownerWindow,
                progress,
                cancellationToken);

            using (var connection = SqliteDatabase.Open(DatabasePath))
            {
                var updates = batchResult.Items.Select(item =>
                    (SafeFileOperationExecutor.Normalize(item.SourcePath),
                     item.ActualTargetPath,
                     MapShellStatus(item.Status),
                     item.Error)).ToList();
                UpdateIntentShellCompleted(connection, intentId, updates);
            }
        }
        catch (Exception ex)
        {
            using (var connection = SqliteDatabase.Open(DatabasePath))
            {
                var failedUpdates = sourcePaths.Select(sp =>
                    (SafeFileOperationExecutor.Normalize(sp),
                     (string?)null,
                     "failed",
                     (string?)ex.Message)).ToList();
                UpdateIntentShellCompleted(connection, intentId, failedUpdates);
            }
            throw;
        }

        var commitResult = await CommitBatchAsync(
            batchResult.Items,
            isMove: false,
            effectiveRoots,
            snapshots,
            cancellationToken);

        using (var connection = SqliteDatabase.Open(DatabasePath))
        {
            var commitUpdates = commitResult.Items.Select(ci =>
                (SafeFileOperationExecutor.Normalize(ci.SourcePath),
                 ci.Succeeded ? "committed" : "failed",
                 ci.Error)).ToList();
            UpdateIntentCommitted(connection, intentId, commitUpdates);
            PurgeCommittedIntents(connection);
        }

        return commitResult;
    }

    public Task<FileOperationCommitBatchResult> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<ManagedRoot> onlineRoots,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onlineRoots);
        return CopyAsync(
            sourcePaths,
            destinationDirectory,
            onlineRoots.Where(r => r.Status == ManagedRootStatus.Online).Select(r => r.Path).ToArray(),
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public async Task<FileOperationCommitBatchResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<string>? onlineRootPaths = null,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (_executor is null)
        {
            throw new InvalidOperationException("SafeFileOperationExecutor 未配置。");
        }

        var effectiveRoots = ResolveOnlineRoots(onlineRootPaths);
        var snapshots = TakeSourceSnapshots(sourcePaths);
        var correlationId = $"op-move-{Guid.NewGuid():N}";

        long intentId;
        using (var connection = SqliteDatabase.Open(DatabasePath))
        {
            var intentItems = sourcePaths.Select(sp =>
            {
                var normSource = SafeFileOperationExecutor.Normalize(sp);
                var targetName = Path.GetFileName(normSource);
                var expectedTarget = Path.Combine(destinationDirectory, targetName);
                return (normSource, (string?)destinationDirectory, (string?)targetName, (string?)expectedTarget);
            }).ToList();

            intentId = InsertIntent(connection, correlationId, "move", MapCollisionPolicy(collisionPolicy), intentItems);
        }

        FileOperationBatchResult batchResult;
        try
        {
            batchResult = await _executor.MoveAsync(
                sourcePaths,
                destinationDirectory,
                effectiveRoots,
                collisionPolicy,
                ownerWindow,
                progress,
                cancellationToken);

            using (var connection = SqliteDatabase.Open(DatabasePath))
            {
                var updates = batchResult.Items.Select(item =>
                    (SafeFileOperationExecutor.Normalize(item.SourcePath),
                     item.ActualTargetPath,
                     MapShellStatus(item.Status),
                     item.Error)).ToList();
                UpdateIntentShellCompleted(connection, intentId, updates);
            }
        }
        catch (Exception ex)
        {
            using (var connection = SqliteDatabase.Open(DatabasePath))
            {
                var failedUpdates = sourcePaths.Select(sp =>
                    (SafeFileOperationExecutor.Normalize(sp),
                     (string?)null,
                     "failed",
                     (string?)ex.Message)).ToList();
                UpdateIntentShellCompleted(connection, intentId, failedUpdates);
            }
            throw;
        }

        var commitResult = await CommitBatchAsync(
            batchResult.Items,
            isMove: true,
            effectiveRoots,
            snapshots,
            cancellationToken);

        using (var connection = SqliteDatabase.Open(DatabasePath))
        {
            var commitUpdates = commitResult.Items.Select(ci =>
                (SafeFileOperationExecutor.Normalize(ci.SourcePath),
                 ci.Succeeded ? "committed" : "failed",
                 ci.Error)).ToList();
            UpdateIntentCommitted(connection, intentId, commitUpdates);
            PurgeCommittedIntents(connection);
        }

        return commitResult;
    }

    public Task<FileOperationCommitBatchResult> MoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        IReadOnlyCollection<ManagedRoot> onlineRoots,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onlineRoots);
        return MoveAsync(
            sourcePaths,
            destinationDirectory,
            onlineRoots.Where(r => r.Status == ManagedRootStatus.Online).Select(r => r.Path).ToArray(),
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);
    }

    public async Task<FileOperationCommitItemResult> RenameAsync(
        string sourcePath,
        string newName,
        IReadOnlyCollection<string>? onlineRootPaths = null,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        if (_executor is null)
        {
            throw new InvalidOperationException("SafeFileOperationExecutor 未配置。");
        }

        var effectiveRoots = ResolveOnlineRoots(onlineRootPaths);
        var normalizedSource = SafeFileOperationExecutor.Normalize(sourcePath);
        var snapshot = TakeSourceSnapshot(normalizedSource);
        var destDir = Path.GetDirectoryName(normalizedSource);
        var expectedTarget = destDir != null ? Path.Combine(destDir, newName) : newName;
        var correlationId = $"op-rename-{Guid.NewGuid():N}";

        long intentId;
        using (var connection = SqliteDatabase.Open(DatabasePath))
        {
            intentId = InsertIntent(
                connection,
                correlationId,
                "rename",
                MapCollisionPolicy(collisionPolicy),
                [(normalizedSource, destDir, newName, expectedTarget)]);
        }

        FileOperationItemResult itemResult;
        try
        {
            itemResult = await _executor.RenameAsync(
                normalizedSource,
                newName,
                effectiveRoots,
                collisionPolicy,
                ownerWindow,
                cancellationToken);

            using (var connection = SqliteDatabase.Open(DatabasePath))
            {
                UpdateIntentShellCompleted(
                    connection,
                    intentId,
                    [(normalizedSource, itemResult.ActualTargetPath, MapShellStatus(itemResult.Status), itemResult.Error)]);
            }
        }
        catch (Exception ex)
        {
            using (var connection = SqliteDatabase.Open(DatabasePath))
            {
                UpdateIntentShellCompleted(
                    connection,
                    intentId,
                    [(normalizedSource, null, "failed", ex.Message)]);
            }
            throw;
        }

        var batchResult = await CommitBatchAsync(
            [itemResult],
            isMove: true,
            effectiveRoots,
            new Dictionary<string, SourceSnapshot>(StringComparer.OrdinalIgnoreCase) { [normalizedSource] = snapshot },
            cancellationToken);

        var commitItem = batchResult.Items[0];
        using (var connection = SqliteDatabase.Open(DatabasePath))
        {
            UpdateIntentCommitted(
                connection,
                intentId,
                [(normalizedSource, commitItem.Succeeded ? "committed" : "failed", commitItem.Error)]);
            PurgeCommittedIntents(connection);
        }

        return commitItem;
    }

    public Task<FileOperationCommitItemResult> RenameAsync(
        string sourcePath,
        string newName,
        IReadOnlyCollection<ManagedRoot> onlineRoots,
        FileCollisionPolicy collisionPolicy = FileCollisionPolicy.AutoRename,
        IntPtr ownerWindow = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onlineRoots);
        return RenameAsync(
            sourcePath,
            newName,
            onlineRoots.Where(r => r.Status == ManagedRootStatus.Online).Select(r => r.Path).ToArray(),
            collisionPolicy,
            ownerWindow,
            cancellationToken);
    }

    public async Task<FileOperationCommitBatchResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyCollection<string>? onlineRootPaths = null,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        if (_executor is null)
        {
            throw new InvalidOperationException("SafeFileOperationExecutor 未配置。");
        }

        var effectiveRoots = ResolveOnlineRoots(onlineRootPaths);
        var correlationId = $"op-delete-{Guid.NewGuid():N}";

        long intentId;
        using (var connection = SqliteDatabase.Open(DatabasePath))
        {
            var intentItems = sourcePaths.Select(sp =>
            {
                var norm = SafeFileOperationExecutor.Normalize(sp);
                return (norm, (string?)null, (string?)null, (string?)null);
            }).ToList();

            intentId = InsertIntent(connection, correlationId, "recycle_bin_delete", "auto_rename", intentItems);
        }

        FileOperationBatchResult batchResult;
        try
        {
            batchResult = await _executor.DeleteToRecycleBinAsync(
                sourcePaths,
                effectiveRoots,
                ownerWindow,
                progress,
                cancellationToken);

            using (var connection = SqliteDatabase.Open(DatabasePath))
            {
                var updates = batchResult.Items.Select(item =>
                    (SafeFileOperationExecutor.Normalize(item.SourcePath),
                     item.ActualTargetPath,
                     MapShellStatus(item.Status),
                     item.Error)).ToList();
                UpdateIntentShellCompleted(connection, intentId, updates);
            }
        }
        catch (Exception ex)
        {
            using (var connection = SqliteDatabase.Open(DatabasePath))
            {
                var failedUpdates = sourcePaths.Select(sp =>
                    (SafeFileOperationExecutor.Normalize(sp),
                     (string?)null,
                     "failed",
                     (string?)ex.Message)).ToList();
                UpdateIntentShellCompleted(connection, intentId, failedUpdates);
            }
            throw;
        }

        var commitResult = await CommitDeleteBatchAsync(
            batchResult.Items,
            effectiveRoots,
            cancellationToken);

        using (var connection = SqliteDatabase.Open(DatabasePath))
        {
            var commitUpdates = commitResult.Items.Select(ci =>
                (SafeFileOperationExecutor.Normalize(ci.SourcePath),
                 ci.Succeeded ? "committed" : "failed",
                 ci.Error)).ToList();
            UpdateIntentCommitted(connection, intentId, commitUpdates);
            PurgeCommittedIntents(connection);
        }

        return commitResult;
    }

    public Task<FileOperationCommitBatchResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyCollection<ManagedRoot> onlineRoots,
        IntPtr ownerWindow = default,
        Action<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onlineRoots);
        return DeleteToRecycleBinAsync(
            sourcePaths,
            onlineRoots.Where(r => r.Status == ManagedRootStatus.Online).Select(r => r.Path).ToArray(),
            ownerWindow,
            progress,
            cancellationToken);
    }

    internal Task<FileOperationCommitBatchResult> CommitDeleteBatchAsync(
        IReadOnlyList<FileOperationItemResult> items,
        IReadOnlyCollection<string> onlineRootPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(onlineRootPaths);

        return Scanner.ExecuteWriteAsync(() =>
        {
            using var connection = SqliteDatabase.Open(DatabasePath);
            var roots = LoadRoots(connection);
            var results = new List<FileOperationCommitItemResult>(items.Count);

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(new FileOperationCommitItemResult(
                        item.SourcePath,
                        null,
                        FileOperationItemStatus.Canceled,
                        "操作已被取消。",
                        IsCanceled: true));
                    continue;
                }

                if (!item.Succeeded)
                {
                    results.Add(FileOperationCommitItemResult.FromItemResult(item));
                    continue;
                }

                string normalizedSource;
                try
                {
                    normalizedSource = SafeFileOperationExecutor.Normalize(item.SourcePath);
                }
                catch (Exception ex)
                {
                    results.Add(new FileOperationCommitItemResult(
                        item.SourcePath,
                        null,
                        FileOperationItemStatus.Failed,
                        $"路径解析错误：{ex.Message}"));
                    continue;
                }

                var sourceStillExists = _fileExists(normalizedSource) || _directoryExists(normalizedSource);
                if (sourceStillExists)
                {
                    results.Add(new FileOperationCommitItemResult(
                        normalizedSource,
                        null,
                        FileOperationItemStatus.Failed,
                        $"文件仍存在于磁盘上，未能移入回收站：{normalizedSource}"));
                    continue;
                }

                var matchingRoot = FindMatchingRoot(roots, normalizedSource);
                if (matchingRoot is null)
                {
                    results.Add(new FileOperationCommitItemResult(
                        normalizedSource,
                        null,
                        FileOperationItemStatus.Failed,
                        $"源路径“{normalizedSource}”不在任何在线管理根目录范围内。"));
                    continue;
                }

                var commitResult = CommitSingleDeleteItem(connection, matchingRoot, normalizedSource);
                results.Add(commitResult);
            }

            return new FileOperationCommitBatchResult(
                results,
                results.Any(r => r.IsCanceled) || cancellationToken.IsCancellationRequested);
        }, cancellationToken);
    }

    internal static FileOperationCommitItemResult CommitSingleDeleteItem(
        SqliteConnection connection,
        ManagedRoot root,
        string normalizedSource)
    {
        try
        {
            using var transaction = connection.BeginTransaction();
            var prefix = Path.EndsInDirectorySeparator(normalizedSource)
                ? normalizedSource
                : normalizedSource + Path.DirectorySeparatorChar;

            using (var markMissing = connection.CreateCommand())
            {
                markMissing.Transaction = transaction;
                markMissing.CommandText =
                    """
                    UPDATE files SET is_online = 0
                    WHERE root_id = $rootId
                      AND is_online = 1
                      AND (normalized_path = $path COLLATE NOCASE OR substr(normalized_path, 1, length($prefix)) = $prefix COLLATE NOCASE);
                    """;
                markMissing.Parameters.AddWithValue("$rootId", root.Id);
                markMissing.Parameters.AddWithValue("$path", normalizedSource);
                markMissing.Parameters.AddWithValue("$prefix", prefix);
                markMissing.ExecuteNonQuery();
            }

            transaction.Commit();

            return new FileOperationCommitItemResult(
                normalizedSource,
                null,
                FileOperationItemStatus.Completed);
        }
        catch (Exception ex)
        {
            return new FileOperationCommitItemResult(
                normalizedSource,
                null,
                FileOperationItemStatus.Failed,
                $"提交删除索引时发生错误：{ex.Message}");
        }
    }

    internal Task<FileOperationCommitBatchResult> CommitBatchAsync(
        IReadOnlyList<FileOperationItemResult> items,
        bool isMove,
        IReadOnlyCollection<string> onlineRootPaths,
        IReadOnlyDictionary<string, SourceSnapshot>? snapshots = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(onlineRootPaths);

        return Scanner.ExecuteWriteAsync(() =>
        {
            using var connection = SqliteDatabase.Open(DatabasePath);
            var roots = LoadRoots(connection);
            var results = new List<FileOperationCommitItemResult>(items.Count);

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(new FileOperationCommitItemResult(
                        item.SourcePath,
                        item.ActualTargetPath,
                        FileOperationItemStatus.Canceled,
                        "操作已被取消。",
                        IsCanceled: true));
                    continue;
                }

                if (!item.Succeeded || string.IsNullOrWhiteSpace(item.ActualTargetPath))
                {
                    results.Add(FileOperationCommitItemResult.FromItemResult(item));
                    continue;
                }

                string normalizedSource;
                string normalizedTarget;
                try
                {
                    normalizedSource = SafeFileOperationExecutor.Normalize(item.SourcePath);
                    normalizedTarget = SafeFileOperationExecutor.Normalize(item.ActualTargetPath);
                }
                catch (Exception ex)
                {
                    results.Add(new FileOperationCommitItemResult(
                        item.SourcePath,
                        item.ActualTargetPath,
                        FileOperationItemStatus.Failed,
                        $"路径解析错误：{ex.Message}"));
                    continue;
                }

                SourceSnapshot snapshot;
                if (snapshots != null && snapshots.TryGetValue(normalizedSource, out var existingSnapshot))
                {
                    snapshot = existingSnapshot;
                }
                else
                {
                    snapshot = QuerySourceSnapshot(connection, normalizedSource);
                }

                var commitItemResult = CommitSingleItem(
                    connection,
                    roots,
                    normalizedSource,
                    normalizedTarget,
                    isMove,
                    snapshot);

                results.Add(commitItemResult);
            }

            return new FileOperationCommitBatchResult(
                results,
                results.Any(r => r.IsCanceled) || cancellationToken.IsCancellationRequested);
        }, cancellationToken);
    }

    internal FileOperationCommitItemResult CommitSingleItem(
        SqliteConnection connection,
        IReadOnlyList<ManagedRoot> roots,
        string normalizedSource,
        string normalizedTarget,
        bool isMove,
        SourceSnapshot snapshot)
    {
        var targetExists = _fileExists(normalizedTarget) || _directoryExists(normalizedTarget);
        if (!targetExists)
        {
            return new FileOperationCommitItemResult(
                normalizedSource,
                normalizedTarget,
                FileOperationItemStatus.Failed,
                $"目标文件不存在或无法访问：{normalizedTarget}");
        }

        var matchingRoot = FindMatchingRoot(roots, normalizedTarget);
        if (matchingRoot is null)
        {
            return new FileOperationCommitItemResult(
                normalizedSource,
                normalizedTarget,
                FileOperationItemStatus.Failed,
                $"目标路径“{normalizedTarget}”不在任何在线管理根目录范围内。");
        }

        var targetIdentity = _readIdentity(normalizedTarget);
        var sourceIdentity = snapshot.DiskIdentity.IsStable
            ? snapshot.DiskIdentity
            : (snapshot.DbIdentity?.IsStable == true ? snapshot.DbIdentity : null);

        // Check for external replacement on same-volume move/rename
        if (isMove && sourceIdentity?.IsStable == true && targetIdentity.IsStable)
        {
            var isSameVolume = string.Equals(
                targetIdentity.VolumeId,
                sourceIdentity.VolumeId,
                StringComparison.OrdinalIgnoreCase);

            if (isSameVolume && !string.Equals(targetIdentity.FileId, sourceIdentity.FileId, StringComparison.OrdinalIgnoreCase))
            {
                return new FileOperationCommitItemResult(
                    normalizedSource,
                    normalizedTarget,
                    FileOperationItemStatus.Failed,
                    $"目标文件身份与源文件不匹配（期望 {sourceIdentity.FileId}，实际 {targetIdentity.FileId}），可能已被外部替换，拒绝提交并请重新扫描。");
            }
        }

        try
        {
            var isTargetDirectory = (_getAttributes(normalizedTarget) & FileAttributes.Directory) != 0;
            if (isTargetDirectory)
            {
                return CommitDirectoryOperation(
                    connection,
                    matchingRoot,
                    normalizedSource,
                    normalizedTarget,
                    isMove,
                    snapshot);
            }

            return CommitFileOperation(
                connection,
                matchingRoot,
                normalizedSource,
                normalizedTarget,
                isMove,
                snapshot,
                targetIdentity);
        }
        catch (Exception ex)
        {
            return new FileOperationCommitItemResult(
                normalizedSource,
                normalizedTarget,
                FileOperationItemStatus.Failed,
                $"提交索引时发生错误：{ex.Message}");
        }
    }

    private FileOperationCommitItemResult CommitFileOperation(
        SqliteConnection connection,
        ManagedRoot targetRoot,
        string normalizedSource,
        string normalizedTarget,
        bool isMove,
        SourceSnapshot snapshot,
        FileIdentity targetIdentity)
    {
        var targetFile = new FileInfo(normalizedTarget);
        var targetSize = targetFile.Length;
        var targetModifiedUtc = targetFile.LastWriteTimeUtc.ToString("O");
        var targetName = targetFile.Name;
        var targetExtension = targetFile.Extension;
        var scanToken = Guid.NewGuid().ToString("N");

        var classification = _classify(normalizedTarget);

        using var transaction = connection.BeginTransaction();

        // 1. Mark any other file online at target path as offline
        using (var releasePath = connection.CreateCommand())
        {
            releasePath.Transaction = transaction;
            releasePath.CommandText =
                """
                UPDATE files SET is_online = 0
                WHERE normalized_path = $normalizedPath COLLATE NOCASE
                  AND is_online = 1
                  AND NOT (volume_id = $volumeId AND file_id = $fileId);
                """;
            releasePath.Parameters.AddWithValue("$normalizedPath", normalizedTarget);
            releasePath.Parameters.AddWithValue("$volumeId", targetIdentity.VolumeId);
            releasePath.Parameters.AddWithValue("$fileId", targetIdentity.FileId);
            releasePath.ExecuteNonQuery();
        }

        // 2. Mark any descendants offline if target path was previously a directory
        using (var releaseDescendants = connection.CreateCommand())
        {
            releaseDescendants.Transaction = transaction;
            releaseDescendants.CommandText =
                "UPDATE files SET is_online = 0 WHERE root_id = $rootId AND is_online = 1 AND substr(normalized_path, 1, length($prefix)) = $prefix COLLATE NOCASE;";
            releaseDescendants.Parameters.AddWithValue("$rootId", targetRoot.Id);
            releaseDescendants.Parameters.AddWithValue("$prefix", normalizedTarget + Path.DirectorySeparatorChar);
            releaseDescendants.ExecuteNonQuery();
        }

        long persistedFileId;

        // Check if this is a path-fallback move where source had a DB record
        if (isMove && !targetIdentity.IsStable && snapshot.DbFileId.HasValue &&
            string.Equals(snapshot.DbIdentity?.VolumeId, "path-fallback", StringComparison.OrdinalIgnoreCase))
        {
            using var updatePathFallback = connection.CreateCommand();
            updatePathFallback.Transaction = transaction;
            updatePathFallback.CommandText =
                """
                UPDATE files SET
                    root_id = $rootId,
                    volume_id = 'path-fallback',
                    file_id = $fileId,
                    path = $path,
                    normalized_path = $normalizedPath,
                    name = $name,
                    extension = $extension,
                    size = $size,
                    modified_utc = $modifiedUtc,
                    identity_kind = 'path',
                    identity_diagnostic = $identityDiagnostic,
                    is_online = 1,
                    scan_token = $scanToken
                WHERE id = $id;
                """;
            updatePathFallback.Parameters.AddWithValue("$rootId", targetRoot.Id);
            updatePathFallback.Parameters.AddWithValue("$fileId", normalizedTarget);
            updatePathFallback.Parameters.AddWithValue("$path", normalizedTarget);
            updatePathFallback.Parameters.AddWithValue("$normalizedPath", normalizedTarget);
            updatePathFallback.Parameters.AddWithValue("$name", targetName);
            updatePathFallback.Parameters.AddWithValue("$extension", targetExtension);
            updatePathFallback.Parameters.AddWithValue("$size", targetSize);
            updatePathFallback.Parameters.AddWithValue("$modifiedUtc", targetModifiedUtc);
            updatePathFallback.Parameters.AddWithValue("$identityDiagnostic", (object?)targetIdentity.Diagnostic ?? DBNull.Value);
            updatePathFallback.Parameters.AddWithValue("$scanToken", scanToken);
            updatePathFallback.Parameters.AddWithValue("$id", snapshot.DbFileId.Value);
            updatePathFallback.ExecuteNonQuery();

            persistedFileId = snapshot.DbFileId.Value;
        }
        else
        {
            using var upsertFile = connection.CreateCommand();
            upsertFile.Transaction = transaction;
            upsertFile.CommandText =
                """
                INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token)
                VALUES ($rootId, $volumeId, $fileId, $path, $normalizedPath, $name, $extension, $size, $modifiedUtc, $identityKind, $identityDiagnostic, 1, $scanToken)
                ON CONFLICT(volume_id, file_id) DO UPDATE SET
                    root_id = excluded.root_id,
                    path = excluded.path,
                    normalized_path = excluded.normalized_path,
                    name = excluded.name,
                    extension = excluded.extension,
                    size = excluded.size,
                    modified_utc = excluded.modified_utc,
                    identity_kind = excluded.identity_kind,
                    identity_diagnostic = excluded.identity_diagnostic,
                    is_online = 1,
                    scan_token = excluded.scan_token
                RETURNING id;
                """;
            upsertFile.Parameters.AddWithValue("$rootId", targetRoot.Id);
            upsertFile.Parameters.AddWithValue("$volumeId", targetIdentity.VolumeId);
            upsertFile.Parameters.AddWithValue("$fileId", targetIdentity.FileId);
            upsertFile.Parameters.AddWithValue("$path", normalizedTarget);
            upsertFile.Parameters.AddWithValue("$normalizedPath", normalizedTarget);
            upsertFile.Parameters.AddWithValue("$name", targetName);
            upsertFile.Parameters.AddWithValue("$extension", targetExtension);
            upsertFile.Parameters.AddWithValue("$size", targetSize);
            upsertFile.Parameters.AddWithValue("$modifiedUtc", targetModifiedUtc);
            upsertFile.Parameters.AddWithValue("$identityKind", targetIdentity.IsStable ? "stable" : "path");
            upsertFile.Parameters.AddWithValue("$identityDiagnostic", (object?)targetIdentity.Diagnostic ?? DBNull.Value);
            upsertFile.Parameters.AddWithValue("$scanToken", scanToken);

            persistedFileId = (long)upsertFile.ExecuteScalar()!;
        }

        // 3. Update automatic tags
        TagService.ReplaceAutomaticTags(connection, transaction, persistedFileId, classification.AutomaticTags);

        // 4. Inherit or preserve user tags
        var sourceIdentity = snapshot.DiskIdentity.IsStable
            ? snapshot.DiskIdentity
            : (snapshot.DbIdentity?.IsStable == true ? snapshot.DbIdentity : null);

        var isCrossVolumeOrCopy = !isMove ||
            sourceIdentity == null ||
            !sourceIdentity.IsStable ||
            !targetIdentity.IsStable ||
            !string.Equals(targetIdentity.VolumeId, sourceIdentity.VolumeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(targetIdentity.FileId, sourceIdentity.FileId, StringComparison.OrdinalIgnoreCase);

        if (isCrossVolumeOrCopy)
        {
            using (var clearUserTags = connection.CreateCommand())
            {
                clearUserTags.Transaction = transaction;
                clearUserTags.CommandText = "DELETE FROM file_tags WHERE file_id = $fileId AND source = 'user';";
                clearUserTags.Parameters.AddWithValue("$fileId", persistedFileId);
                clearUserTags.ExecuteNonQuery();
            }

            if (snapshot.UserTags.Count > 0)
            {
                foreach (var userTagName in snapshot.UserTags)
                {
                    var (displayName, normalizedTagName) = TagService.NormalizeName(userTagName);
                    long tagId;
                    using (var tagCmd = connection.CreateCommand())
                    {
                        tagCmd.Transaction = transaction;
                        tagCmd.CommandText =
                            """
                            INSERT INTO tags (name, normalized_name, source)
                            VALUES ($name, $normalizedName, 'user')
                            ON CONFLICT(normalized_name, source) DO UPDATE SET name = excluded.name
                            RETURNING id;
                            """;
                        tagCmd.Parameters.AddWithValue("$name", displayName);
                        tagCmd.Parameters.AddWithValue("$normalizedName", normalizedTagName);
                        tagId = (long)tagCmd.ExecuteScalar()!;
                    }

                    using var relationCmd = connection.CreateCommand();
                    relationCmd.Transaction = transaction;
                    relationCmd.CommandText =
                        "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fileId, $tagId, 'user') ON CONFLICT DO NOTHING;";
                    relationCmd.Parameters.AddWithValue("$fileId", persistedFileId);
                    relationCmd.Parameters.AddWithValue("$tagId", tagId);
                    relationCmd.ExecuteNonQuery();
                }
            }
        }

        // 5. If cross-volume move and source file has disappeared from disk, mark source node offline
        if (isMove && isCrossVolumeOrCopy)
        {
            var sourceStillExists = _fileExists(normalizedSource);
            if (!sourceStillExists)
            {
                using var markSourceOffline = connection.CreateCommand();
                markSourceOffline.Transaction = transaction;
                markSourceOffline.CommandText =
                    """
                    UPDATE files SET is_online = 0
                    WHERE normalized_path = $sourcePath COLLATE NOCASE
                      AND id != $persistedFileId;
                    """;
                markSourceOffline.Parameters.AddWithValue("$sourcePath", normalizedSource);
                markSourceOffline.Parameters.AddWithValue("$persistedFileId", persistedFileId);
                markSourceOffline.ExecuteNonQuery();

                if (snapshot.DbFileId.HasValue && snapshot.DbFileId.Value != persistedFileId)
                {
                    using var markDbOffline = connection.CreateCommand();
                    markDbOffline.Transaction = transaction;
                    markDbOffline.CommandText = "UPDATE files SET is_online = 0 WHERE id = $id;";
                    markDbOffline.Parameters.AddWithValue("$id", snapshot.DbFileId.Value);
                    markDbOffline.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();

        var currentUserTags = LoadUserTagsForFile(connection, persistedFileId);
        var currentAutoTags = LoadAutomaticTagsForFile(connection, persistedFileId);

        return new FileOperationCommitItemResult(
            normalizedSource,
            normalizedTarget,
            FileOperationItemStatus.Completed,
            PersistedFileId: persistedFileId,
            UserTags: currentUserTags,
            AutomaticTags: currentAutoTags);
    }

    private FileOperationCommitItemResult CommitDirectoryOperation(
        SqliteConnection connection,
        ManagedRoot targetRoot,
        string normalizedSource,
        string normalizedTarget,
        bool isMove,
        SourceSnapshot snapshot)
    {
        // 1. Reconcile the target directory scope without deadlocking (using ReconcilePathsDirect)
        Scanner.ReconcilePathsDirect(targetRoot.Id, [normalizedTarget]);

        // 2. If directory copy, inherit user tags for files in target directory from source directory
        if (!isMove)
        {
            InheritDirectoryUserTags(connection, normalizedSource, normalizedTarget);
        }

        // 3. If move and source directory no longer exists on disk, mark old scope as offline
        if (isMove && !_directoryExists(normalizedSource))
        {
            var sourceRoot = FindMatchingRoot(LoadRoots(connection), normalizedSource);
            if (sourceRoot != null)
            {
                var prefix = Path.EndsInDirectorySeparator(normalizedSource)
                    ? normalizedSource
                    : normalizedSource + Path.DirectorySeparatorChar;

                using var transaction = connection.BeginTransaction();
                using var markMissing = connection.CreateCommand();
                markMissing.Transaction = transaction;
                markMissing.CommandText =
                    """
                    UPDATE files SET is_online = 0
                    WHERE root_id = $rootId
                      AND is_online = 1
                      AND (normalized_path = $path COLLATE NOCASE OR substr(normalized_path, 1, length($prefix)) = $prefix COLLATE NOCASE);
                    """;
                markMissing.Parameters.AddWithValue("$rootId", sourceRoot.Id);
                markMissing.Parameters.AddWithValue("$path", normalizedSource);
                markMissing.Parameters.AddWithValue("$prefix", prefix);
                markMissing.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        return new FileOperationCommitItemResult(
            normalizedSource,
            normalizedTarget,
            FileOperationItemStatus.Completed);
    }

    private static void InheritDirectoryUserTags(
        SqliteConnection connection,
        string sourceDir,
        string targetDir)
    {
        var targetPrefix = Path.EndsInDirectorySeparator(targetDir)
            ? targetDir
            : targetDir + Path.DirectorySeparatorChar;

        var targetFiles = new List<(long Id, string Path)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT id, normalized_path
                FROM files
                WHERE (normalized_path = $targetDir COLLATE NOCASE OR substr(normalized_path, 1, length($prefix)) = $prefix COLLATE NOCASE)
                  AND is_online = 1;
                """;
            cmd.Parameters.AddWithValue("$targetDir", targetDir);
            cmd.Parameters.AddWithValue("$prefix", targetPrefix);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                targetFiles.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        if (targetFiles.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();

        foreach (var (targetFileId, targetFilePath) in targetFiles)
        {
            var relativePath = Path.GetRelativePath(targetDir, targetFilePath);
            var expectedSourcePath = SafeFileOperationExecutor.Normalize(Path.Combine(sourceDir, relativePath));

            var sourceUserTags = new List<string>();
            using (var sourceCmd = connection.CreateCommand())
            {
                sourceCmd.Transaction = transaction;
                sourceCmd.CommandText =
                    """
                    SELECT t.name
                    FROM tags t
                    JOIN file_tags ft ON ft.tag_id = t.id
                    JOIN files f ON f.id = ft.file_id
                    WHERE f.normalized_path = $srcPath COLLATE NOCASE
                      AND ft.source = 'user' AND t.source = 'user'
                    ORDER BY t.name COLLATE NOCASE;
                    """;
                sourceCmd.Parameters.AddWithValue("$srcPath", expectedSourcePath);
                using var reader = sourceCmd.ExecuteReader();
                while (reader.Read())
                {
                    sourceUserTags.Add(reader.GetString(0));
                }
            }

            if (sourceUserTags.Count > 0)
            {
                using var clearCmd = connection.CreateCommand();
                clearCmd.Transaction = transaction;
                clearCmd.CommandText = "DELETE FROM file_tags WHERE file_id = $fileId AND source = 'user';";
                clearCmd.Parameters.AddWithValue("$fileId", targetFileId);
                clearCmd.ExecuteNonQuery();

                foreach (var userTagName in sourceUserTags)
                {
                    var (displayName, normalizedTagName) = TagService.NormalizeName(userTagName);
                    long tagId;
                    using (var tagCmd = connection.CreateCommand())
                    {
                        tagCmd.Transaction = transaction;
                        tagCmd.CommandText =
                            """
                            INSERT INTO tags (name, normalized_name, source)
                            VALUES ($name, $normalizedName, 'user')
                            ON CONFLICT(normalized_name, source) DO UPDATE SET name = excluded.name
                            RETURNING id;
                            """;
                        tagCmd.Parameters.AddWithValue("$name", displayName);
                        tagCmd.Parameters.AddWithValue("$normalizedName", normalizedTagName);
                        tagId = (long)tagCmd.ExecuteScalar()!;
                    }

                    using var relCmd = connection.CreateCommand();
                    relCmd.Transaction = transaction;
                    relCmd.CommandText =
                        "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fileId, $tagId, 'user') ON CONFLICT DO NOTHING;";
                    relCmd.Parameters.AddWithValue("$fileId", targetFileId);
                    relCmd.Parameters.AddWithValue("$tagId", tagId);
                    relCmd.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
    }

    private Dictionary<string, SourceSnapshot> TakeSourceSnapshots(IReadOnlyList<string> sourcePaths)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        var snapshots = new Dictionary<string, SourceSnapshot>(sourcePaths.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var path in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var normalized = SafeFileOperationExecutor.Normalize(path);
                if (!snapshots.ContainsKey(normalized))
                {
                    snapshots[normalized] = QuerySourceSnapshot(connection, normalized);
                }
            }
            catch
            {
            }
        }

        return snapshots;
    }

    private SourceSnapshot TakeSourceSnapshot(string normalizedSource)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        return QuerySourceSnapshot(connection, normalizedSource);
    }

    internal SourceSnapshot QuerySourceSnapshot(SqliteConnection connection, string normalizedSource)
    {
        var diskIdentity = _readIdentity(normalizedSource);
        var isDirectory = _directoryExists(normalizedSource);

        long? dbFileId = null;
        FileIdentity? dbIdentity = null;
        var userTags = new List<string>();

        // Query by normalized online path first
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT f.id, f.volume_id, f.file_id, f.identity_kind, f.identity_diagnostic
                FROM files f
                WHERE f.normalized_path = $normalizedPath COLLATE NOCASE AND f.is_online = 1
                ORDER BY f.id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$normalizedPath", normalizedSource);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                dbFileId = reader.GetInt64(0);
                var vol = reader.GetString(1);
                var fid = reader.GetString(2);
                var kind = reader.GetString(3);
                var diag = reader.IsDBNull(4) ? null : reader.GetString(4);
                dbIdentity = new FileIdentity(vol, fid, kind == "stable", diag);
            }
        }

        // Fallback query by stable identity
        if (dbFileId is null && diskIdentity.IsStable)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT f.id, f.volume_id, f.file_id, f.identity_kind, f.identity_diagnostic
                FROM files f
                WHERE f.volume_id = $volumeId AND f.file_id = $fileId
                ORDER BY f.id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$volumeId", diskIdentity.VolumeId);
            command.Parameters.AddWithValue("$fileId", diskIdentity.FileId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                dbFileId = reader.GetInt64(0);
                var vol = reader.GetString(1);
                var fid = reader.GetString(2);
                var kind = reader.GetString(3);
                var diag = reader.IsDBNull(4) ? null : reader.GetString(4);
                dbIdentity = new FileIdentity(vol, fid, kind == "stable", diag);
            }
        }

        if (dbFileId.HasValue)
        {
            userTags.AddRange(LoadUserTagsForFile(connection, dbFileId.Value));
        }

        return new SourceSnapshot(
            normalizedSource,
            diskIdentity,
            dbFileId,
            dbIdentity,
            userTags,
            isDirectory);
    }

    private static IReadOnlyList<string> LoadUserTagsForFile(SqliteConnection connection, long fileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.name
            FROM tags t
            JOIN file_tags ft ON ft.tag_id = t.id
            WHERE ft.file_id = $fileId AND ft.source = 'user' AND t.source = 'user'
            ORDER BY t.name COLLATE NOCASE, t.id;
            """;
        command.Parameters.AddWithValue("$fileId", fileId);
        using var reader = command.ExecuteReader();
        var tags = new List<string>();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    private static IReadOnlyList<string> LoadAutomaticTagsForFile(SqliteConnection connection, long fileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.name
            FROM tags t
            JOIN file_tags ft ON ft.tag_id = t.id
            WHERE ft.file_id = $fileId AND ft.source = 'automatic' AND t.source = 'automatic'
            ORDER BY t.name COLLATE NOCASE, t.id;
            """;
        command.Parameters.AddWithValue("$fileId", fileId);
        using var reader = command.ExecuteReader();
        var tags = new List<string>();
        while (reader.Read())
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    private IReadOnlyList<string> ResolveOnlineRoots(IReadOnlyCollection<string>? onlineRootPaths)
    {
        if (onlineRootPaths != null && onlineRootPaths.Count > 0)
        {
            return onlineRootPaths.ToArray();
        }

        return Scanner.ListRoots()
            .Where(r => r.Status == ManagedRootStatus.Online)
            .Select(r => r.Path)
            .ToArray();
    }

    internal static IReadOnlyList<ManagedRoot> LoadRoots(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path, status, last_error, last_checked_utc FROM roots;";
        using var reader = command.ExecuteReader();
        var roots = new List<ManagedRoot>();
        while (reader.Read())
        {
            var status = reader.GetString(2) switch
            {
                "online" => ManagedRootStatus.Online,
                "offline" => ManagedRootStatus.Offline,
                "recovering" => ManagedRootStatus.Recovering,
                _ => ManagedRootStatus.Online
            };

            roots.Add(new ManagedRoot(
                reader.GetInt64(0),
                reader.GetString(1),
                status,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4))));
        }

        return roots;
    }

    internal static ManagedRoot? FindMatchingRoot(IReadOnlyList<ManagedRoot> roots, string path)
    {
        var normalizedPath = SafeFileOperationExecutor.Normalize(path);
        foreach (var root in roots)
        {
            var normalizedRoot = SafeFileOperationExecutor.Normalize(root.Path);
            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                IsAncestor(normalizedRoot, normalizedPath))
            {
                return root;
            }
        }

        return null;
    }

    private static bool IsAncestor(string parent, string child) =>
        child.Length > parent.Length
        && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
        && (Path.EndsInDirectorySeparator(parent) || IsDirectorySeparator(child[parent.Length]));

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    internal static string MapCollisionPolicy(FileCollisionPolicy policy) => policy switch
    {
        FileCollisionPolicy.Overwrite => "overwrite",
        FileCollisionPolicy.Skip => "skip",
        _ => "auto_rename",
    };

    internal static string MapShellStatus(FileOperationItemStatus status) => status switch
    {
        FileOperationItemStatus.Completed => "completed",
        FileOperationItemStatus.Skipped => "skipped",
        FileOperationItemStatus.Canceled => "canceled",
        _ => "failed",
    };

    public long InsertIntent(
        SqliteConnection connection,
        string correlationId,
        string operationType,
        string collisionPolicy,
        IReadOnlyList<(string SourcePath, string? DestinationDirectory, string? TargetName, string? ExpectedTargetPath)> items)
    {
        var nowUtc = DateTime.UtcNow.ToString("O");
        using var tx = connection.BeginTransaction();
        long intentId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO file_operation_intents (correlation_id, operation_type, collision_policy, status, created_utc)
                VALUES ($correlationId, $operationType, $collisionPolicy, 'pending', $createdUtc)
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$correlationId", correlationId);
            cmd.Parameters.AddWithValue("$operationType", operationType);
            cmd.Parameters.AddWithValue("$collisionPolicy", collisionPolicy);
            cmd.Parameters.AddWithValue("$createdUtc", nowUtc);
            intentId = (long)cmd.ExecuteScalar()!;
        }

        foreach (var item in items)
        {
            using var itemCmd = connection.CreateCommand();
            itemCmd.Transaction = tx;
            itemCmd.CommandText =
                """
                INSERT INTO file_operation_intent_items (intent_id, source_path, destination_directory, target_name, expected_target_path, shell_status, commit_status)
                VALUES ($intentId, $sourcePath, $destDir, $targetName, $expectedTarget, NULL, 'pending');
                """;
            itemCmd.Parameters.AddWithValue("$intentId", intentId);
            itemCmd.Parameters.AddWithValue("$sourcePath", SafeFileOperationExecutor.Normalize(item.SourcePath));
            itemCmd.Parameters.AddWithValue("$destDir", (object?)item.DestinationDirectory ?? DBNull.Value);
            itemCmd.Parameters.AddWithValue("$targetName", (object?)item.TargetName ?? DBNull.Value);
            itemCmd.Parameters.AddWithValue("$expectedTarget", (object?)item.ExpectedTargetPath ?? DBNull.Value);
            itemCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return intentId;
    }

    public void UpdateIntentShellCompleted(
        SqliteConnection connection,
        long intentId,
        IReadOnlyList<(string SourcePath, string? ActualTargetPath, string ShellStatus, string? Error)> itemUpdates)
    {
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE file_operation_intents SET status = 'shell_completed' WHERE id = $intentId;";
            cmd.Parameters.AddWithValue("$intentId", intentId);
            cmd.ExecuteNonQuery();
        }

        foreach (var update in itemUpdates)
        {
            var normalizedSource = SafeFileOperationExecutor.Normalize(update.SourcePath);
            var normalizedShellStatus = update.ShellStatus.ToLowerInvariant() switch
            {
                "completed" or "succeeded" => "completed",
                "skipped" => "skipped",
                "canceled" => "canceled",
                _ => "failed",
            };

            using var itemCmd = connection.CreateCommand();
            itemCmd.Transaction = tx;
            itemCmd.CommandText =
                """
                UPDATE file_operation_intent_items SET
                    actual_target_path = $actualTarget,
                    shell_status = $shellStatus,
                    error = $error
                WHERE intent_id = $intentId AND source_path = $sourcePath COLLATE NOCASE;
                """;
            itemCmd.Parameters.AddWithValue("$intentId", intentId);
            itemCmd.Parameters.AddWithValue("$actualTarget", (object?)update.ActualTargetPath ?? DBNull.Value);
            itemCmd.Parameters.AddWithValue("$shellStatus", normalizedShellStatus);
            itemCmd.Parameters.AddWithValue("$error", (object?)update.Error ?? DBNull.Value);
            itemCmd.Parameters.AddWithValue("$sourcePath", normalizedSource);
            itemCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void UpdateIntentCommitted(
        SqliteConnection connection,
        long intentId,
        IReadOnlyList<(string SourcePath, string CommitStatus, string? Error)> itemUpdates)
    {
        var nowUtc = DateTime.UtcNow.ToString("O");
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                UPDATE file_operation_intents SET
                    status = 'committed',
                    completed_utc = $completedUtc
                WHERE id = $intentId;
                """;
            cmd.Parameters.AddWithValue("$intentId", intentId);
            cmd.Parameters.AddWithValue("$completedUtc", nowUtc);
            cmd.ExecuteNonQuery();
        }

        foreach (var update in itemUpdates)
        {
            var normalizedSource = SafeFileOperationExecutor.Normalize(update.SourcePath);
            using var itemCmd = connection.CreateCommand();
            itemCmd.Transaction = tx;
            itemCmd.CommandText =
                """
                UPDATE file_operation_intent_items SET
                    commit_status = $commitStatus,
                    error = coalesce($error, error)
                WHERE intent_id = $intentId AND source_path = $sourcePath COLLATE NOCASE;
                """;
            itemCmd.Parameters.AddWithValue("$intentId", intentId);
            itemCmd.Parameters.AddWithValue("$commitStatus", update.CommitStatus);
            itemCmd.Parameters.AddWithValue("$error", (object?)update.Error ?? DBNull.Value);
            itemCmd.Parameters.AddWithValue("$sourcePath", normalizedSource);
            itemCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void UpdateIntentIndeterminate(
        SqliteConnection connection,
        long intentId,
        IReadOnlyList<(string SourcePath, string CommitStatus, string? Error)> itemUpdates)
    {
        var nowUtc = DateTime.UtcNow.ToString("O");
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                UPDATE file_operation_intents SET
                    status = 'indeterminate',
                    completed_utc = $completedUtc
                WHERE id = $intentId;
                """;
            cmd.Parameters.AddWithValue("$intentId", intentId);
            cmd.Parameters.AddWithValue("$completedUtc", nowUtc);
            cmd.ExecuteNonQuery();
        }

        foreach (var update in itemUpdates)
        {
            var normalizedSource = SafeFileOperationExecutor.Normalize(update.SourcePath);
            using var itemCmd = connection.CreateCommand();
            itemCmd.Transaction = tx;
            itemCmd.CommandText =
                """
                UPDATE file_operation_intent_items SET
                    commit_status = $commitStatus,
                    error = coalesce($error, error)
                WHERE intent_id = $intentId AND source_path = $sourcePath COLLATE NOCASE;
                """;
            itemCmd.Parameters.AddWithValue("$intentId", intentId);
            itemCmd.Parameters.AddWithValue("$commitStatus", update.CommitStatus);
            itemCmd.Parameters.AddWithValue("$error", (object?)update.Error ?? DBNull.Value);
            itemCmd.Parameters.AddWithValue("$sourcePath", normalizedSource);
            itemCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public string GetIntentStatus(SqliteConnection connection, long intentId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM file_operation_intents WHERE id = $intentId;";
        cmd.Parameters.AddWithValue("$intentId", intentId);
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? string.Empty;
    }

    public void PurgeCommittedIntents(
        SqliteConnection connection,
        int maxCommittedToRetain = 100,
        int retentionDays = 14)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O");
        using var tx = connection.BeginTransaction();

        // 1. Delete committed older than retentionDays
        using (var deleteOldCmd = connection.CreateCommand())
        {
            deleteOldCmd.Transaction = tx;
            deleteOldCmd.CommandText =
                """
                DELETE FROM file_operation_intents
                WHERE status = 'committed' AND created_utc < $cutoff;
                """;
            deleteOldCmd.Parameters.AddWithValue("$cutoff", cutoff);
            deleteOldCmd.ExecuteNonQuery();
        }

        // 2. Bound retention: keep at most maxCommittedToRetain committed intents
        using (var boundCmd = connection.CreateCommand())
        {
            boundCmd.Transaction = tx;
            boundCmd.CommandText =
                """
                DELETE FROM file_operation_intents
                WHERE status = 'committed'
                  AND id NOT IN (
                      SELECT id FROM file_operation_intents
                      WHERE status = 'committed'
                      ORDER BY id DESC
                      LIMIT $maxRetain
                  );
                """;
            boundCmd.Parameters.AddWithValue("$maxRetain", maxCommittedToRetain);
            boundCmd.ExecuteNonQuery();
        }

        // 3. Clean up orphaned intent items (in case FK cascade is off)
        using (var orphanCmd = connection.CreateCommand())
        {
            orphanCmd.Transaction = tx;
            orphanCmd.CommandText =
                """
                DELETE FROM file_operation_intent_items
                WHERE intent_id NOT IN (SELECT id FROM file_operation_intents);
                """;
            orphanCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    internal sealed record SourceSnapshot(
        string SourcePath,
        FileIdentity DiskIdentity,
        long? DbFileId,
        FileIdentity? DbIdentity,
        IReadOnlyList<string> UserTags,
        bool IsDirectory);
}
