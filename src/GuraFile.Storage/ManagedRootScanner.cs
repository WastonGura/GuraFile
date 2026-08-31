using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public sealed record ManagedRoot(long Id, string Path);

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
    IReadOnlyList<ScanFailure> Failures);

public sealed class ManagedRootScanner
{
    private readonly Func<string, FileIdentity> _readIdentity;
    private readonly Func<string, string[]> _getFileSystemEntries;
    private readonly Func<string, FileAttributes> _getAttributes;

    public ManagedRootScanner(string databasePath) :
        this(databasePath, FileIdentityReader.Read, Directory.GetFileSystemEntries, File.GetAttributes)
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(getFileSystemEntries);
        ArgumentNullException.ThrowIfNull(getAttributes);
        DatabasePath = Path.GetFullPath(databasePath);
        _readIdentity = readIdentity;
        _getFileSystemEntries = getFileSystemEntries;
        _getAttributes = getAttributes;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var _ = SqliteDatabase.Open(DatabasePath);
    }

    public string DatabasePath { get; }

    public ManagedRoot AddRoot(string path)
    {
        var fullPath = Normalize(path);
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

    public IReadOnlyList<ManagedRoot> ListRoots()
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path FROM roots ORDER BY path COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var roots = new List<ManagedRoot>();
        while (reader.Read())
        {
            roots.Add(new(reader.GetInt64(0), reader.GetString(1)));
        }

        return roots;
    }

    public bool RemoveRoot(long rootId)
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

    public Task<ScanResult> ScanAsync(
        long rootId,
        int batchSize = 100,
        Action<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return Task.Run(() => Scan(rootId, batchSize, progress, cancellationToken));
    }

    private ScanResult Scan(long rootId, int batchSize, Action<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        var root = ReadRoot(connection, rootId);
        var failures = new List<ScanFailure>();
        var pending = new List<FileRecord>(batchSize);
        var directories = new Stack<string>();
        var scanToken = Guid.NewGuid().ToString("N");
        var discovered = 0;
        var committed = 0;
        var added = 0;
        var updated = 0;
        var missing = 0;
        var fallback = 0;

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
                failures.Add(new(root.Path, "Managed root is a reparse point."));
                return Complete(canceled: false);
            }

            directories.Push(root.Path);
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

            missing = MarkMissing(connection, root.Id, scanToken);
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
                entries = _getFileSystemEntries(directory);
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                failures.Add(new(directory, exception.Message));
                progress?.Invoke(new(discovered, committed, failures.Count));
                continue;
            }

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Complete(canceled: true);
                }

                try
                {
                    var attributes = _getAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry);
                        continue;
                    }

                    var file = new FileInfo(entry);
                    var fullPath = Normalize(file.FullName);
                    var identity = _readIdentity(fullPath);
                    if (!identity.IsStable)
                    {
                        fallback++;
                    }

                    pending.Add(new(
                        identity,
                        fullPath,
                        file.Name,
                        file.Extension,
                        file.Length,
                        file.LastWriteTimeUtc.ToString("O")));
                    discovered++;
                }
                catch (Exception exception) when (IsFileSystemError(exception))
                {
                    failures.Add(new(entry, exception.Message));
                    progress?.Invoke(new(discovered, committed, failures.Count));
                    continue;
                }

                if (pending.Count == batchSize)
                {
                    var written = WriteBatch(connection, root.Id, scanToken, pending);
                    added += written.Added;
                    updated += written.Updated;
                    missing += written.Missing;
                    committed += pending.Count;
                    pending.Clear();
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
            var written = WriteBatch(connection, root.Id, scanToken, pending);
            added += written.Added;
            updated += written.Updated;
            missing += written.Missing;
            committed += pending.Count;
            progress?.Invoke(new(discovered, committed, failures.Count));
        }

        if (failures.Count == 0)
        {
            missing += MarkMissing(connection, root.Id, scanToken);
        }
        return Complete(canceled: false);

        ScanResult Complete(bool canceled) =>
            new(discovered, committed, added, updated, missing, fallback, canceled, failures);
    }

    private static ManagedRoot ReadRoot(SqliteConnection connection, long rootId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path FROM roots WHERE id = $rootId;";
        command.Parameters.AddWithValue("$rootId", rootId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentException("Managed root was not found.", nameof(rootId));
        }

        return new(reader.GetInt64(0), reader.GetString(1));
    }

    private static (int Added, int Updated, int Missing) WriteBatch(
        SqliteConnection connection,
        long rootId,
        string scanToken,
        IReadOnlyList<FileRecord> files)
    {
        using var transaction = connection.BeginTransaction();
        var added = 0;
        var updated = 0;
        var missing = 0;
        foreach (var file in files)
        {
            var identityExists = false;
            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText =
                    "SELECT EXISTS(SELECT 1 FROM files WHERE volume_id = $volumeId AND file_id = $fileId);";
                exists.Parameters.AddWithValue("$volumeId", file.Identity.VolumeId);
                exists.Parameters.AddWithValue("$fileId", file.Identity.FileId);
                identityExists = (long)exists.ExecuteScalar()! == 1;
            }

            if (identityExists)
            {
                updated++;
            }
            else
            {
                added++;
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
                    scan_token = excluded.scan_token;
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
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return (added, updated, missing);
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

    private sealed record FileRecord(FileIdentity Identity, string Path, string Name, string Extension, long Size, string ModifiedUtc);
}
