using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public sealed record ManagedRoot(long Id, string Path);

public sealed record ScanFailure(string Path, string Error);

public sealed record ScanProgress(int DiscoveredFiles, int CommittedFiles, int FailedItems);

public sealed record ScanResult(int DiscoveredFiles, int CommittedFiles, bool Canceled, IReadOnlyList<ScanFailure> Failures);

public sealed class ManagedRootScanner
{
    public ManagedRootScanner(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
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
        var discovered = 0;
        var committed = 0;

        if (!Directory.Exists(root.Path))
        {
            failures.Add(new(root.Path, "Managed root does not exist."));
            return Complete(canceled: false);
        }

        try
        {
            if ((File.GetAttributes(root.Path) & FileAttributes.ReparsePoint) == 0)
            {
                directories.Push(root.Path);
            }
        }
        catch (Exception exception) when (IsFileSystemError(exception))
        {
            failures.Add(new(root.Path, exception.Message));
        }

        while (directories.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var directory = directories.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
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
                    var attributes = File.GetAttributes(entry);
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
                    // ponytail: path fallback only; replace both identity fields with Windows stable IDs in Issue #9.
                    pending.Add(new(
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
                    WriteBatch(connection, root.Id, pending);
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
            WriteBatch(connection, root.Id, pending);
            committed += pending.Count;
            progress?.Invoke(new(discovered, committed, failures.Count));
        }

        return Complete(canceled: false);

        ScanResult Complete(bool canceled) => new(discovered, committed, canceled, failures);
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

    private static void WriteBatch(SqliteConnection connection, long rootId, IReadOnlyList<FileRecord> files)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var file in files)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc)
                VALUES ($rootId, $volumeId, $fileId, $path, $normalizedPath, $name, $extension, $size, $modifiedUtc)
                ON CONFLICT(normalized_path) DO UPDATE SET
                    root_id = excluded.root_id,
                    path = excluded.path,
                    name = excluded.name,
                    extension = excluded.extension,
                    size = excluded.size,
                    modified_utc = excluded.modified_utc;
                """;
            command.Parameters.AddWithValue("$rootId", rootId);
            command.Parameters.AddWithValue("$volumeId", "path-fallback");
            command.Parameters.AddWithValue("$fileId", file.Path);
            command.Parameters.AddWithValue("$path", file.Path);
            command.Parameters.AddWithValue("$normalizedPath", file.Path);
            command.Parameters.AddWithValue("$name", file.Name);
            command.Parameters.AddWithValue("$extension", file.Extension);
            command.Parameters.AddWithValue("$size", file.Size);
            command.Parameters.AddWithValue("$modifiedUtc", file.ModifiedUtc);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
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

    private sealed record FileRecord(string Path, string Name, string Extension, long Size, string ModifiedUtc);
}
