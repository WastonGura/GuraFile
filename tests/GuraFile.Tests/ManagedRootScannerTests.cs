using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class ManagedRootScannerTests
{
    [TestMethod]
    public void RootsPersistAcrossReopen()
    {
        using var temp = TempDirectory.Create();
        var databasePath = Path.Combine(temp.Path, "index.db");
        var rootPath = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(rootPath);

        var root = new ManagedRootScanner(databasePath).AddRoot(rootPath);
        var reopened = new ManagedRootScanner(databasePath).ListRoots();

        Assert.HasCount(1, reopened);
        Assert.AreEqual(root.Id, reopened[0].Id);
        Assert.AreEqual(Path.GetFullPath(rootPath), reopened[0].Path);
    }

    [TestMethod]
    public void AddRootIsIdempotentAndRejectsParentChildOverlapOnly()
    {
        using var temp = TempDirectory.Create();
        var parentPath = temp.CreateDirectory("root");
        var childPath = Path.Combine(parentPath, "child");
        Directory.CreateDirectory(childPath);
        var adjacentPath = temp.CreateDirectory("rooted");
        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));

        var parent = scanner.AddRoot(parentPath);
        var duplicate = scanner.AddRoot(parentPath + Path.DirectorySeparatorChar);

        Assert.AreEqual(parent, duplicate);
        var childError = Assert.ThrowsExactly<InvalidOperationException>(() => scanner.AddRoot(childPath));
        StringAssert.Contains(childError.Message, "overlaps");
        scanner.AddRoot(adjacentPath);
        Assert.HasCount(2, scanner.ListRoots());

        var reverseScanner = new ManagedRootScanner(Path.Combine(temp.Path, "reverse.db"));
        reverseScanner.AddRoot(childPath);
        var parentError = Assert.ThrowsExactly<InvalidOperationException>(() => reverseScanner.AddRoot(parentPath));
        StringAssert.Contains(parentError.Message, "overlaps");
    }

    [TestMethod]
    public async Task ScanStoresFileMetadata()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        var filePath = Path.Combine(rootPath, "note.TXT");
        await File.WriteAllTextAsync(filePath, "hello");
        var modified = new DateTime(2026, 8, 31, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(filePath, modified);
        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(rootPath);

        var result = await scanner.ScanAsync(root.Id);

        Assert.IsFalse(result.Canceled);
        Assert.AreEqual(1, result.CommittedFiles);
        using var connection = SqliteDatabase.Open(scanner.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, name, extension, size, modified_utc FROM files WHERE root_id = $rootId;";
        command.Parameters.AddWithValue("$rootId", root.Id);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(Path.GetFullPath(filePath), reader.GetString(0));
        Assert.AreEqual("note.TXT", reader.GetString(1));
        Assert.AreEqual(".TXT", reader.GetString(2));
        Assert.AreEqual(5L, reader.GetInt64(3));
        Assert.AreEqual(modified, DateTime.Parse(reader.GetString(4)).ToUniversalTime());
    }

    [TestMethod]
    public async Task RenameAndMovePreserveNodeAndUserTag()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        var originalPath = Path.Combine(rootPath, "original.txt");
        await File.WriteAllTextAsync(originalPath, "tagged");
        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);
        var fileId = Scalar<long>(scanner.DatabasePath, "SELECT id FROM files;");
        AddUserTag(scanner.DatabasePath, fileId);
        var movedDirectory = Path.Combine(rootPath, "moved");
        Directory.CreateDirectory(movedDirectory);
        var movedPath = Path.Combine(movedDirectory, "renamed.txt");
        File.Move(originalPath, movedPath);

        await scanner.ScanAsync(root.Id);

        Assert.AreEqual(fileId, Scalar<long>(scanner.DatabasePath, "SELECT id FROM files;"));
        Assert.AreEqual(Path.GetFullPath(movedPath), Scalar<string>(scanner.DatabasePath, "SELECT path FROM files;"));
        Assert.AreEqual(1L, Scalar<long>(scanner.DatabasePath, "SELECT COUNT(*) FROM file_tags WHERE file_id = $fileId AND source = 'user';", ("$fileId", fileId)));
    }

    [TestMethod]
    public async Task MissingFileGoesOfflineAndReappearsAsSameNode()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        var outsidePath = temp.CreateDirectory("outside");
        var filePath = Path.Combine(rootPath, "temporary.txt");
        var outsideFilePath = Path.Combine(outsidePath, "temporary.txt");
        await File.WriteAllTextAsync(filePath, "temporary");
        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(rootPath);
        var initial = await scanner.ScanAsync(root.Id);
        var fileId = Scalar<long>(scanner.DatabasePath, "SELECT id FROM files;");

        File.Move(filePath, outsideFilePath);
        var missing = await scanner.ScanAsync(root.Id);
        Assert.AreEqual(0L, Scalar<long>(scanner.DatabasePath, "SELECT is_online FROM files WHERE id = $id;", ("$id", fileId)));

        File.Move(outsideFilePath, filePath);
        var restored = await scanner.ScanAsync(root.Id);
        Assert.AreEqual(1, initial.AddedFiles);
        Assert.AreEqual(1, missing.MissingFiles);
        Assert.AreEqual(1, restored.UpdatedFiles);
        Assert.AreEqual(fileId, Scalar<long>(scanner.DatabasePath, "SELECT id FROM files WHERE is_online = 1;"));
        Assert.AreEqual(1L, Scalar<long>(scanner.DatabasePath, "SELECT COUNT(*) FROM files;"));
    }

    [TestMethod]
    public async Task SamePathReplacementDoesNotInheritUserTag()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        var outsidePath = temp.CreateDirectory("outside");
        var filePath = Path.Combine(rootPath, "replace.txt");
        await File.WriteAllTextAsync(filePath, "old");
        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);
        var oldId = Scalar<long>(scanner.DatabasePath, "SELECT id FROM files;");
        AddUserTag(scanner.DatabasePath, oldId);
        File.Move(filePath, Path.Combine(outsidePath, "old.txt"));
        await File.WriteAllTextAsync(filePath, "new");

        await scanner.ScanAsync(root.Id);

        Assert.AreEqual(2L, Scalar<long>(scanner.DatabasePath, "SELECT COUNT(*) FROM files;"));
        Assert.AreEqual(0L, Scalar<long>(scanner.DatabasePath, "SELECT is_online FROM files WHERE id = $id;", ("$id", oldId)));
        Assert.AreEqual(1L, Scalar<long>(scanner.DatabasePath, "SELECT COUNT(*) FROM file_tags WHERE file_id = $id AND source = 'user';", ("$id", oldId)));
        Assert.AreEqual(0L, Scalar<long>(scanner.DatabasePath, "SELECT COUNT(*) FROM file_tags ft JOIN files f ON f.id = ft.file_id WHERE f.is_online = 1 AND ft.source = 'user';"));
    }

    [TestMethod]
    public async Task FallbackIsDiagnosedAndMatchesOnlyByPath()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        var filePath = Path.Combine(rootPath, "fallback.txt");
        await File.WriteAllTextAsync(filePath, "fallback");
        var scanner = new ManagedRootScanner(
            Path.Combine(temp.Path, "index.db"),
            path => FileIdentity.PathFallback(path, "simulated identity failure"));
        var root = scanner.AddRoot(rootPath);

        var first = await scanner.ScanAsync(root.Id);
        var firstId = Scalar<long>(scanner.DatabasePath, "SELECT id FROM files;");
        await File.AppendAllTextAsync(filePath, "-updated");
        await scanner.ScanAsync(root.Id);
        var renamedPath = Path.Combine(rootPath, "renamed.txt");
        File.Move(filePath, renamedPath);
        await scanner.ScanAsync(root.Id);

        Assert.AreEqual(1, first.FallbackFiles);
        Assert.AreEqual("simulated identity failure", Scalar<string>(scanner.DatabasePath, "SELECT identity_diagnostic FROM files WHERE id = $id;", ("$id", firstId)));
        Assert.AreEqual(2L, Scalar<long>(scanner.DatabasePath, "SELECT COUNT(*) FROM files;"));
        Assert.AreEqual(0L, Scalar<long>(scanner.DatabasePath, "SELECT is_online FROM files WHERE id = $id;", ("$id", firstId)));
    }

    [TestMethod]
    public async Task CancellationKeepsCommittedBatchAndAllowsRescan()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        for (var index = 0; index < 3; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, $"{index}.txt"), index.ToString());
        }

        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(rootPath);
        using var cancellation = new CancellationTokenSource();

        var canceled = await scanner.ScanAsync(
            root.Id,
            batchSize: 1,
            progress =>
            {
                if (progress.CommittedFiles == 1)
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token);

        Assert.IsTrue(canceled.Canceled);
        Assert.AreEqual(1, CountFiles(scanner.DatabasePath, root.Id));

        var completed = await scanner.ScanAsync(root.Id);
        Assert.IsFalse(completed.Canceled);
        Assert.AreEqual(3, CountFiles(scanner.DatabasePath, root.Id));
    }

    [TestMethod]
    public async Task MissingRootIsReportedWithoutThrowing()
    {
        using var temp = TempDirectory.Create();
        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(Path.Combine(temp.Path, "missing"));

        var result = await scanner.ScanAsync(root.Id);

        Assert.HasCount(1, result.Failures);
        Assert.AreEqual(root.Path, result.Failures[0].Path);
        Assert.AreEqual(0, result.CommittedFiles);
        var offline = scanner.ListRoots().Single();
        Assert.AreEqual(ManagedRootStatus.Offline, offline.Status);
        Assert.IsNotNull(offline.LastCheckedUtc);
        StringAssert.Contains(offline.LastError, "does not exist");
    }

    [TestMethod]
    public async Task RootAccessFailureDoesNotMarkIndexedFilesMissing()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "keep.txt"), "keep");
        var databasePath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);
        var fileId = Scalar<long>(databasePath, "SELECT id FROM files;");
        var failingScanner = new ManagedRootScanner(
            databasePath,
            FileIdentityReader.Read,
            Directory.GetFileSystemEntries,
            path => string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("simulated root denial")
                : File.GetAttributes(path));

        var result = await failingScanner.ScanAsync(root.Id);

        Assert.HasCount(1, result.Failures);
        Assert.AreEqual(0, result.MissingFiles);
        Assert.AreEqual(1L, Scalar<long>(databasePath, "SELECT is_online FROM files WHERE id = $id;", ("$id", fileId)));
        Assert.AreEqual(ManagedRootStatus.Offline, failingScanner.ListRoots().Single().Status);
    }

    [TestMethod]
    public async Task RootEnumerationFailureMarksRootOfflineWithoutMarkingFilesMissing()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root-enumeration");
        var filePath = Path.Combine(rootPath, "keep.txt");
        await File.WriteAllTextAsync(filePath, "keep");
        var databasePath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);
        var failingScanner = new ManagedRootScanner(
            databasePath,
            FileIdentityReader.Read,
            path => string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("simulated enumeration denial")
                : Directory.GetFileSystemEntries(path));

        var result = await failingScanner.ScanAsync(root.Id);

        Assert.HasCount(1, result.Failures);
        Assert.AreEqual(0, result.MissingFiles);
        Assert.AreEqual(ManagedRootStatus.Offline, failingScanner.ListRoots().Single().Status);
        Assert.IsTrue((await new FileQueryService(databasePath).QueryAsync(new()))
            .Single(file => file.Path == filePath).IsOnline);
    }

    [TestMethod]
    [DataRow(FileAttributes.Directory | FileAttributes.ReparsePoint)]
    [DataRow(FileAttributes.Normal)]
    public async Task NonTraversableRootDoesNotMarkIndexedFilesMissing(FileAttributes rootAttributes)
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "keep.txt"), "keep");
        var databasePath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);
        var guardedScanner = new ManagedRootScanner(
            databasePath,
            FileIdentityReader.Read,
            Directory.GetFileSystemEntries,
            path => string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase)
                ? rootAttributes
                : File.GetAttributes(path));

        var result = await guardedScanner.ScanAsync(root.Id);

        Assert.HasCount(1, result.Failures);
        Assert.AreEqual(0, result.MissingFiles);
        Assert.AreEqual(1L, Scalar<long>(databasePath, "SELECT is_online FROM files;"));
    }

    [TestMethod]
    public async Task PreCanceledMissingRootDoesNotModifyIndexedFiles()
    {
        using var temp = TempDirectory.Create();
        var databasePath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(Path.Combine(temp.Path, "missing"));
        using (var connection = SqliteDatabase.Open(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO files (
                    root_id, volume_id, file_id, path, normalized_path,
                    name, extension, size, modified_utc, identity_kind)
                VALUES ($rootId, 'volume', 'file', $path, $path, 'ghost.txt', '.txt', 0, '2026-08-31T00:00:00Z', 'stable');
                """;
            command.Parameters.AddWithValue("$rootId", root.Id);
            command.Parameters.AddWithValue("$path", Path.Combine(root.Path, "ghost.txt"));
            command.ExecuteNonQuery();
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await scanner.ScanAsync(root.Id, cancellationToken: cancellation.Token);

        Assert.IsTrue(result.Canceled);
        Assert.IsEmpty(result.Failures);
        Assert.AreEqual(0, result.MissingFiles);
        Assert.AreEqual(1L, Scalar<long>(databasePath, "SELECT is_online FROM files;"));
    }

    [TestMethod]
    public async Task CancellationDuringMissingRootProbeDoesNotModifyIndexedFiles()
    {
        using var temp = TempDirectory.Create();
        var databasePath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(Path.Combine(temp.Path, "missing"));
        InsertIndexedGhost(databasePath, root);
        using var cancellation = new CancellationTokenSource();
        var racingScanner = new ManagedRootScanner(
            databasePath,
            FileIdentityReader.Read,
            Directory.GetFileSystemEntries,
            _ =>
            {
                cancellation.Cancel();
                throw new DirectoryNotFoundException("simulated missing root");
            });

        var result = await racingScanner.ScanAsync(root.Id, cancellationToken: cancellation.Token);

        Assert.IsTrue(result.Canceled);
        Assert.IsEmpty(result.Failures);
        Assert.AreEqual(0, result.MissingFiles);
        Assert.AreEqual(1L, Scalar<long>(databasePath, "SELECT is_online FROM files;"));
    }

    [TestMethod]
    public async Task VersionTwoPathNodeKeepsIdAndTagWhenFallbackPathMatches()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("根目录");
        var filePath = Path.Combine(rootPath, "资料.txt");
        await File.WriteAllTextAsync(filePath, "tagged");
        var databasePath = Path.Combine(temp.Path, "index.db");
        SeedVersionTwoTaggedFile(databasePath, rootPath, filePath);

        var scanner = new ManagedRootScanner(
            databasePath,
            path => FileIdentity.PathFallback(path, "simulated fallback"));
        await scanner.ScanAsync(1);

        Assert.AreEqual(1L, Scalar<long>(databasePath, "SELECT COUNT(*) FROM files;"));
        Assert.AreEqual(17L, Scalar<long>(databasePath, "SELECT id FROM files WHERE is_online = 1;"));
        Assert.AreEqual("path", Scalar<string>(databasePath, "SELECT identity_kind FROM files WHERE id = 17;"));
        Assert.AreEqual(1L, Scalar<long>(databasePath, "SELECT COUNT(*) FROM file_tags WHERE file_id = 17 AND tag_id = 3 AND source = 'user';"));
    }

    [TestMethod]
    public async Task VersionTwoPathNodeDoesNotGiveTagToNewStableIdentity()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("根目录");
        var filePath = Path.Combine(rootPath, "资料.txt");
        await File.WriteAllTextAsync(filePath, "tagged");
        var databasePath = Path.Combine(temp.Path, "index.db");
        SeedVersionTwoTaggedFile(databasePath, rootPath, filePath);
        var scanner = new ManagedRootScanner(
            databasePath,
            _ => new FileIdentity("0000000000000001", "00000000000000000000000000000001", true, null));

        await scanner.ScanAsync(1);

        Assert.AreEqual(2L, Scalar<long>(databasePath, "SELECT COUNT(*) FROM files;"));
        Assert.AreEqual(0L, Scalar<long>(databasePath, "SELECT is_online FROM files WHERE id = 17;"));
        Assert.AreEqual(1L, Scalar<long>(databasePath, "SELECT COUNT(*) FROM file_tags WHERE file_id = 17 AND tag_id = 3 AND source = 'user';"));
        Assert.AreEqual(0L, Scalar<long>(databasePath, "SELECT COUNT(*) FROM file_tags ft JOIN files f ON f.id = ft.file_id WHERE f.is_online = 1 AND ft.source = 'user';"));
    }

    [TestMethod]
    public async Task EnumerationFailureDoesNotMarkUnvisitedFilesMissing()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        var blockedPath = Path.Combine(rootPath, "blocked");
        Directory.CreateDirectory(blockedPath);
        await File.WriteAllTextAsync(Path.Combine(blockedPath, "keep.txt"), "keep");
        var databasePath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(databasePath);
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);
        var fileId = Scalar<long>(databasePath, "SELECT id FROM files;");
        var failingScanner = new ManagedRootScanner(
            databasePath,
            FileIdentityReader.Read,
            path => string.Equals(path, blockedPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("simulated denial")
                : Directory.GetFileSystemEntries(path));

        var result = await failingScanner.ScanAsync(root.Id);

        Assert.HasCount(1, result.Failures);
        Assert.AreEqual(blockedPath, result.Failures[0].Path);
        Assert.AreEqual(0, result.MissingFiles);
        Assert.AreEqual(1L, Scalar<long>(databasePath, "SELECT is_online FROM files WHERE id = $id;", ("$id", fileId)));
    }

    [TestMethod]
    public async Task ReparsePointDirectoryIsNotTraversed()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        var outsidePath = temp.CreateDirectory("outside");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "inside.txt"), "inside");
        await File.WriteAllTextAsync(Path.Combine(outsidePath, "outside.txt"), "outside");
        var linkPath = Path.Combine(rootPath, "linked");
        Directory.CreateSymbolicLink(linkPath, outsidePath);
        temp.ReparsePoints.Add(linkPath);
        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(rootPath);

        var first = await scanner.ScanAsync(root.Id);
        var second = await scanner.ScanAsync(root.Id);

        Assert.AreEqual(1, first.CommittedFiles);
        Assert.AreEqual(1, second.CommittedFiles);
        Assert.AreEqual(1, CountFiles(scanner.DatabasePath, root.Id));
    }

    [TestMethod]
    public async Task RemovingRootOnlyDeletesIndexRows()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        var filePath = Path.Combine(rootPath, "keep.txt");
        await File.WriteAllTextAsync(filePath, "keep");
        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);

        Assert.IsTrue(scanner.RemoveRoot(root.Id));

        Assert.IsTrue(File.Exists(filePath));
        Assert.IsEmpty(scanner.ListRoots());
        Assert.AreEqual(0, CountFiles(scanner.DatabasePath, root.Id));
    }

    [TestMethod]
    public async Task TenThousandFilesScanRunsInBackgroundAndCompletes()
    {
        using var temp = TempDirectory.Create();
        var rootPath = temp.CreateDirectory("root");
        for (var index = 0; index < 10_000; index++)
        {
            File.Create(Path.Combine(rootPath, $"{index:D5}.dat")).Dispose();
        }

        var scanner = new ManagedRootScanner(Path.Combine(temp.Path, "index.db"));
        var root = scanner.AddRoot(rootPath);
        var scanTimer = System.Diagnostics.Stopwatch.StartNew();
        var callTimer = System.Diagnostics.Stopwatch.StartNew();

        var scanTask = scanner.ScanAsync(root.Id);

        callTimer.Stop();
        Assert.IsFalse(scanTask.IsCompleted, "The real scan completed inline instead of returning background work.");
        Assert.IsTrue(callTimer.Elapsed < TimeSpan.FromSeconds(1), $"ScanAsync blocked for {callTimer.Elapsed}.");
        var result = await scanTask.WaitAsync(TimeSpan.FromSeconds(120));
        scanTimer.Stop();

        Assert.IsFalse(result.Canceled);
        Assert.AreEqual(10_000, result.CommittedFiles);
        Assert.IsEmpty(result.Failures);
        Assert.AreEqual(10_000, CountFiles(scanner.DatabasePath, root.Id));
        Console.WriteLine($"10,000-file scan returned in {callTimer.Elapsed.TotalMilliseconds:F1} ms and completed in {scanTimer.Elapsed.TotalSeconds:F2} s.");
    }

    private static long CountFiles(string databasePath, long rootId)
    {
        using var connection = SqliteDatabase.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files WHERE root_id = $rootId;";
        command.Parameters.AddWithValue("$rootId", rootId);
        return (long)command.ExecuteScalar()!;
    }

    private static T Scalar<T>(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = SqliteDatabase.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)command.ExecuteScalar()!;
    }

    private static void AddUserTag(string databasePath, long fileId)
    {
        using var connection = SqliteDatabase.Open(databasePath);
        using var transaction = connection.BeginTransaction();
        using var tag = connection.CreateCommand();
        tag.Transaction = transaction;
        tag.CommandText = "INSERT INTO tags (name, normalized_name) VALUES ('Keep', 'keep') RETURNING id;";
        var tagId = (long)tag.ExecuteScalar()!;
        using var relation = connection.CreateCommand();
        relation.Transaction = transaction;
        relation.CommandText = "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fileId, $tagId, 'user');";
        relation.Parameters.AddWithValue("$fileId", fileId);
        relation.Parameters.AddWithValue("$tagId", tagId);
        relation.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void InsertIndexedGhost(string databasePath, ManagedRoot root)
    {
        using var connection = SqliteDatabase.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO files (
                root_id, volume_id, file_id, path, normalized_path,
                name, extension, size, modified_utc, identity_kind)
            VALUES ($rootId, 'volume', 'file', $path, $path, 'ghost.txt', '.txt', 0, '2026-08-31T00:00:00Z', 'stable');
            """;
        command.Parameters.AddWithValue("$rootId", root.Id);
        command.Parameters.AddWithValue("$path", Path.Combine(root.Path, "ghost.txt"));
        command.ExecuteNonQuery();
    }

    private static void SeedVersionTwoTaggedFile(string databasePath, string rootPath, string filePath)
    {
        using var connection = SqliteDatabase.Open(databasePath, 2);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO roots (id, path, normalized_path) VALUES (1, $rootPath, $rootPath);
            INSERT INTO files (
                id, root_id, volume_id, file_id, path, normalized_path,
                name, extension, size, modified_utc)
            VALUES (17, 1, 'path-fallback', $filePath, $filePath, $filePath, '资料.txt', '.txt', 6, '2026-08-31T00:00:00Z');
            INSERT INTO tags (id, name, normalized_name) VALUES (3, '保留', '保留');
            INSERT INTO file_tags (file_id, tag_id, source) VALUES (17, 3, 'user');
            """;
        command.Parameters.AddWithValue("$rootPath", rootPath);
        command.Parameters.AddWithValue("$filePath", filePath);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }
        public List<string> ReparsePoints { get; } = [];

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
        }

        public string CreateDirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            foreach (var path in ReparsePoints)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path);
                }
            }

            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
