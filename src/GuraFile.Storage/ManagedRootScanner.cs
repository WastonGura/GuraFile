using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public enum ManagedRootStatus
{
    Online,
    Offline,
    Recovering
}

public sealed record ManagedRoot(
    long Id,
    string Path,
    ManagedRootStatus Status = ManagedRootStatus.Online,
    string? LastError = null,
    DateTimeOffset? LastCheckedUtc = null,
    StorageCapability? Capability = null)
{
    public string DisplayName
    {
        get
        {
            if (Status == ManagedRootStatus.Offline)
            {
                return $"{Path}  [离线]{ErrorSuffix}";
            }

            if (Status == ManagedRootStatus.Recovering)
            {
                return $"{Path}  [正在恢复]{ErrorSuffix}";
            }

            var cap = Capability ?? StorageCapabilityService.Default.Probe(Path);
            var statusTag = cap.SupportsStableFileId
                ? $"[在线 · {cap.FileSystemName}]"
                : "[在线 · 身份跟踪有限]";

            return $"{Path}  {statusTag}{ErrorSuffix}";
        }
    }

    private string ErrorSuffix => string.IsNullOrWhiteSpace(LastError) ? "" : $"  {LastError}";
}

public sealed record ScanFailure(string Path, string Error);

public sealed record ScanProgress(int DiscoveredFiles, int CommittedFiles, int FailedItems);

public sealed record ScanResult(
    int DiscoveredFiles,
    int CommittedFiles,
    int AddedFiles,
    int UpdatedFiles,
    int MissingFiles,
    int FallbackFiles,
    bool Canceled,
    IReadOnlyList<ScanFailure> Failures,
    int SkippedReparsePoints = 0);

public sealed record ScanSessionRecord(
    long Id,
    long RootId,
    string ScanToken,
    string ScanType,
    string Status,
    string StartedUtc,
    string? CompletedUtc);

