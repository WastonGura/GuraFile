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
