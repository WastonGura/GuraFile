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

        var batchResult = await _executor.CopyAsync(
            sourcePaths,
            destinationDirectory,
            effectiveRoots,
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);

        return await CommitBatchAsync(
            batchResult.Items,
            isMove: false,
            effectiveRoots,
            snapshots,
            cancellationToken);
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

        var batchResult = await _executor.MoveAsync(
            sourcePaths,
            destinationDirectory,
            effectiveRoots,
            collisionPolicy,
            ownerWindow,
            progress,
            cancellationToken);

        return await CommitBatchAsync(
            batchResult.Items,
            isMove: true,
            effectiveRoots,
            snapshots,
            cancellationToken);
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

        var itemResult = await _executor.RenameAsync(
            normalizedSource,
            newName,
            effectiveRoots,
            collisionPolicy,
            ownerWindow,
            cancellationToken);

        var batchResult = await CommitBatchAsync(
            [itemResult],
            isMove: true,
            effectiveRoots,
            new Dictionary<string, SourceSnapshot>(StringComparer.OrdinalIgnoreCase) { [normalizedSource] = snapshot },
            cancellationToken);

        return batchResult.Items[0];
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

    private FileOperationCommitItemResult CommitSingleItem(
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

        // Check for external replacement on same-volume move/rename
        if (isMove && snapshot.DiskIdentity.IsStable && targetIdentity.IsStable)
        {
            var isSameVolume = string.Equals(
                targetIdentity.VolumeId,
                snapshot.DiskIdentity.VolumeId,
                StringComparison.OrdinalIgnoreCase);

            if (isSameVolume && !string.Equals(targetIdentity.FileId, snapshot.DiskIdentity.FileId, StringComparison.OrdinalIgnoreCase))
            {
                return new FileOperationCommitItemResult(
                    normalizedSource,
                    normalizedTarget,
                    FileOperationItemStatus.Failed,
                    $"目标文件身份与源文件不匹配（期望 {snapshot.DiskIdentity.FileId}，实际 {targetIdentity.FileId}），可能已被外部替换，拒绝提交并请重新扫描。");
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
        var isCrossVolumeOrCopy = !isMove ||
            !string.Equals(targetIdentity.VolumeId, snapshot.DiskIdentity.VolumeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(targetIdentity.FileId, snapshot.DiskIdentity.FileId, StringComparison.OrdinalIgnoreCase);

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
                    WHERE normalized_path = $sourcePath COLLATE NOCASE;
                    """;
                markSourceOffline.Parameters.AddWithValue("$sourcePath", normalizedSource);
                markSourceOffline.ExecuteNonQuery();

                if (snapshot.DbFileId.HasValue)
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
        // For directory operations, reconcile the target directory scope and mark old scope missing if moved
        Scanner.ReconcilePathsAsync(targetRoot.Id, [normalizedTarget]).GetAwaiter().GetResult();

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

    private SourceSnapshot QuerySourceSnapshot(SqliteConnection connection, string normalizedSource)
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

    private static IReadOnlyList<ManagedRoot> LoadRoots(SqliteConnection connection)
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

    private static ManagedRoot? FindMatchingRoot(IReadOnlyList<ManagedRoot> roots, string path)
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

    internal sealed record SourceSnapshot(
        string SourcePath,
        FileIdentity DiskIdentity,
        long? DbFileId,
        FileIdentity? DbIdentity,
        IReadOnlyList<string> UserTags,
        bool IsDirectory);
}
