using System.Diagnostics;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class ScanCrashRecoveryTests
{
    private sealed class TempContext : IDisposable
    {
        public string RootPath { get; }
        public string DatabasePath { get; }
        public string LogsPath { get; }
        public DiagnosticLogger Logger { get; }

        private TempContext(string rootPath, string databasePath, string logsPath, DiagnosticLogger logger)
        {
            RootPath = rootPath;
            DatabasePath = databasePath;
            LogsPath = logsPath;
            Logger = logger;
        }

        public static TempContext Create()
        {
            var basePath = Path.Combine(Path.GetTempPath(), $"GuraFile.CrashRecovery.{Guid.NewGuid():N}");
            var rootPath = Path.Combine(basePath, "root");
            var dbPath = Path.Combine(basePath, "index.db");
            var logsPath = Path.Combine(basePath, "logs");
            Directory.CreateDirectory(rootPath);
            Directory.CreateDirectory(logsPath);
            var logger = new DiagnosticLogger(logsPath);
            return new(rootPath, dbPath, logsPath, logger);
        }

        public void Dispose()
        {
            try
            {
                var dir = Path.GetDirectoryName(RootPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task ScanLifecycle_RecordsRunning_ThenCompletedSession()
    {
        using var temp = TempContext.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.RootPath, "file1.txt"), "hello");
        var scanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var root = scanner.AddRoot(temp.RootPath);

        var result = await scanner.ScanAsync(root.Id);
        Assert.AreEqual(1, result.CommittedFiles);

        using var connection = SqliteDatabase.Open(temp.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT scan_type, status, started_utc, completed_utc FROM scan_sessions WHERE root_id = $rootId;";
        command.Parameters.AddWithValue("$rootId", root.Id);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("full", reader.GetString(0));
        Assert.AreEqual("completed", reader.GetString(1));
        Assert.IsFalse(reader.IsDBNull(2));
        Assert.IsFalse(reader.IsDBNull(3));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    public async Task ScanCancellation_RecordsInterruptedSession()
    {
        using var temp = TempContext.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.RootPath, "file1.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(temp.RootPath, "file2.txt"), "world");
        var scanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var root = scanner.AddRoot(temp.RootPath);

        using var cts = new CancellationTokenSource();
        var result = await scanner.ScanAsync(
            root.Id,
            batchSize: 1,
            progress: _ => cts.Cancel(),
            cancellationToken: cts.Token);
        Assert.IsTrue(result.Canceled);

        using var connection = SqliteDatabase.Open(temp.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, completed_utc FROM scan_sessions WHERE root_id = $rootId;";
        command.Parameters.AddWithValue("$rootId", root.Id);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("interrupted", reader.GetString(0));
        Assert.IsFalse(reader.IsDBNull(1));
    }

    [TestMethod]
    public async Task GetInterruptedScanRoots_And_ResolveInterruptedSessions()
    {
        using var temp = TempContext.Create();
        var scanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var root = scanner.AddRoot(temp.RootPath);

        // Initially no interrupted sessions
        Assert.IsEmpty(scanner.GetInterruptedScanRoots());

        // Inject a running session simulating crashed scan
        using (var connection = SqliteDatabase.Open(temp.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO scan_sessions (root_id, scan_token, scan_type, status, started_utc)
                VALUES ($rootId, 'crash-token-1', 'full', 'running', $startedUtc);
                """;
            command.Parameters.AddWithValue("$rootId", root.Id);
            command.Parameters.AddWithValue("$startedUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var interruptedRoots = scanner.GetInterruptedScanRoots();
        Assert.HasCount(1, interruptedRoots);
        Assert.AreEqual(root.Id, interruptedRoots[0].Id);

        var sessions = scanner.GetInterruptedSessions(root.Id);
        Assert.HasCount(1, sessions);
        Assert.AreEqual("crash-token-1", sessions[0].ScanToken);

        // Resolve
        var resolvedCount = scanner.ResolveInterruptedSessions(root.Id);
        Assert.AreEqual(1, resolvedCount);

        // Now none running
        Assert.IsEmpty(scanner.GetInterruptedScanRoots());
        Assert.IsEmpty(scanner.GetInterruptedSessions(root.Id));
    }

    [TestMethod]
    public async Task CrashDuringEnumeration_ReconcilesParityOnRecovery()
    {
        using var temp = TempContext.Create();
        var fileA = Path.Combine(temp.RootPath, "fileA.txt");
        var fileB = Path.Combine(temp.RootPath, "fileB.txt");
        var fileC = Path.Combine(temp.RootPath, "fileC.txt");
        await File.WriteAllTextAsync(fileA, "A");
        await File.WriteAllTextAsync(fileB, "B");
        await File.WriteAllTextAsync(fileC, "C");

        var shouldCrash = true;
        var crashScanner = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                if (shouldCrash)
                {
                    throw new InvalidOperationException("Simulated crash during directory enumeration!");
                }
                return Directory.GetFileSystemEntries(path);
            },
            File.GetAttributes,
            new FileTypeClassifier().Classify,
            temp.Logger);

        var root = crashScanner.AddRoot(temp.RootPath);

        // Attempt scan which crashes during enumeration
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await crashScanner.ScanAsync(root.Id);
        });

        // Verify session left in 'running' status
        Assert.HasCount(1, crashScanner.GetInterruptedScanRoots());
        var interruptedSession = crashScanner.GetInterruptedSessions(root.Id).Single();
        Assert.AreEqual("running", interruptedSession.Status);

        // Simulate app restart: normal scanner & coordinator
        shouldCrash = false;
        var recoveryScanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var updates = new SemaphoreSlim(0);
        await using var coordinator = new FileChangeCoordinator(
            recoveryScanner,
            _ => updates.Release(),
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger);

        // Startup crash detection
        var roots = recoveryScanner.ListRoots();
        Assert.HasCount(1, roots);
        Assert.IsTrue(coordinator.CheckAndStartCrashRecovery(roots[0]));

        // Root should be recovering initially
        Assert.AreEqual(ManagedRootStatus.Recovering, recoveryScanner.ListRoots().Single().Status);

        // Wait for recovery to complete
        Assert.IsTrue(await updates.WaitAsync(TimeSpan.FromSeconds(5)));

        // Verify root is online
        Assert.AreEqual(ManagedRootStatus.Online, recoveryScanner.ListRoots().Single().Status);

        // Verify all 3 files are indexed and online
        var query = new FileQueryService(temp.DatabasePath);
        var files = await query.QueryAsync(new());
        Assert.HasCount(3, files);
        Assert.IsTrue(files.All(f => f.IsOnline));

        // Verify scan_sessions: old is interrupted, recovery is completed
        var allSessions = recoveryScanner.GetInterruptedSessions(root.Id);
        Assert.IsEmpty(allSessions);

        // Verify diagnostic logs contain CrashRecoveryDetected and CrashRecoveryCompleted
        var logFiles = Directory.GetFiles(temp.LogsPath, "gurafile_*.log");
        var logContent = string.Join("\n", logFiles.Select(File.ReadAllText));
        StringAssert.Contains(logContent, "CrashRecoveryDetected");
        StringAssert.Contains(logContent, "CrashRecoveryCompleted");
    }

    [TestMethod]
    public async Task CrashAfterPartialBatchWrite_ReconcilesWithoutDuplicatesOrLosingUserTags()
    {
        using var temp = TempContext.Create();
        // Create 5 files
        for (var i = 1; i <= 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(temp.RootPath, $"item_{i}.txt"), $"content_{i}");
        }

        var scanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var root = scanner.AddRoot(temp.RootPath);

        // Pre-scan 1 file and attach a user tag to it
        await scanner.ScanAsync(root.Id);
        var query = new FileQueryService(temp.DatabasePath);
        var initialFiles = await query.QueryAsync(new());
        Assert.HasCount(5, initialFiles);

        var tagService = new TagService(temp.DatabasePath);
        var importantTag = tagService.CreateTag("Important");
        var taggedFile = initialFiles.First(f => f.Name == "item_1.txt");
        tagService.AddTagToFiles(importantTag.Id, [taggedFile.Id]);

        // Verify tag is attached
        var tagsBefore = tagService.ListTagsForFile(taggedFile.Id);
        Assert.HasCount(1, tagsBefore);
        Assert.AreEqual("Important", tagsBefore[0].Name);

        // Now simulate a crash during a rescan:
        // Set batch size = 2 and simulate crash after first batch is written
        var batchesCommitted = 0;
        scanner.OnBatchCommitted = count =>
        {
            batchesCommitted++;
            if (batchesCommitted == 1)
            {
                throw new InvalidOperationException("Simulated crash after first batch written!");
            }
        };

        // Attempt scan
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await scanner.ScanAsync(root.Id, batchSize: 2);
        });

        // Reset hook
        scanner.OnBatchCommitted = null;

        // Verify interrupted session exists
        Assert.HasCount(1, scanner.GetInterruptedScanRoots());

        // Simulate app restart
        var restartScanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var updates = new SemaphoreSlim(0);
        await using var coordinator = new FileChangeCoordinator(
            restartScanner,
            _ => updates.Release(),
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger);

        Assert.IsTrue(coordinator.CheckAndStartCrashRecovery(restartScanner.ListRoots().Single()));
        Assert.IsTrue(await updates.WaitAsync(TimeSpan.FromSeconds(5)));

        // Verify:
        // 1. Exactly 5 files in database (no duplicates!)
        var finalFiles = await query.QueryAsync(new());
        Assert.HasCount(5, finalFiles);
        Assert.IsTrue(finalFiles.All(f => f.IsOnline));

        // 2. No duplicate relations and user tag is completely preserved
        var tagsAfter = tagService.ListTagsForFile(taggedFile.Id);
        Assert.HasCount(1, tagsAfter);
        Assert.AreEqual("Important", tagsAfter[0].Name);

        // 3. Foreign key check
        using (var conn = SqliteDatabase.Open(temp.DatabasePath))
        {
            DatabaseMigrationFixtures.AssertForeignKeys(conn);
        }
    }

    [TestMethod]
    public async Task CrashBeforeMarkMissing_ReconcilesDeletedFiles()
    {
        using var temp = TempContext.Create();
        var file1 = Path.Combine(temp.RootPath, "file1.txt");
        var file2 = Path.Combine(temp.RootPath, "file2.txt");
        var file3 = Path.Combine(temp.RootPath, "file3.txt");
        await File.WriteAllTextAsync(file1, "1");
        await File.WriteAllTextAsync(file2, "2");
        await File.WriteAllTextAsync(file3, "3");

        var scanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);

        var query = new FileQueryService(temp.DatabasePath);
        Assert.HasCount(3, await query.QueryAsync(new()));

        // Delete file3 from disk
        File.Delete(file3);

        // Crash before MarkMissing
        scanner.OnBeforeMarkMissing = () =>
        {
            throw new InvalidOperationException("Simulated crash right before MarkMissing!");
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await scanner.ScanAsync(root.Id);
        });

        scanner.OnBeforeMarkMissing = null;

        // In DB, file3 is still online because MarkMissing was skipped
        var interruptedFiles = await query.QueryAsync(new());
        Assert.IsTrue(interruptedFiles.Single(f => f.Name == "file3.txt").IsOnline);
        Assert.HasCount(1, scanner.GetInterruptedScanRoots());

        // Restart and recover
        var restartScanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var updates = new SemaphoreSlim(0);
        await using var coordinator = new FileChangeCoordinator(
            restartScanner,
            _ => updates.Release(),
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger);

        Assert.IsTrue(coordinator.CheckAndStartCrashRecovery(restartScanner.ListRoots().Single()));
        Assert.IsTrue(await updates.WaitAsync(TimeSpan.FromSeconds(5)));

        // Now file3 MUST be marked offline (IsOnline = false), while file1 and file2 remain online
        var reconciledFiles = await query.QueryAsync(new());
        Assert.IsFalse(reconciledFiles.Single(f => f.Name == "file3.txt").IsOnline);
        Assert.IsTrue(reconciledFiles.Single(f => f.Name == "file1.txt").IsOnline);
        Assert.IsTrue(reconciledFiles.Single(f => f.Name == "file2.txt").IsOnline);
    }

    [TestMethod]
    public async Task RepeatedRestarts_Idempotent_DoesNotAccumulateInfiniteRecoveryTasks()
    {
        using var temp = TempContext.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.RootPath, "data.txt"), "data");
        var scanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var root = scanner.AddRoot(temp.RootPath);

        // Inject uncompleted session
        using (var connection = SqliteDatabase.Open(temp.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO scan_sessions (root_id, scan_token, scan_type, status, started_utc)
                VALUES ($rootId, 'loop-test-token', 'full', 'running', $startedUtc);
                """;
            command.Parameters.AddWithValue("$rootId", root.Id);
            command.Parameters.AddWithValue("$startedUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        // Restart 1
        var scansCount = 0;
        var recoveryScanner1 = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                Interlocked.Increment(ref scansCount);
                return Directory.GetFileSystemEntries(path);
            },
            File.GetAttributes,
            new FileTypeClassifier().Classify,
            temp.Logger);

        var updates1 = new SemaphoreSlim(0);
        await using (var coord1 = new FileChangeCoordinator(
            recoveryScanner1,
            _ => updates1.Release(),
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger))
        {
            Assert.IsTrue(coord1.CheckAndStartCrashRecovery(recoveryScanner1.ListRoots().Single()));
            Assert.IsTrue(await updates1.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.AreEqual(1, scansCount);

        // Restart 2: no uncompleted sessions exist
        var recoveryScanner2 = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                Interlocked.Increment(ref scansCount);
                return Directory.GetFileSystemEntries(path);
            },
            File.GetAttributes,
            new FileTypeClassifier().Classify,
            temp.Logger);

        await using (var coord2 = new FileChangeCoordinator(
            recoveryScanner2,
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger))
        {
            // CheckAndStartCrashRecovery MUST return false because session was already resolved and completed
            Assert.IsFalse(coord2.CheckAndStartCrashRecovery(recoveryScanner2.ListRoots().Single()));
        }

        // Restart 3
        await using (var coord3 = new FileChangeCoordinator(
            recoveryScanner2,
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger))
        {
            Assert.IsFalse(coord3.CheckAndStartCrashRecovery(recoveryScanner2.ListRoots().Single()));
        }

        // Total scans across all restarts is still exactly 1
        Assert.AreEqual(1, scansCount);
    }

    [TestMethod]
    public async Task NormalShutdown_DoesNotTriggerSuperfluousScanOnRestart()
    {
        using var temp = TempContext.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.RootPath, "normal.txt"), "normal");
        var scanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var root = scanner.AddRoot(temp.RootPath);

        // Normal full scan
        var result = await scanner.ScanAsync(root.Id);
        Assert.AreEqual(1, result.CommittedFiles);

        // Verify session status is 'completed'
        using (var conn = SqliteDatabase.Open(temp.DatabasePath))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT status FROM scan_sessions WHERE root_id = $rootId;";
            cmd.Parameters.AddWithValue("$rootId", root.Id);
            Assert.AreEqual("completed", (string)cmd.ExecuteScalar()!);
        }

        // Simulate app restart
        var scansCount = 0;
        var restartScanner = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path =>
            {
                Interlocked.Increment(ref scansCount);
                return Directory.GetFileSystemEntries(path);
            },
            File.GetAttributes,
            new FileTypeClassifier().Classify,
            temp.Logger);

        await using var coordinator = new FileChangeCoordinator(
            restartScanner,
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger);

        var currentRoot = restartScanner.ListRoots().Single();
        // CheckAndStartCrashRecovery must return false
        var triggered = coordinator.CheckAndStartCrashRecovery(currentRoot);
        Assert.IsFalse(triggered);

        // Watch normally
        var watched = coordinator.Watch(currentRoot);
        Assert.IsTrue(watched);

        // Zero scans should have been run!
        Assert.AreEqual(0, scansCount);
    }

    [TestMethod]
    public async Task CrashDuringRecovery_RecoversAgainOnSubsequentRestart()
    {
        using var temp = TempContext.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.RootPath, "rec_item.txt"), "rec");
        var scanner = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var root = scanner.AddRoot(temp.RootPath);

        // Initial crash setup: running session
        using (var connection = SqliteDatabase.Open(temp.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO scan_sessions (root_id, scan_token, scan_type, status, started_utc)
                VALUES ($rootId, 'initial-crash', 'full', 'running', $startedUtc);
                """;
            command.Parameters.AddWithValue("$rootId", root.Id);
            command.Parameters.AddWithValue("$startedUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        // Restart 1: recovery scanner crashes during its recovery scan
        var crashRecoveryScanner = new ManagedRootScanner(
            temp.DatabasePath,
            FileIdentityReader.Read,
            path => throw new InvalidOperationException("Crashed during recovery scan!"),
            File.GetAttributes,
            new FileTypeClassifier().Classify,
            temp.Logger);

        var reportedError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var coord1 = new FileChangeCoordinator(
            crashRecoveryScanner,
            onError: ex => reportedError.TrySetResult(ex),
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger))
        {
            coord1.CheckAndStartCrashRecovery(crashRecoveryScanner.ListRoots().Single());
            await reportedError.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Verify a new session was created with status 'running' (representing the crashed recovery scan)
        var interruptedSessions = scanner.GetInterruptedSessions(root.Id);
        Assert.HasCount(1, interruptedSessions);
        Assert.AreEqual("recovery", interruptedSessions[0].ScanType);

        // Restart 2: successful recovery
        var recoveryScanner2 = new ManagedRootScanner(temp.DatabasePath, temp.Logger);
        var updates2 = new SemaphoreSlim(0);
        await using (var coord2 = new FileChangeCoordinator(
            recoveryScanner2,
            _ => updates2.Release(),
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger))
        {
            Assert.IsTrue(coord2.CheckAndStartCrashRecovery(recoveryScanner2.ListRoots().Single()));
            Assert.IsTrue(await updates2.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        // Parity verified
        Assert.AreEqual(ManagedRootStatus.Online, recoveryScanner2.ListRoots().Single().Status);
        var query = new FileQueryService(temp.DatabasePath);
        Assert.IsTrue((await query.QueryAsync(new())).Single().IsOnline);

        // Restart 3: clean normal startup
        await using (var coord3 = new FileChangeCoordinator(
            recoveryScanner2,
            debounce: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromHours(1),
            logger: temp.Logger))
        {
            Assert.IsFalse(coord3.CheckAndStartCrashRecovery(recoveryScanner2.ListRoots().Single()));
        }
    }
}