public sealed class ManagedRootScanner
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Func<string, FileIdentity> _readIdentity;
    private readonly Func<string, string[]> _getFileSystemEntries;
    private readonly Func<string, FileAttributes> _getAttributes;
    private readonly Func<string, FileTypeClassification> _classify;
    private readonly DiagnosticLogger _logger;

    internal Action? OnBeforeMarkMissing { get; set; }
    internal Action<int>? OnBatchCommitted { get; set; }

    public ManagedRootScanner(string databasePath, DiagnosticLogger? logger = null) :
        this(
            databasePath,
            FileIdentityReader.Read,
            Directory.GetFileSystemEntries,
            File.GetAttributes,
            new FileTypeClassifier().Classify,
            logger)
    {
    }

    internal ManagedRootScanner(string databasePath, Func<string, FileIdentity> readIdentity) :
        this(databasePath, readIdentity, Directory.GetFileSystemEntries, File.GetAttributes)
    {
    }

    internal ManagedRootScanner(
        string databasePath,
        Func<string, FileIdentity> readIdentity,
        Func<string, string[]> getFileSystemEntries) :
        this(databasePath, readIdentity, getFileSystemEntries, File.GetAttributes)
    {
    }

    internal ManagedRootScanner(
        string databasePath,
        Func<string, FileIdentity> readIdentity,
        Func<string, string[]> getFileSystemEntries,
        Func<string, FileAttributes> getAttributes)
        : this(databasePath, readIdentity, getFileSystemEntries, getAttributes, new FileTypeClassifier().Classify)
    {
    }

    internal ManagedRootScanner(
        string databasePath,
        Func<string, FileIdentity> readIdentity,
        Func<string, string[]> getFileSystemEntries,
        Func<string, FileAttributes> getAttributes,
        Func<string, FileTypeClassification> classify,
        DiagnosticLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(getFileSystemEntries);
        ArgumentNullException.ThrowIfNull(getAttributes);
        ArgumentNullException.ThrowIfNull(classify);
        DatabasePath = Path.GetFullPath(databasePath);
        _readIdentity = readIdentity;
        _getFileSystemEntries = getFileSystemEntries;
        _getAttributes = getAttributes;
        _classify = classify;
        _logger = logger ?? DiagnosticLogger.Default;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var _ = SqliteDatabase.Open(DatabasePath);
    }

    public string DatabasePath { get; }
    public DiagnosticLogger Logger => _logger;

    public ManagedRoot AddRoot(string path)
    {
        var fullPath = Normalize(path);
        _writeGate.Wait();
        try
        {
            using var connection = SqliteDatabase.Open(DatabasePath);
            using var transaction = connection.BeginTransaction();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id, path FROM roots;";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var existing = new ManagedRoot(reader.GetInt64(0), reader.GetString(1));
                    if (string.Equals(existing.Path, fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        transaction.Commit();
                        return existing;
                    }

                    if (IsAncestor(existing.Path, fullPath) || IsAncestor(fullPath, existing.Path))
                    {
                        throw new InvalidOperationException($"Managed root '{fullPath}' overlaps existing root '{existing.Path}'.");
                    }
                }
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO roots (path, normalized_path) VALUES ($path, $normalizedPath) RETURNING id;";
            insert.Parameters.AddWithValue("$path", fullPath);
            insert.Parameters.AddWithValue("$normalizedPath", fullPath);
            var root = new ManagedRoot((long)insert.ExecuteScalar()!, fullPath);
            transaction.Commit();
            return root;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public IReadOnlyList<ManagedRoot> ListRoots()
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, path, status, last_error, last_checked_utc FROM roots ORDER BY path COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var roots = new List<ManagedRoot>();
        while (reader.Read())
        {
            roots.Add(ReadManagedRoot(reader));
        }

        return roots;
    }

    public bool RemoveRoot(long rootId)
    {
        _writeGate.Wait();
        try
        {
            using var connection = SqliteDatabase.Open(DatabasePath);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM roots WHERE id = $rootId;";
            command.Parameters.AddWithValue("$rootId", rootId);
            var removed = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return removed;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal void SetRootStatus(long rootId, ManagedRootStatus status, string? error = null)
    {
        _writeGate.Wait();
        try
        {
            using var connection = SqliteDatabase.Open(DatabasePath);
            SetRootStatus(connection, rootId, status, error);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public IReadOnlyList<ManagedRoot> GetInterruptedScanRoots()
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT r.id, r.path, r.status, r.last_error, r.last_checked_utc
            FROM roots r
            JOIN scan_sessions s ON s.root_id = r.id
            WHERE s.status = 'running'
            ORDER BY r.path COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var roots = new List<ManagedRoot>();
        while (reader.Read())
        {
            roots.Add(ReadManagedRoot(reader));
        }

        return roots;
    }

    public IReadOnlyList<ScanSessionRecord> GetInterruptedSessions(long? rootId = null)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = rootId.HasValue
            ? "SELECT id, root_id, scan_token, scan_type, status, started_utc, completed_utc FROM scan_sessions WHERE status = 'running' AND root_id = $rootId ORDER BY id ASC;"
            : "SELECT id, root_id, scan_token, scan_type, status, started_utc, completed_utc FROM scan_sessions WHERE status = 'running' ORDER BY id ASC;";
        if (rootId.HasValue)
        {
            command.Parameters.AddWithValue("$rootId", rootId.Value);
        }

        using var reader = command.ExecuteReader();
        var sessions = new List<ScanSessionRecord>();
        while (reader.Read())
        {
            sessions.Add(new ScanSessionRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return sessions;
    }

    public int ResolveInterruptedSessions(long rootId)
    {
        _writeGate.Wait();
        try
        {
            using var connection = SqliteDatabase.Open(DatabasePath);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE scan_sessions
                SET status = 'interrupted', completed_utc = $completedUtc
                WHERE root_id = $rootId AND status = 'running';
                """;
            command.Parameters.AddWithValue("$rootId", rootId);
            command.Parameters.AddWithValue("$completedUtc", DateTimeOffset.UtcNow.ToString("O"));
            var count = command.ExecuteNonQuery();
            transaction.Commit();
            return count;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<ScanResult> ScanAsync(
        long rootId,
        int batchSize = 100,
        Action<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string scanType = "full")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return RunSerialized(() => Scan(rootId, batchSize, progress, cancellationToken, scanType), cancellationToken);
    }

    public Task<ScanResult> ReconcilePathsAsync(
        long rootId,
        IReadOnlyCollection<string> paths,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return RunSerialized(
            () => ReconcilePaths(rootId, paths, batchSize, cancellationToken),
            cancellationToken);
    }

    internal ScanResult ReconcilePathsDirect(
        long rootId,
        IReadOnlyCollection<string> paths,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return ReconcilePaths(rootId, paths, batchSize, cancellationToken);
    }

    internal Task<T> ExecuteWriteAsync<T>(Func<T> action, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            _writeGate.Wait(cancellationToken);
            try
            {
                return action();
            }
            finally
            {
                _writeGate.Release();
            }
        }, cancellationToken);

    private Task<ScanResult> RunSerialized(Func<ScanResult> action, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                _writeGate.Wait(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(0, 0, 0, 0, 0, 0, true, []);
            }

            try
            {
                return action();
            }
            finally
            {
                _writeGate.Release();
            }
        });

    private ScanResult Scan(
        long rootId,
        int batchSize,
        Action<ScanProgress>? progress,
        CancellationToken cancellationToken,
        string scanType = "full")
    {
        if (scanType is not ("full" or "recovery" or "reconcile"))
        {
            throw new ArgumentException($"Invalid scan type '{scanType}'.", nameof(scanType));
        }

        using var connection = SqliteDatabase.Open(DatabasePath);
        var root = ReadRoot(connection, rootId);
        var failures = new List<ScanFailure>();
        var pending = new List<FileRecord>(batchSize);
        var directories = new Stack<string>();
        var scanToken = Guid.NewGuid().ToString("N");
        var correlationId = $"scan-{rootId}-{scanToken}";
        var startedUtc = DateTimeOffset.UtcNow.ToString("O");
        long sessionId;
        using (var insertSession = connection.CreateCommand())
        {
            insertSession.CommandText =
                """
                INSERT INTO scan_sessions (root_id, scan_token, scan_type, status, started_utc)
                VALUES ($rootId, $scanToken, $scanType, 'running', $startedUtc)
                RETURNING id;
                """;
            insertSession.Parameters.AddWithValue("$rootId", rootId);
            insertSession.Parameters.AddWithValue("$scanToken", scanToken);
            insertSession.Parameters.AddWithValue("$scanType", scanType);
            insertSession.Parameters.AddWithValue("$startedUtc", startedUtc);
            sessionId = (long)insertSession.ExecuteScalar()!;
        }

        var discovered = 0;
        var committed = 0;
        var added = 0;
        var updated = 0;
        var missing = 0;
        var fallback = 0;
        var skippedReparsePoints = 0;
        var coverageComplete = true;
        var rootAvailable = false;

        _logger.LogInfo(
            DiagnosticCategory.Scanner,
            "ScanStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: $"Root '{root.Path}' (Id={rootId})",
            properties: new Dictionary<string, object?>
            {
                ["scanType"] = scanType,
                ["scanToken"] = scanToken
            });

        if (cancellationToken.IsCancellationRequested)
        {
            return Complete(canceled: true);
        }

        try
        {
            var attributes = _getAttributes(root.Path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                failures.Add(new(root.Path, "Managed root is not a directory."));
                return Complete(canceled: false);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                skippedReparsePoints++;
                _logger.LogInfo(
                    DiagnosticCategory.Scanner,
                    "ReparsePointSkipped",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Skipped,
                    message: $"Managed root is a reparse point: {root.Path}",
                    properties: new Dictionary<string, object?>
                    {
                        ["path"] = root.Path,
                        ["type"] = "Directory"
                    });
                failures.Add(new(root.Path, "Managed root is a reparse point."));
                return Complete(canceled: false);
            }

            directories.Push(root.Path);
            rootAvailable = true;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Complete(canceled: true);
            }

            failures.Add(new(root.Path, "Managed root does not exist."));
            if (cancellationToken.IsCancellationRequested)
            {
                return Complete(canceled: true);
            }

            return Complete(canceled: false);
        }
        catch (Exception exception) when (IsFileSystemError(exception))
        {
            failures.Add(new(root.Path, exception.Message));
            return Complete(canceled: false);
        }

        while (directories.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var directory = directories.Pop();
            string[] entries;
            try
            {
                var directoryAttributes = _getAttributes(directory);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0 ||
                    (directoryAttributes & FileAttributes.Directory) == 0)
                {
                    if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedReparsePoints++;
                        _logger.LogInfo(
                            DiagnosticCategory.Scanner,
                            "ReparsePointSkipped",
                            correlationId: correlationId,
                            status: DiagnosticResultStatus.Skipped,
                            message: $"Skipped reparse point directory: {directory}",
                            properties: new Dictionary<string, object?>
                            {
                                ["path"] = directory,
                                ["type"] = "Directory"
                            });
                    }
                    continue;
                }

                entries = _getFileSystemEntries(directory);
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                coverageComplete = false;
                if (string.Equals(directory, root.Path, StringComparison.OrdinalIgnoreCase))
                {
                    rootAvailable = false;
                }

                _logger.LogWarning(
                    DiagnosticCategory.Scanner,
                    "ScanItemError",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: $"{directory}: {exception.Message}",
                    errorCode: "SCAN_DIRECTORY_ERROR",
                    exception: exception);

                failures.Add(new(directory, exception.Message));
                progress?.Invoke(new(discovered, committed, failures.Count));
                continue;
            }

            foreach (var entry in entries)
            {
                if (IsDatabasePath(entry))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Complete(canceled: true);
                }

                try
                {
                    var attributes = _getAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedReparsePoints++;
                        _logger.LogInfo(
                            DiagnosticCategory.Scanner,
                            "ReparsePointSkipped",
                            correlationId: correlationId,
                            status: DiagnosticResultStatus.Skipped,
                            message: $"Skipped reparse point: {entry}",
                            properties: new Dictionary<string, object?>
                            {
                                ["path"] = entry,
                                ["type"] = (attributes & FileAttributes.Directory) != 0 ? "Directory" : "File"
                            });
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry);
                        continue;
                    }

                    var file = ReadFileRecord(entry);
                    if (!file.Identity.IsStable)
                    {
                        fallback++;
                    }

                    pending.Add(file);
                    discovered++;
                }
                catch (Exception exception) when (IsFileSystemError(exception))
                {
                    coverageComplete = false;
                    _logger.LogWarning(
                        DiagnosticCategory.Scanner,
                        "ScanItemError",
                        correlationId: correlationId,
                        status: DiagnosticResultStatus.Failed,
                        message: $"{entry}: {exception.Message}",
                        errorCode: "SCAN_FILE_ERROR",
                        exception: exception);

                    failures.Add(new(entry, exception.Message));
                    progress?.Invoke(new(discovered, committed, failures.Count));
                    continue;
                }

                if (pending.Count == batchSize)
                {
                    var written = WriteBatch(connection, root.Id, scanToken, pending, failures);
                    added += written.Added;
                    updated += written.Updated;
                    missing += written.Missing;
                    committed += pending.Count;
                    pending.Clear();
                    OnBatchCommitted?.Invoke(committed / batchSize);
                    progress?.Invoke(new(discovered, committed, failures.Count));
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Complete(canceled: true);
        }

        if (pending.Count > 0)
        {
            var written = WriteBatch(connection, root.Id, scanToken, pending, failures);
            added += written.Added;
            updated += written.Updated;
            missing += written.Missing;
            committed += pending.Count;
            OnBatchCommitted?.Invoke(committed / batchSize);
            progress?.Invoke(new(discovered, committed, failures.Count));
        }

        if (coverageComplete)
        {
            OnBeforeMarkMissing?.Invoke();
            missing += MarkMissing(connection, root.Id, scanToken);
        }
        return Complete(canceled: false);

        ScanResult Complete(bool canceled)
        {
            using (var updateSession = connection.CreateCommand())
            {
                updateSession.CommandText =
                    """
                    UPDATE scan_sessions
                    SET status = $status, completed_utc = $completedUtc
                    WHERE id = $sessionId;
                    """;
                updateSession.Parameters.AddWithValue("$status", canceled ? "interrupted" : "completed");
                updateSession.Parameters.AddWithValue("$completedUtc", DateTimeOffset.UtcNow.ToString("O"));
                updateSession.Parameters.AddWithValue("$sessionId", sessionId);
                updateSession.ExecuteNonQuery();
            }

            if (canceled)
            {
                _logger.LogInfo(
                    DiagnosticCategory.Scanner,
                    "ScanCancelled",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Skipped,
                    message: $"Root '{root.Path}' scan cancelled.");
            }
            else
            {
                SetRootStatus(
                    connection,
                    root.Id,
                    rootAvailable ? ManagedRootStatus.Online : ManagedRootStatus.Offline,
                    failures.LastOrDefault()?.Error);

                if (!rootAvailable)
                {
                    _logger.LogWarning(
                        DiagnosticCategory.Scanner,
                        "RootOffline",
                        correlationId: correlationId,
                        status: DiagnosticResultStatus.Failed,
                        message: $"Managed root '{root.Path}' is offline: {failures.LastOrDefault()?.Error ?? "Root unavailable"}",
                        errorCode: "ROOT_OFFLINE");
                }
                else if (failures.Count > 0)
                {
                    _logger.LogWarning(
                        DiagnosticCategory.Scanner,
                        "ScanCompletedWithFailures",
                        correlationId: correlationId,
                        status: DiagnosticResultStatus.Failed,
                        message: $"Root '{root.Path}' scan completed with {failures.Count} failures (Discovered: {discovered}, Committed: {committed}, Missing: {missing}).",
                        errorCode: "SCAN_PARTIAL_FAILURE");
                }
                else
                {
                    _logger.LogInfo(
                        DiagnosticCategory.Scanner,
                        "RootOnline",
                        correlationId: correlationId,
                        status: DiagnosticResultStatus.Success,
                        message: $"Managed root '{root.Path}' is online.");

                    _logger.LogInfo(
                        DiagnosticCategory.Scanner,
                        "ScanCompleted",
                        correlationId: correlationId,
                        status: DiagnosticResultStatus.Success,
                        message: $"Root '{root.Path}' scan completed successfully (Discovered: {discovered}, Committed: {committed}, Missing: {missing}).");
                }
            }

            return new(discovered, committed, added, updated, missing, fallback, canceled, failures, skippedReparsePoints);
        }
    }

    private ScanResult ReconcilePaths(
        long rootId,
        IReadOnlyCollection<string> paths,
        int batchSize,
        CancellationToken cancellationToken)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        var root = ReadRoot(connection, rootId);
        var failures = new List<ScanFailure>();
        var discovered = 0;
        var committed = 0;
        var added = 0;
        var updated = 0;
        var missing = 0;
        var fallback = 0;
        var skippedReparsePoints = 0;

        foreach (var path in CollapsePaths(root, paths))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Complete(canceled: true);
            }

            FileAttributes attributes;
            try
            {
                attributes = _getAttributes(path);
            }
            catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
            {
                missing += MarkPathMissing(connection, root.Id, path);
                continue;
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                failures.Add(new(path, exception.Message));
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                missing += MarkPathMissing(connection, root.Id, path);
                skippedReparsePoints++;
                _logger.LogInfo(
                    DiagnosticCategory.Scanner,
                    "ReparsePointSkipped",
                    status: DiagnosticResultStatus.Skipped,
                    message: $"Skipped reparse point: {path}",
                    properties: new Dictionary<string, object?>
                    {
                        ["path"] = path,
                        ["type"] = (attributes & FileAttributes.Directory) != 0 ? "Directory" : "File"
                    });
                continue;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                try
                {
                    var file = ReadFileRecord(path);
                    discovered++;
                    fallback += file.Identity.IsStable ? 0 : 1;
                    var written = WriteBatch(connection, root.Id, Guid.NewGuid().ToString("N"), [file], failures);
                    added += written.Added;
                    updated += written.Updated;
                    missing += written.Missing;
                    committed++;
                }
                catch (Exception exception) when (IsFileSystemError(exception))
                {
                    failures.Add(new(path, exception.Message));
                }

                continue;
            }

            var result = ReconcileDirectory(connection, root.Id, path, batchSize, failures, cancellationToken);
            discovered += result.DiscoveredFiles;
            committed += result.CommittedFiles;
            added += result.AddedFiles;
            updated += result.UpdatedFiles;
            missing += result.MissingFiles;
            fallback += result.FallbackFiles;
            skippedReparsePoints += result.SkippedReparsePoints;
            if (result.Canceled)
            {
                return Complete(canceled: true);
            }
        }

        return Complete(canceled: false);

        ScanResult Complete(bool canceled) =>
            new(discovered, committed, added, updated, missing, fallback, canceled, failures, skippedReparsePoints);
    }

    private ScanResult ReconcileDirectory(
        SqliteConnection connection,
        long rootId,
        string directoryPath,
        int batchSize,
        ICollection<ScanFailure> failures,
        CancellationToken cancellationToken)
    {
        var pending = new List<FileRecord>(batchSize);
        var directories = new Stack<string>();
        var scanToken = Guid.NewGuid().ToString("N");
        var discovered = 0;
        var committed = 0;
        var added = 0;
        var updated = 0;
        var missing = 0;
        var fallback = 0;
        var skippedReparsePoints = 0;
        var coverageComplete = true;
        directories.Push(directoryPath);

        while (directories.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var directory = directories.Pop();
            string[] entries;
            try
            {
                var directoryAttributes = _getAttributes(directory);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0 ||
                    (directoryAttributes & FileAttributes.Directory) == 0)
                {
                    if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedReparsePoints++;
                        _logger.LogInfo(
                            DiagnosticCategory.Scanner,
                            "ReparsePointSkipped",
                            status: DiagnosticResultStatus.Skipped,
                            message: $"Skipped reparse point directory: {directory}",
                            properties: new Dictionary<string, object?>
                            {
                                ["path"] = directory,
                                ["type"] = "Directory"
                            });
                    }
                    continue;
                }

                entries = _getFileSystemEntries(directory);
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                coverageComplete = false;
                failures.Add(new ScanFailure(directory, exception.Message));
                continue;
            }

            foreach (var entry in entries)
            {
                if (IsDatabasePath(entry))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Complete(canceled: true);
                }

                try
                {
                    var attributes = _getAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedReparsePoints++;
                        _logger.LogInfo(
                            DiagnosticCategory.Scanner,
                            "ReparsePointSkipped",
                            status: DiagnosticResultStatus.Skipped,
                            message: $"Skipped reparse point: {entry}",
                            properties: new Dictionary<string, object?>
                            {
                                ["path"] = entry,
                                ["type"] = (attributes & FileAttributes.Directory) != 0 ? "Directory" : "File"
                            });
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry);
                        continue;
                    }

                    var file = ReadFileRecord(entry);
                    pending.Add(file);
                    discovered++;
                    fallback += file.Identity.IsStable ? 0 : 1;
                }
                catch (Exception exception) when (IsFileSystemError(exception))
                {
                    coverageComplete = false;
                    failures.Add(new ScanFailure(entry, exception.Message));
                    continue;
                }

                if (pending.Count == batchSize)
                {
                    Flush();
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Complete(canceled: true);
        }

        if (pending.Count > 0)
        {
            Flush();
        }

        if (coverageComplete)
        {
            missing += MarkScopeMissing(connection, rootId, directoryPath, scanToken);
        }

        return Complete(canceled: false);

        void Flush()
        {
            var written = WriteBatch(connection, rootId, scanToken, pending, failures);
            added += written.Added;
            updated += written.Updated;
            missing += written.Missing;
            committed += pending.Count;
            pending.Clear();
        }

        ScanResult Complete(bool canceled) =>
            new(discovered, committed, added, updated, missing, fallback, canceled, failures.ToArray(), skippedReparsePoints);
    }

    private static ManagedRoot ReadRoot(SqliteConnection connection, long rootId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, path, status, last_error, last_checked_utc FROM roots WHERE id = $rootId;";
        command.Parameters.AddWithValue("$rootId", rootId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentException("Managed root was not found.", nameof(rootId));
        }

        return ReadManagedRoot(reader);
    }

    private static ManagedRoot ReadManagedRoot(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            ParseStatus(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)));

    private static ManagedRootStatus ParseStatus(string status) => status switch
    {
        "online" => ManagedRootStatus.Online,
        "offline" => ManagedRootStatus.Offline,
        "recovering" => ManagedRootStatus.Recovering,
        _ => throw new InvalidDataException($"Unknown managed root status '{status}'.")
    };

    private static void SetRootStatus(
        SqliteConnection connection,
        long rootId,
        ManagedRootStatus status,
        string? error)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE roots SET status = $status, last_error = $error, last_checked_utc = $checked WHERE id = $rootId;";
        command.Parameters.AddWithValue("$status", status.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$checked", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$rootId", rootId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new ArgumentException("Managed root was not found.", nameof(rootId));
        }
    }

    private IReadOnlyList<string> CollapsePaths(ManagedRoot root, IReadOnlyCollection<string> paths)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var fullPath = Normalize(path);
            if (!string.Equals(root.Path, fullPath, StringComparison.OrdinalIgnoreCase) &&
                !IsAncestor(root.Path, fullPath))
            {
                throw new ArgumentException($"Path '{fullPath}' is outside managed root '{root.Path}'.", nameof(paths));
            }

            if (IsDatabasePath(fullPath))
            {
                continue;
            }

            normalized[fullPath] = fullPath;
        }

        var collapsed = new List<string>();
        foreach (var path in normalized.Values.OrderBy(path => path.Length))
        {
            if (!collapsed.Any(parent => IsAncestor(parent, path)))
            {
                collapsed.Add(path);
            }
        }

        return collapsed;
    }

    internal bool IsDatabasePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return string.Equals(fullPath, DatabasePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, DatabasePath + "-wal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, DatabasePath + "-shm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, DatabasePath + "-journal", StringComparison.OrdinalIgnoreCase);
    }

    private FileRecord ReadFileRecord(string path)
    {
        var file = new FileInfo(path);
        var fullPath = Normalize(file.FullName);
        return new(
            _readIdentity(fullPath),
            fullPath,
            file.Name,
            file.Extension,
            file.Length,
            file.LastWriteTimeUtc.ToString("O"));
    }

    private (int Added, int Updated, int Missing) WriteBatch(
        SqliteConnection connection,
        long rootId,
        string scanToken,
        IReadOnlyList<FileRecord> files,
        ICollection<ScanFailure> failures)
    {
        var prepared = new List<PreparedFile>(files.Count);
        foreach (var file in files)
        {
            var existing = ReadExistingFile(connection, file.Identity);
            FileTypeClassification? classification = null;
            if (existing is null ||
                !string.Equals(existing.Extension, file.Extension, StringComparison.OrdinalIgnoreCase) ||
                existing.Size != file.Size ||
                !string.Equals(existing.ModifiedUtc, file.ModifiedUtc, StringComparison.Ordinal))
            {
                try
                {
                    classification = _classify(file.Path);
                    if (!classification.HasConflict && !string.IsNullOrWhiteSpace(classification.Diagnostic))
                    {
                        failures.Add(new(file.Path, classification.Diagnostic));
                    }
                }
                catch (Exception exception) when (IsClassificationError(exception))
                {
                    failures.Add(new(file.Path, $"类型识别失败：{exception.Message}"));
                }
            }

            prepared.Add(new(file, existing is not null, classification));
        }

        using var transaction = connection.BeginTransaction();
        var added = 0;
        var updated = 0;
        var missing = 0;
        foreach (var preparedFile in prepared)
        {
            var file = preparedFile.File;

            if (preparedFile.IdentityExists)
            {
                updated++;
            }
            else
            {
                added++;
            }

            using (var releaseDescendants = connection.CreateCommand())
            {
                releaseDescendants.Transaction = transaction;
                releaseDescendants.CommandText =
                    "UPDATE files SET is_online = 0 WHERE root_id = $rootId AND is_online = 1 AND substr(normalized_path, 1, length($prefix)) = $prefix COLLATE NOCASE;";
                releaseDescendants.Parameters.AddWithValue("$rootId", rootId);
                releaseDescendants.Parameters.AddWithValue("$prefix", file.Path + Path.DirectorySeparatorChar);
                missing += releaseDescendants.ExecuteNonQuery();
            }

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
                releasePath.Parameters.AddWithValue("$normalizedPath", file.Path);
                releasePath.Parameters.AddWithValue("$volumeId", file.Identity.VolumeId);
                releasePath.Parameters.AddWithValue("$fileId", file.Identity.FileId);
                missing += releasePath.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
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
            command.Parameters.AddWithValue("$rootId", rootId);
            command.Parameters.AddWithValue("$volumeId", file.Identity.VolumeId);
            command.Parameters.AddWithValue("$fileId", file.Identity.FileId);
            command.Parameters.AddWithValue("$path", file.Path);
            command.Parameters.AddWithValue("$normalizedPath", file.Path);
            command.Parameters.AddWithValue("$name", file.Name);
            command.Parameters.AddWithValue("$extension", file.Extension);
            command.Parameters.AddWithValue("$size", file.Size);
            command.Parameters.AddWithValue("$modifiedUtc", file.ModifiedUtc);
            command.Parameters.AddWithValue("$identityKind", file.Identity.IsStable ? "stable" : "path");
            command.Parameters.AddWithValue("$identityDiagnostic", (object?)file.Identity.Diagnostic ?? DBNull.Value);
            command.Parameters.AddWithValue("$scanToken", scanToken);
            var persistedFileId = (long)command.ExecuteScalar()!;
            if (preparedFile.Classification is not null)
            {
                TagService.ReplaceAutomaticTags(
                    connection,
                    transaction,
                    persistedFileId,
                    preparedFile.Classification.AutomaticTags);
            }
        }

        transaction.Commit();
        return (added, updated, missing);
    }

    private static ExistingFile? ReadExistingFile(SqliteConnection connection, FileIdentity identity)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT extension, size, modified_utc
            FROM files
            WHERE volume_id = $volumeId AND file_id = $fileId;
            """;
        command.Parameters.AddWithValue("$volumeId", identity.VolumeId);
        command.Parameters.AddWithValue("$fileId", identity.FileId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2))
            : null;
    }

    private static int MarkMissing(SqliteConnection connection, long rootId, string scanToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE files SET is_online = 0 WHERE root_id = $rootId AND is_online = 1 AND scan_token <> $scanToken;";
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$scanToken", scanToken);
        return command.ExecuteNonQuery();
    }

    private static int MarkPathMissing(SqliteConnection connection, long rootId, string path)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE files SET is_online = 0 WHERE root_id = $rootId AND is_online = 1 AND (normalized_path = $path COLLATE NOCASE OR substr(normalized_path, 1, length($prefix)) = $prefix COLLATE NOCASE);";
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$prefix", Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar);
        var missing = command.ExecuteNonQuery();
        transaction.Commit();
        return missing;
    }

    private static int MarkScopeMissing(
        SqliteConnection connection,
        long rootId,
        string directoryPath,
        string scanToken)
    {
        var prefix = Path.EndsInDirectorySeparator(directoryPath)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE files SET is_online = 0 WHERE root_id = $rootId AND is_online = 1 AND scan_token <> $scanToken AND (normalized_path = $path COLLATE NOCASE OR substr(normalized_path, 1, length($prefix)) = $prefix COLLATE NOCASE);";
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$scanToken", scanToken);
        command.Parameters.AddWithValue("$path", directoryPath);
        command.Parameters.AddWithValue("$prefix", prefix);
        var missing = command.ExecuteNonQuery();
        transaction.Commit();
        return missing;
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsAncestor(string parent, string child) =>
        child.Length > parent.Length
        && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
        && (Path.EndsInDirectorySeparator(parent) || IsDirectorySeparator(child[parent.Length]));

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    private static bool IsFileSystemError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static bool IsClassificationError(Exception exception) => exception is
        IOException or UnauthorizedAccessException or System.Security.SecurityException or
        InvalidDataException or NotSupportedException or ArgumentException;

    private sealed record FileRecord(FileIdentity Identity, string Path, string Name, string Extension, long Size, string ModifiedUtc);
    private sealed record ExistingFile(string Extension, long Size, string ModifiedUtc);
    private sealed record PreparedFile(FileRecord File, bool IdentityExists, FileTypeClassification? Classification);
}
