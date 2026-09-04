using System.Text.Json;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class DiagnosticLoggerTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GuraFile_DiagTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in temp dir
        }
    }

    [TestMethod]
    public void AppPaths_DefinesDefaultLogsDirectory()
    {
        var logsDir = AppPaths.DefaultLogsDirectory;
        Assert.IsFalse(string.IsNullOrWhiteSpace(logsDir));
        Assert.EndsWith("logs", logsDir, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void DiagnosticLogger_SanitizePath_RedactsUserProfileAndUsername()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var originalPath = Path.Combine(userProfile, "Documents", "secret_project", "file.txt");

        var sanitized = DiagnosticLogger.SanitizePath(originalPath);
        Assert.DoesNotContain(Environment.UserName, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(sanitized.Contains(@"\Users\<user>\", StringComparison.OrdinalIgnoreCase) ||
                      sanitized.Contains(@"/Users/<user>/", StringComparison.OrdinalIgnoreCase),
            $"Sanitized path should contain <user>: {sanitized}");
    }

    [TestMethod]
    public void DiagnosticLogger_SanitizeText_RedactsTokensAndSecrets()
    {
        var sensitiveText = "Error communicating with token=abc123secret and bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        var sanitized = DiagnosticLogger.SanitizeText(sensitiveText);

        Assert.DoesNotContain("abc123secret", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void DiagnosticLogger_WritesStructuredLogEntryWithCorrelationIdAndErrorCode()
    {
        var logger = new DiagnosticLogger(_tempDir);
        var correlationId = "batch-op-12345";

        logger.Log(
            DiagnosticLogLevel.Error,
            DiagnosticCategory.FileOperation,
            "FileMoveFailed",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Failed,
            message: $@"Failed to move {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\secret.txt",
            errorCode: "ERR_FILE_LOCKED",
            exception: new IOException("The process cannot access the file because it is being used by another process."));

        var logFiles = Directory.GetFiles(_tempDir, "gurafile_*.log");
        Assert.HasCount(1, logFiles);

        var lines = File.ReadAllLines(logFiles[0]);
        Assert.HasCount(1, lines);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;

        Assert.AreEqual("Error", root.GetProperty("level").GetString());
        Assert.AreEqual("FileOperation", root.GetProperty("category").GetString());
        Assert.AreEqual("FileMoveFailed", root.GetProperty("event").GetString());
        Assert.AreEqual(correlationId, root.GetProperty("correlationId").GetString());
        Assert.AreEqual("Failed", root.GetProperty("status").GetString());
        Assert.AreEqual("ERR_FILE_LOCKED", root.GetProperty("errorCode").GetString());

        var message = root.GetProperty("message").GetString()!;
        Assert.DoesNotContain(Environment.UserName, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<user>", message);

        var exceptionStr = root.GetProperty("exception").GetString()!;
        Assert.Contains("cannot access the file", exceptionStr);
    }

    [TestMethod]
    public void DiagnosticLogger_CorrelationId_ChainsBusinessWorkflow()
    {
        var logger = new DiagnosticLogger(_tempDir);
        var correlationId = "scan-root-42";

        logger.LogInfo(DiagnosticCategory.Scanner, "ScanStarted", correlationId: correlationId, status: DiagnosticResultStatus.Started, message: "Started scanning root");
        logger.LogInfo(DiagnosticCategory.Scanner, "ScanCompleted", correlationId: correlationId, status: DiagnosticResultStatus.Success, message: "Completed scanning root (100 files)");

        var logFiles = Directory.GetFiles(_tempDir, "gurafile_*.log");
        Assert.HasCount(1, logFiles);

        var lines = File.ReadAllLines(logFiles[0]);
        Assert.HasCount(2, lines);

        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.AreEqual(correlationId, doc.RootElement.GetProperty("correlationId").GetString());
        }
    }

    [TestMethod]
    public void DiagnosticLogger_ConcurrentWrites_AreThreadSafe()
    {
        var logger = new DiagnosticLogger(_tempDir);
        const int taskCount = 10;
        const int iterationsPerTask = 50;

        Parallel.For(0, taskCount, t =>
        {
            for (int i = 0; i < iterationsPerTask; i++)
            {
                logger.LogInfo(
                    DiagnosticCategory.Watcher,
                    "ReconciliationItem",
                    correlationId: $"task-{t}",
                    message: $"Message {i} from thread {Environment.CurrentManagedThreadId}");
            }
        });

        var logFiles = Directory.GetFiles(_tempDir, "gurafile_*.log");
        Assert.HasCount(1, logFiles);

        var lines = File.ReadAllLines(logFiles[0]);
        Assert.HasCount(taskCount * iterationsPerTask, lines);

        // Every line must be valid JSON
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.AreEqual("Watcher", doc.RootElement.GetProperty("category").GetString());
        }
    }

    [TestMethod]
    public void DiagnosticLogger_FaultIsolation_SwallowsDiskAndPermissionFailuresSilently()
    {
        // Point logger to an invalid file path as directory, or an uncreatable location
        var invalidDir = Path.Combine(_tempDir, "file_as_dir.txt");
        File.WriteAllText(invalidDir, "blocker");

        // logger should not throw when constructed or when logging to a path blocked by a file
        var logger = new DiagnosticLogger(invalidDir);

        // All calls must succeed without throwing any exception
        logger.LogInfo(DiagnosticCategory.App, "Startup", message: "Starting app");
        logger.LogError(DiagnosticCategory.Database, "DbError", errorCode: "CORRUPT", message: "Db corrupted");
        logger.LogWarning(DiagnosticCategory.Scanner, "ScanSlow", message: "Scan took too long");
    }

    [TestMethod]
    public void DiagnosticLogger_RollingAndRetention_RotatesBySizeAndPurgesOldFiles()
    {
        // Configure logger with very small max file size (300 bytes) and max file count of 3
        var logger = new DiagnosticLogger(
            _tempDir,
            maxFileSizeBytes: 300,
            maxFileCount: 3,
            retentionDays: 14);

        for (int i = 0; i < 20; i++)
        {
            logger.LogInfo(
                DiagnosticCategory.Scanner,
                "ScanBatch",
                message: $"Batch {i}: lots of descriptive text to easily exceed the 300 bytes threshold per file.");
        }

        var logFiles = Directory.GetFiles(_tempDir, "gurafile_*.log");

        // Must have rotated and strictly capped total files to maxFileCount (3)
        Assert.IsLessThanOrEqualTo(3, logFiles.Length);
        Assert.IsGreaterThan(1, logFiles.Length);
    }

    [TestMethod]
    public void DiagnosticLogger_AntiFlooding_SuppressesExcessiveRepetitiveEvents()
    {
        // By default, flood protection restricts high frequency events of the same Category+EventName
        var logger = new DiagnosticLogger(
            _tempDir,
            enableFloodProtection: true,
            floodThresholdPerSecond: 10);

        // Simulate high-frequency single file scanning (e.g. 1000 events in tight loop)
        for (int i = 0; i < 1000; i++)
        {
            logger.LogInfo(
                DiagnosticCategory.Scanner,
                "FileScanned",
                message: $"Discovered item_{i}.txt");
        }

        var logFiles = Directory.GetFiles(_tempDir, "gurafile_*.log");
        Assert.HasCount(1, logFiles);

        var lines = File.ReadAllLines(logFiles[0]);

        // Lines count should be bounded (significantly less than 1000, around ~10-12 due to throttling)
        Assert.IsLessThanOrEqualTo(25, lines.Length);
    }
}
