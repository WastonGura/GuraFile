using System.Diagnostics;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class FileChangeRecoveryTests
{
    [TestMethod]
    public async Task RecoveryRequestsAreMergedAndRepairDroppedEvents()
    {
        using var temp = TempDirectory.Create();
        var oldPath = Path.Combine(temp.RootPath, "old.txt");
        await File.WriteAllTextAsync(oldPath, "old");
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);
        File.Delete(oldPath);
        var newPath = Path.Combine(temp.RootPath, "new.md");
        await File.WriteAllTextAsync(newPath, "new");
        var scans = 0;
        var recoveryScanner = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                Interlocked.Increment(ref scans);
                return Directory.GetFileSystemEntries(path);
            });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new FileChangeCoordinator(
            recoveryScanner,
            _ => completed.TrySetResult(),
            retryInterval: TimeSpan.FromHours(1));

        for (var index = 0; index < 10; index++)
        {
            coordinator.RequestRecovery(root, new InternalBufferOverflowException("lost events"));
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var files = await new FileQueryService(temp.DatabasePath).QueryAsync(new());
        Assert.IsFalse(files.Single(file => file.Path == oldPath).IsOnline);
        Assert.IsTrue(files.Single(file => file.Path == newPath).IsOnline);
        Assert.AreEqual(1, scans);
        Assert.AreEqual(ManagedRootStatus.Online, recoveryScanner.ListRoots().Single().Status);
    }

    [TestMethod]
    public async Task StartSchedulesNonBlockingCompensation()
    {
        using var temp = TempDirectory.Create();
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var startupScanner = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(2));
                return Directory.GetFileSystemEntries(path);
            });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new FileChangeCoordinator(
            startupScanner,
            _ => completed.TrySetResult(),
            retryInterval: TimeSpan.FromHours(1));
        var stopwatch = Stopwatch.StartNew();

        coordinator.Start(root);

        stopwatch.Stop();
        Assert.IsLessThan(100, stopwatch.ElapsedMilliseconds);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(ManagedRootStatus.Recovering, startupScanner.ListRoots().Single().Status);
        release.Set();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ManagedRootStatus.Online, startupScanner.ListRoots().Single().Status);
    }

    [TestMethod]
    public async Task OfflineRootPreservesTagsAndAutomaticallyRecovers()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.RootPath, "keep.txt");
        await File.WriteAllTextAsync(filePath, "before");
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);
        var indexed = (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single();
        var tags = new TagService(temp.DatabasePath);
        var tag = tags.CreateTag("Keep offline");
        tags.AddTagToFiles(tag.Id, [indexed.Id]);
        var updates = new SemaphoreSlim(0);
        await using var coordinator = new FileChangeCoordinator(
            scanner,
            _ => updates.Release(),
            retryInterval: TimeSpan.FromMilliseconds(50));
        var unavailablePath = temp.RootPath + "-offline";
        Directory.Move(temp.RootPath, unavailablePath);

        coordinator.RequestRecovery(root, new IOException("device unavailable"));
        Assert.IsTrue(await updates.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.AreEqual(ManagedRootStatus.Offline, scanner.ListRoots().Single().Status);
        Assert.IsTrue((await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single(file => file.Id == indexed.Id).IsOnline);
        Assert.AreEqual("Keep offline", tags.ListTagsForFile(indexed.Id).Single().Name);
        await File.AppendAllTextAsync(Path.Combine(unavailablePath, "keep.txt"), "-changed");
        Directory.Move(unavailablePath, temp.RootPath);
        Assert.IsTrue(await updates.WaitAsync(TimeSpan.FromSeconds(2)));

        var recovered = (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single(file => file.Id == indexed.Id);
        Assert.AreEqual(ManagedRootStatus.Online, scanner.ListRoots().Single().Status);
        Assert.IsTrue(recovered.IsOnline);
        Assert.IsGreaterThan(indexed.Size, recovered.Size);
        Assert.AreEqual("Keep offline", tags.ListTagsForFile(indexed.Id).Single().Name);
    }

    [TestMethod]
    public async Task RecoveryWatchesChangesThatOccurDuringTheCompensationScan()
    {
        using var temp = TempDirectory.Create();
        var blocked = Directory.CreateDirectory(Path.Combine(temp.RootPath, "blocked")).FullName;
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var recoveryScanner = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                if (string.Equals(path, blocked, StringComparison.OrdinalIgnoreCase))
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(2));
                }

                return Directory.GetFileSystemEntries(path);
            });
        await using var coordinator = new FileChangeCoordinator(
            recoveryScanner,
            retryInterval: TimeSpan.FromHours(1),
            debounce: TimeSpan.FromMilliseconds(30));

        coordinator.RequestRecovery(root);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(1)));
        var latePath = Path.Combine(temp.RootPath, "late.txt");
        await File.WriteAllTextAsync(latePath, "late");
        release.Set();

        await WaitForFileAsync(temp.DatabasePath, latePath);
    }

    [TestMethod]
    public async Task RecoveryRequestedDuringAnActiveScanRunsOneFollowUpScan()
    {
        using var temp = TempDirectory.Create();
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var scans = 0;
        var recoveryScanner = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                if (Interlocked.Increment(ref scans) == 1)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(2));
                }

                return Directory.GetFileSystemEntries(path);
            });
        var completed = new SemaphoreSlim(0);
        await using var coordinator = new FileChangeCoordinator(
            recoveryScanner,
            _ => completed.Release(),
            retryInterval: TimeSpan.FromHours(1),
            debounce: TimeSpan.FromMilliseconds(30));

        coordinator.RequestRecovery(root);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(1)));
        for (var index = 0; index < 10; index++)
        {
            coordinator.RequestRecovery(root, new InternalBufferOverflowException($"loss {index}"));
        }
        release.Set();

        Assert.IsTrue(await completed.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(await completed.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(150);
        Assert.AreEqual(2, scans);
    }

    [TestMethod]
    public async Task UnwatchDuringRecoveryDoesNotReattachTheRoot()
    {
        using var temp = TempDirectory.Create();
        var scanner = new ManagedRootScanner(temp.DatabasePath);
        var root = scanner.AddRoot(temp.RootPath);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var recoveryScanner = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(2));
                return Directory.GetFileSystemEntries(path);
            });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new FileChangeCoordinator(
            recoveryScanner,
            _ => completed.TrySetResult(),
            retryInterval: TimeSpan.FromHours(1),
            debounce: TimeSpan.FromMilliseconds(30));

        coordinator.RequestRecovery(root);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(1)));
        coordinator.Unwatch(root.Id);
        release.Set();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var ignoredPath = Path.Combine(temp.RootPath, "ignored.txt");
        await File.WriteAllTextAsync(ignoredPath, "ignored");
        await Task.Delay(300);

        Assert.IsFalse((await new FileQueryService(temp.DatabasePath).QueryAsync(new()))
            .Any(file => file.Path == ignoredPath));
    }

    private static async Task WaitForFileAsync(string databasePath, string path)
    {
        var query = new FileQueryService(databasePath);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if ((await query.QueryAsync(new())).Any(file => file.Path == path && file.IsOnline))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"File was not recovered: {path}");
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
            RootPath = System.IO.Path.Combine(path, "root");
            DatabasePath = System.IO.Path.Combine(path, "index.db");
            Directory.CreateDirectory(RootPath);
        }

        public string Path { get; }
        public string RootPath { get; }
        public string DatabasePath { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"GuraFile.Recovery.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose()
        {
            var offlinePath = RootPath + "-offline";
            if (Directory.Exists(offlinePath) && !Directory.Exists(RootPath))
            {
                Directory.Move(offlinePath, RootPath);
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
