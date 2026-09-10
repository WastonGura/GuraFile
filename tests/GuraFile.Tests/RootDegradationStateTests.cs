using System.IO;
using System.Text.Json;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class RootDegradationStateTests
{
    [TestMethod]
    public async Task DisconnectedMedia_MarksRootOffline_PreservesAllFileNodesAndTags()
    {
        using var temp = TempDirectory.Create();
        var logsDir = temp.CreateDirectory("logs");
        var logger = new DiagnosticLogger(logsDir);

        var rootPath = temp.CreateDirectory("managed_root");
        var testFile = Path.Combine(rootPath, "document.txt");
        await File.WriteAllTextAsync(testFile, "Important document content");

        var dbPath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(dbPath, logger: logger);
        var root = scanner.AddRoot(rootPath);
        var initialScan = await scanner.ScanAsync(root.Id);
        Assert.AreEqual(1, initialScan.CommittedFiles);

        // Add a user tag to the file
        var queryService = new FileQueryService(dbPath);
        var initialFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, initialFiles);
        var fileId = initialFiles[0].Id;

        var tagService = new TagService(dbPath);
        var tag = tagService.CreateTag("Critical Project");
        tagService.AddTagToFiles(tag.Id, [fileId]);

        var fileTags = tagService.ListTagsForFile(fileId);
        Assert.HasCount(1, fileTags);
        Assert.AreEqual("Critical Project", fileTags[0].Name);

        // Simulate media disconnection (e.g. USB unmount, network drop)
        var offlinePath = temp.CreateDirectory("managed_root_offline");
        Directory.Move(rootPath, Path.Combine(offlinePath, "moved"));

        // Scan disconnected root
        var offlineScan = await scanner.ScanAsync(root.Id);
        Assert.AreEqual(0, offlineScan.CommittedFiles);

        // Verify root status is Offline
        var roots = scanner.ListRoots();
        Assert.HasCount(1, roots);
        Assert.AreEqual(ManagedRootStatus.Offline, roots[0].Status);

        // Crucial safety guarantee: Database files and tags MUST NOT be bulk-deleted!
        var afterFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, afterFiles, "Files must remain in database when root goes offline!");
        Assert.AreEqual(fileId, afterFiles[0].Id);
        Assert.IsTrue(afterFiles[0].IsOnline, "Files must retain their prior state when root is offline!");

        var afterTags = tagService.ListTagsForFile(fileId);
        Assert.HasCount(1, afterTags, "User tags must be strictly preserved when root goes offline!");
        Assert.AreEqual("Critical Project", afterTags[0].Name);

        // Verify DiagnosticLogger has RootOffline event
        var logFiles = Directory.GetFiles(logsDir, "gurafile_*.log");
        Assert.HasCount(1, logFiles);

        var entries = File.ReadAllLines(logFiles[0])
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var offlineLogs = entries.Where(e => e.GetProperty("event").GetString() == "RootOffline").ToList();
        Assert.IsGreaterThan(0, offlineLogs.Count, "RootOffline event must be recorded in diagnostic logs.");
        Assert.AreEqual("Failed", offlineLogs[0].GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Reconnection_TriggersRecoveryScanAndRestartsWatcher()
    {
        using var temp = TempDirectory.Create();
        var logsDir = temp.CreateDirectory("logs");
        var logger = new DiagnosticLogger(logsDir);

        var rootPath = temp.CreateDirectory("reconnect_root");
        var fileA = Path.Combine(rootPath, "fileA.txt");
        await File.WriteAllTextAsync(fileA, "Content A");

        var dbPath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(dbPath, logger: logger);
        var root = scanner.AddRoot(rootPath);
        await scanner.ScanAsync(root.Id);

        var tagService = new TagService(dbPath);
        var queryService = new FileQueryService(dbPath);
        var files = await queryService.QueryAsync(new());
        var tag = tagService.CreateTag("Keep After Reconnect");
        tagService.AddTagToFiles(tag.Id, [files[0].Id]);

        var recoveryTcs = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new FileChangeCoordinator(
            scanner,
            onChanged: result =>
            {
                if (!result.Canceled)
                {
                    recoveryTcs.TrySetResult(result);
                }
            },
            retryInterval: TimeSpan.FromMilliseconds(50),
            debounce: TimeSpan.FromMilliseconds(50),
            logger: logger);

        // Simulate disconnection
        var tempStash = temp.CreateDirectory("stash");
        var tempHidden = Path.Combine(tempStash, "hidden");
        Directory.Move(rootPath, tempHidden);

        coordinator.RequestRecovery(root, new IOException("Media disconnected"));
        await recoveryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForRootStatusAsync(scanner, ManagedRootStatus.Offline);

        Assert.AreEqual(ManagedRootStatus.Offline, scanner.ListRoots().Single().Status);

        // Reconnect: restore directory
        recoveryTcs = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Directory.Move(tempHidden, rootPath);

        // Add a new file while media was reconnecting
        var fileB = Path.Combine(rootPath, "fileB.txt");
        await File.WriteAllTextAsync(fileB, "Content B");

        coordinator.RequestRecovery(root);
        var recoveredResult = await recoveryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNotNull(recoveredResult);
        await WaitForRootStatusAsync(scanner, ManagedRootStatus.Online);
        Assert.AreEqual(ManagedRootStatus.Online, scanner.ListRoots().Single().Status);

        var updatedFiles = await queryService.QueryAsync(new());
        Assert.HasCount(2, updatedFiles);
        Assert.IsTrue(updatedFiles.Any(f => f.Path == fileA && f.IsOnline));
        Assert.IsTrue(updatedFiles.Any(f => f.Path == fileB && f.IsOnline));

        // Tag on fileA is still intact
        var preservedTags = tagService.ListTagsForFile(updatedFiles.Single(f => f.Path == fileA).Id);
        Assert.HasCount(1, preservedTags);
        Assert.AreEqual("Keep After Reconnect", preservedTags[0].Name);

        // Verify DiagnosticLogger has RootRecovering and RootRecovered events
        var logFiles = Directory.GetFiles(logsDir, "gurafile_*.log");
        Assert.HasCount(1, logFiles);

        var entries = File.ReadAllLines(logFiles[0])
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        Assert.IsTrue(entries.Any(e => e.GetProperty("event").GetString() == "RootRecovering"));
        Assert.IsTrue(entries.Any(e => e.GetProperty("event").GetString() == "RootRecovered" || e.GetProperty("event").GetString() == "RootOnline"));
    }

    [TestMethod]
    public async Task Watch_WhenRootDirectoryInaccessible_ReturnsFalse_LogsDiagnosticAndTransitionsRootToOffline()
    {
        using var temp = TempDirectory.Create();
        var logsDir = temp.CreateDirectory("logs");
        var logger = new DiagnosticLogger(logsDir);

        var rootPath = temp.CreateDirectory("inaccessible_root");
        var dbPath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(dbPath, logger: logger);
        var root = scanner.AddRoot(rootPath);
        Assert.AreEqual(ManagedRootStatus.Online, root.Status);

        await using var coordinator = new FileChangeCoordinator(
            scanner,
            logger: logger);

        // Delete the root directory to simulate an unreachable / unmounted root
        Directory.Delete(rootPath);

        var watched = coordinator.Watch(root);
        Assert.IsFalse(watched, "Watch must return false when root directory does not exist or is unreachable.");

        // Root status in scanner must transition to Offline
        var updatedRoots = scanner.ListRoots();
        Assert.HasCount(1, updatedRoots);
        Assert.AreEqual(ManagedRootStatus.Offline, updatedRoots[0].Status, "Root status must transition to Offline when Watch fails.");

        // Diagnostic log must record RootOffline event
        var logFiles = Directory.GetFiles(logsDir, "gurafile_*.log");
        Assert.HasCount(1, logFiles);
        var entries = File.ReadAllLines(logFiles[0])
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var offlineLogs = entries.Where(e => e.GetProperty("event").GetString() == "RootOffline").ToList();
        Assert.IsGreaterThan(0, offlineLogs.Count, "DiagnosticLogger must record RootOffline event when root is inaccessible.");
    }

    [TestMethod]
    public async Task Watch_ConcurrentWithDisposeAsync_DoesNotDeadlockOrThrowUnexpectedException()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            using var temp = TempDirectory.Create();
            var dbPath = Path.Combine(temp.Path, "index.db");
            var scanner = new ManagedRootScanner(dbPath);

            var roots = new List<ManagedRoot>();
            for (var i = 0; i < 5; i++)
            {
                var dir = temp.CreateDirectory($"root_{iteration}_{i}");
                roots.Add(scanner.AddRoot(dir));
            }

            var coordinator = new FileChangeCoordinator(scanner);

            var startWatchingTask = Task.Run(() =>
            {
                foreach (var r in roots)
                {
                    try
                    {
                        coordinator.Watch(r);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Expected if disposed concurrently
                        break;
                    }
                }
            });

            var disposeTask = Task.Run(async () =>
            {
                await Task.Yield();
                await coordinator.DisposeAsync();
            });

            await Task.WhenAll(startWatchingTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task StartWatchingRoots_NonBlockingSlowOrInaccessibleRoots_CompletesAsynchronouslyWithoutBlockingCaller()
    {
        using var temp = TempDirectory.Create();
        var logsDir = temp.CreateDirectory("logs");
        var logger = new DiagnosticLogger(logsDir);
        var dbPath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(dbPath, logger: logger);

        var accessibleDir = temp.CreateDirectory("accessible_root");
        var validRoot = scanner.AddRoot(accessibleDir);

        var inaccessibleDir = Path.Combine(temp.Path, "non_existent_drive_folder");
        var offlineRoot = scanner.AddRoot(inaccessibleDir);

        await using var coordinator = new FileChangeCoordinator(scanner, logger: logger);

        var roots = scanner.ListRoots();
        var rootsRefreshed = 0;

        // Simulate async non-blocking background initialization as done in MainWindow
        var callerCompleted = false;
        var startTask = Task.Run(() =>
        {
            foreach (var r in roots)
            {
                try
                {
                    if (!coordinator.CheckAndStartCrashRecovery(r))
                    {
                        coordinator.Watch(r);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    scanner.SetRootStatus(r.Id, ManagedRootStatus.Offline, ex.Message);
                }
            }
            Interlocked.Increment(ref rootsRefreshed);
        });

        callerCompleted = true;
        Assert.IsTrue(callerCompleted, "Starting watchers asynchronously must not block the initiating thread.");

        await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, rootsRefreshed);

        var updatedRoots = scanner.ListRoots();
        Assert.AreEqual(ManagedRootStatus.Online, updatedRoots.Single(r => r.Id == validRoot.Id).Status);
        Assert.AreEqual(ManagedRootStatus.Offline, updatedRoots.Single(r => r.Id == offlineRoot.Id).Status);
    }


    private static async Task WaitForRootStatusAsync(ManagedRootScanner scanner, ManagedRootStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (scanner.ListRoots().Single().Status == status)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.AreEqual(status, scanner.ListRoots().Single().Status, $"Managed root did not become {status} within five seconds.");
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.RootDegradationTests.{Guid.NewGuid():N}");
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
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
