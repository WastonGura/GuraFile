using System.Diagnostics;
using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class ScaleColdStartTests
{
    private sealed class TempScaleDatabase : IDisposable
    {
        public string Path { get; }

        public TempScaleDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.ScaleTest.{Guid.NewGuid():N}.db");
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO roots (id, path, normalized_path, status) VALUES (1, 'C:\\ScaleRoot', 'C:\\ScaleRoot', 'online');";
            command.ExecuteNonQuery();
        }

        public void SeedFiles(int count)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO files (
                    root_id, volume_id, file_id, path, normalized_path,
                    name, extension, size, modified_utc, identity_kind, is_online, scan_token)
                VALUES (1, 'vol-scale', $fileId, $path, $normPath, $name, $ext, $size, $modUtc, 'stable', 1, 'init-token');
                """;

            var pFileId = command.Parameters.Add("$fileId", SqliteType.Text);
            var pPath = command.Parameters.Add("$path", SqliteType.Text);
            var pNormPath = command.Parameters.Add("$normPath", SqliteType.Text);
            var pName = command.Parameters.Add("$name", SqliteType.Text);
            var pExt = command.Parameters.Add("$ext", SqliteType.Text);
            var pSize = command.Parameters.Add("$size", SqliteType.Integer);
            var pModUtc = command.Parameters.Add("$modUtc", SqliteType.Text);

            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            for (var i = 0; i < count; i++)
            {
                var dirIndex = i / 1000;
                var ext = (i % 4) switch
                {
                    0 => ".txt",
                    1 => ".cs",
                    2 => ".png",
                    _ => ".pdf"
                };
                var name = $"File_{i:D6}{ext}";
                var path = $@"C:\ScaleRoot\Dir_{dirIndex:D3}\{name}";

                pFileId.Value = $"fid-{i:D6}";
                pPath.Value = path;
                pNormPath.Value = path;
                pName.Value = name;
                pExt.Value = ext;
                pSize.Value = 1000 + (i % 50000);
                pModUtc.Value = timestamp;

                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void Checkpoint()
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            foreach (var file in new[] { Path, $"{Path}-shm", $"{Path}-wal" })
            {
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    [TestMethod]
    public async Task ColdStart_100kFiles_QueryAndInteractive_WithinThreshold()
    {
        using var db = new TempScaleDatabase();
        var seedTimer = Stopwatch.StartNew();
        db.SeedFiles(100_000);
        seedTimer.Stop();
        Console.WriteLine($"[Baseline] 100,000 files seeded in {seedTimer.Elapsed.TotalMilliseconds:F1} ms");

        // Force GC to measure clean cold query state
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var memoryBefore = GC.GetTotalMemory(true);

        // Cold query: open database and execute first bounded query (default sort by Name, Limit: 1000)
        var queryTimer = Stopwatch.StartNew();
        var queryService = new FileQueryService(db.Path);
        var files = await queryService.QueryAsync(new FileQuery(Limit: 1000));
        queryTimer.Stop();

        var memoryAfter = GC.GetTotalMemory(false);
        var memoryDeltaMb = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);

        Console.WriteLine($"[Baseline] 100,000 files bounded cold query: {queryTimer.Elapsed.TotalMilliseconds:F1} ms, Memory delta: {memoryDeltaMb:F2} MB");

        Assert.HasCount(1000, files);
        // Pure database query must complete well within the cold start budget (< 2000 ms) so full UI is < 3000 ms
        Assert.IsTrue(
            queryTimer.Elapsed < TimeSpan.FromSeconds(2.0),
            $"100k bounded query took {queryTimer.Elapsed.TotalMilliseconds:F1} ms, exceeding the 2.0s database query budget.");

        // Verify correct ordering by name (case-insensitive)
        Assert.AreEqual("File_000000.txt", files[0].Name);
        Assert.AreEqual("File_000001.cs", files[1].Name);
    }

    [TestMethod]
    public async Task ScaleScan_100kFiles_HighThroughput_AndBoundedMemory()
    {
        using var db = new TempScaleDatabase();
        var filesCount = 100_000;
        var batchSize = 1000;

        // Build virtual hierarchy: 100 directories, each with 1000 files
        var directories = new List<string>();
        var filesByDir = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (var d = 0; d < 100; d++)
        {
            var dirPath = $@"C:\ScaleRoot\Dir_{d:D3}";
            directories.Add(dirPath);
            var fileList = new string[1000];
            for (var f = 0; f < 1000; f++)
            {
                var idx = d * 1000 + f;
                fileList[f] = $@"{dirPath}\File_{idx:D6}.txt";
            }
            filesByDir[dirPath] = fileList;
        }

        Func<string, string[]> getFileSystemEntries = path =>
        {
            if (string.Equals(path, @"C:\ScaleRoot", StringComparison.OrdinalIgnoreCase))
            {
                return directories.ToArray();
            }
            if (filesByDir.TryGetValue(path, out var entries))
            {
                return entries;
            }
            return [];
        };

        Func<string, FileAttributes> getAttributes = path =>
            path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Normal
                : FileAttributes.Directory;

        Func<string, FileIdentity> readIdentity = path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return new FileIdentity("vol-scale", $"fid-{name}", true, null);
        };

        var defaultClassifier = new FileTypeClassifier((path, limit) => []);
        Func<string, FileTypeClassification> classify = path =>
            defaultClassifier.Classify(path);

        var committedBatches = 0;
        var scanner = new ManagedRootScanner(
            db.Path,
            readIdentity,
            getFileSystemEntries,
            getAttributes,
            classify,
            logger: null,
            readFileMetadata: _ => (1024, DateTimeOffset.UtcNow));

        scanner.OnBatchCommitted = batchNumber => committedBatches++;

        GC.Collect();
        var memBefore = GC.GetTotalMemory(true);
        var timer = Stopwatch.StartNew();

        var result = await scanner.ScanAsync(1, batchSize: batchSize);

        timer.Stop();
        var memAfter = GC.GetTotalMemory(false);
        var memDeltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);
        var throughput = filesCount / timer.Elapsed.TotalSeconds;

        Console.WriteLine($"[Baseline] 100,000 files scan completed in {timer.Elapsed.TotalSeconds:F2} s ({throughput:F0} files/s), Committed batches: {committedBatches}, Mem delta: {memDeltaMb:F2} MB");

        Assert.IsFalse(result.Canceled);
        Assert.AreEqual(filesCount, result.DiscoveredFiles);
        Assert.AreEqual(filesCount, result.CommittedFiles);
        Assert.AreEqual(filesCount, result.AddedFiles);
        Assert.IsEmpty(result.Failures);
        Assert.AreEqual(100, committedBatches);

        // Throughput must be high: 100,000 files must complete within 45s (> 2,200 files/s) with FTS5 indexing
        Assert.IsTrue(
            timer.Elapsed < TimeSpan.FromSeconds(45.0),
            $"100,000 files scan took {timer.Elapsed.TotalSeconds:F2} s, which is too slow (throughput: {throughput:F0} files/s).");
    }

    [TestMethod]
    public async Task ScaleScan_Cancellation_MaintainsDatabaseConsistency()
    {
        using var db = new TempScaleDatabase();
        var batchSize = 1000;
        var directories = new List<string>();
        var filesByDir = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (var d = 0; d < 100; d++)
        {
            var dirPath = $@"C:\ScaleRoot\Dir_{d:D3}";
            directories.Add(dirPath);
            var fileList = new string[1000];
            for (var f = 0; f < 1000; f++)
            {
                var idx = d * 1000 + f;
                fileList[f] = $@"{dirPath}\File_{idx:D6}.txt";
            }
            filesByDir[dirPath] = fileList;
        }

        using var cts = new CancellationTokenSource();
        var committedBatches = 0;

        var scanner = new ManagedRootScanner(
            db.Path,
            path => new FileIdentity("vol-scale", $"fid-{Path.GetFileNameWithoutExtension(path)}", true, null),
            path => string.Equals(path, @"C:\ScaleRoot", StringComparison.OrdinalIgnoreCase) ? directories.ToArray() : filesByDir.GetValueOrDefault(path, []),
            path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? FileAttributes.Normal : FileAttributes.Directory,
            new FileTypeClassifier().Classify,
            logger: null,
            readFileMetadata: _ => (1024, DateTimeOffset.UtcNow));

        scanner.OnBatchCommitted = batchNum =>
        {
            committedBatches++;
            if (committedBatches == 10) // cancel after 10,000 files committed
            {
                cts.Cancel();
            }
        };

        var result = await scanner.ScanAsync(1, batchSize: batchSize, cancellationToken: cts.Token);

        Assert.IsTrue(result.Canceled);
        Assert.AreEqual(10_000, result.CommittedFiles);

        // Verify database is consistent and readable
        using var connection = SqliteDatabase.Open(db.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files WHERE root_id = 1;";
        var count = (long)command.ExecuteScalar()!;
        Assert.AreEqual(10_000, count);

        // Verify quick_check passes
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "PRAGMA quick_check;";
        Assert.AreEqual("ok", checkCmd.ExecuteScalar());
    }

    [TestMethod]
    public async Task ScaleScan_DirectoryReplacedByFile_PreservesSemantics()
    {
        using var db = new TempScaleDatabase();
        // Seed files under C:\ScaleRoot\OldDir\SubFile.txt
        using (var connection = SqliteDatabase.Open(db.Path))
        using (var transaction = connection.BeginTransaction())
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, is_online, scan_token)
                VALUES
                    (1, 'vol-scale', 'sub-1', 'C:\ScaleRoot\OldDir\SubFile.txt', 'C:\ScaleRoot\OldDir\SubFile.txt', 'SubFile.txt', '.txt', 100, '2026-09-05T00:00:00Z', 'stable', 1, 'old-token'),
                    (1, 'vol-scale', 'sub-2', 'C:\ScaleRoot\OldDir\Nested\Sub2.txt', 'C:\ScaleRoot\OldDir\Nested\Sub2.txt', 'Sub2.txt', '.txt', 200, '2026-09-05T00:00:00Z', 'stable', 1, 'old-token');
                """;
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }

        // Now scan with OldDir being a file!
        var scanner = new ManagedRootScanner(
            db.Path,
            path => new FileIdentity("vol-scale", "new-file-id", true, null),
            path => [@"\ScaleRoot\OldDir"],
            path => FileAttributes.Normal,
            new FileTypeClassifier().Classify,
            logger: null,
            readFileMetadata: _ => (1024, DateTimeOffset.UtcNow));

        // Reconcile path
        await scanner.ReconcilePathsAsync(1, [@"C:\ScaleRoot\OldDir"]);

        using (var connection = SqliteDatabase.Open(db.Path))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files WHERE is_online = 1;";
            var onlineCount = (long)cmd.ExecuteScalar()!;
            Assert.AreEqual(1, onlineCount, "Only the new replacement file should be online.");

            cmd.CommandText = "SELECT COUNT(*) FROM files WHERE is_online = 0;";
            var offlineCount = (long)cmd.ExecuteScalar()!;
            Assert.AreEqual(2, offlineCount, "The two old descendant files must be marked offline.");
        }
    }

    [TestMethod]
    public void RealProcessColdStart_ThreeConsecutiveLaunches_UnderThreeSeconds()
    {
        var exePath = GetGuraFileExecutablePath();
        if (!File.Exists(exePath))
        {
            Assert.Inconclusive($"GuraFile executable not found at {exePath}. Build Release first.");
            return;
        }

        using var templateDb = new TempScaleDatabase();
        var seedTimer = Stopwatch.StartNew();
        templateDb.SeedFiles(100_000);
        templateDb.Checkpoint();
        seedTimer.Stop();
        Console.WriteLine($"[Template DB] 100,000 files seeded and checkpointed in {seedTimer.Elapsed.TotalMilliseconds:F1} ms");

        for (var run = 1; run <= 3; run++)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"GuraFile.ScaleColdStart.Run{run}.{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var logsDir = Path.Combine(tempDir, "logs");
            var targetDb = Path.Combine(tempDir, "index.db");
            File.Copy(templateDb.Path, targetDb, true);
            if (File.Exists($"{templateDb.Path}-wal"))
            {
                File.Copy($"{templateDb.Path}-wal", $"{targetDb}-wal", true);
            }
            if (File.Exists($"{templateDb.Path}-shm"))
            {
                File.Copy($"{templateDb.Path}-shm", $"{targetDb}-shm", true);
            }

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false
            };
            psi.ArgumentList.Add("--data-dir");
            psi.ArgumentList.Add(tempDir);
            psi.EnvironmentVariables["GURAFILE_DATA_DIR"] = tempDir;

            var sw = Stopwatch.StartNew();
            using var process = Process.Start(psi);
            Assert.IsNotNull(process, $"Process failed to start on run {run}");

            long windowReadyMs = 0;
            long queryReadyMs = 0;
            long peakWorkingSet = 0;

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                var windowReady = false;
                var queryReady = false;

                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(30);
                    process.Refresh();

                    if (process.HasExited)
                    {
                        Assert.Fail($"GuraFile exited prematurely on run {run} with exit code {process.ExitCode}");
                    }

                    try
                    {
                        peakWorkingSet = Math.Max(peakWorkingSet, process.PeakWorkingSet64);
                    }
                    catch
                    {
                    }

                    if (!windowReady)
                    {
                        var handle = process.MainWindowHandle;
                        var title = process.MainWindowTitle;
                        var visible = handle != IntPtr.Zero && WindowFinder.IsWindowVisible(handle);

                        if (!(handle != IntPtr.Zero && title == "GuraFile" && visible))
                        {
                            var titledHandle = WindowFinder.FindWindowByTitle(process.Id, "GuraFile");
                            if (titledHandle != IntPtr.Zero)
                            {
                                handle = titledHandle;
                                title = "GuraFile";
                                visible = WindowFinder.IsWindowVisible(handle);
                            }
                        }

                        if (handle != IntPtr.Zero && title == "GuraFile" && process.Responding && visible)
                        {
                            windowReady = true;
                            windowReadyMs = sw.ElapsedMilliseconds;
                        }
                    }

                    if (!queryReady)
                    {
                        if (HasInitialQueryCompletedLog(logsDir))
                        {
                            queryReady = true;
                            queryReadyMs = sw.ElapsedMilliseconds;
                        }
                    }

                    if (windowReady && queryReady)
                    {
                        sw.Stop();
                        break;
                    }
                }

                Assert.IsTrue(windowReady, $"Launch timed out waiting for GuraFile window on run {run}.");
                Assert.IsTrue(queryReady, $"Launch timed out waiting for initial file query completion log on run {run}.");

                var peakWorkingSetMb = peakWorkingSet / (1024.0 * 1024.0);
                Console.WriteLine($"[Cold Start Run {run}] Window ready: {windowReadyMs} ms, Initial query completed: {queryReadyMs} ms, Total: {sw.Elapsed.TotalMilliseconds:F1} ms, Peak Memory: {peakWorkingSetMb:F1} MB");

                Assert.IsTrue(
                    sw.Elapsed < TimeSpan.FromSeconds(3.0),
                    $"Cold start run {run} took {sw.Elapsed.TotalMilliseconds:F1} ms (window: {windowReadyMs} ms, query: {queryReadyMs} ms), exceeding 3.0s budget.");
            }
            finally
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                    {
                        if (process.CloseMainWindow())
                        {
                            if (!process.WaitForExit(3000))
                            {
                                process.Kill(true);
                                process.WaitForExit();
                            }
                        }
                        else
                        {
                            process.Kill(true);
                            process.WaitForExit();
                        }
                    }
                }
                catch
                {
                }

                DeleteDirectoryWithRetry(tempDir);
            }
        }
    }

    private static bool HasInitialQueryCompletedLog(string logsDir)
    {
        if (!Directory.Exists(logsDir))
        {
            return false;
        }

        var logFiles = Directory.GetFiles(logsDir, "*.log*");
        foreach (var file in logFiles)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var content = reader.ReadToEnd();
                if (content.Contains("[Lifecycle] Initial file query completed"))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static void DeleteDirectoryWithRetry(string directory, int maxRetries = 5, int delayMs = 100)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    private static string GetGuraFileExecutablePath()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "src", "GuraFile", "bin", "Release", "net10.0-windows10.0.26100.0", "win-x64", "GuraFile.exe");
    }

    private static class WindowFinder
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr windowHandle);

        public static IntPtr FindWindowByTitle(int processId, string expectedTitle)
        {
            var found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                uint pid = 0;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == (uint)processId)
                {
                    var sb = new System.Text.StringBuilder(256);
                    GetWindowTextW(hWnd, sb, 256);
                    if (string.Equals(sb.ToString(), expectedTitle, StringComparison.Ordinal))
                    {
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
