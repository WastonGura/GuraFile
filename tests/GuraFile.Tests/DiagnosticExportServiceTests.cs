using System.IO.Compression;
using System.Text.Json;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class DiagnosticExportServiceTests
{
    private string _tempDir = null!;
    private string _dbPath = null!;
    private string _logsDir = null!;
    private string _backupDir = null!;
    private string _exportZipPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GuraFile_ExportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "index.db");
        _logsDir = Path.Combine(_tempDir, "logs");
        _backupDir = Path.Combine(_tempDir, "backups", "tags");
        _exportZipPath = Path.Combine(_tempDir, "Diagnostics_Test.zip");

        Directory.CreateDirectory(_logsDir);
        Directory.CreateDirectory(_backupDir);

        // Initialize a valid database
        using (var db = SqliteDatabase.Open(_dbPath))
        {
        }

        // Create sample log files
        File.WriteAllText(
            Path.Combine(_logsDir, "gurafile_2026-09-04.log"),
            "{\"timestamp\":\"2026-09-04T12:00:00Z\",\"level\":\"Info\",\"category\":\"App\",\"event\":\"AppStarted\"}\n");
        File.WriteAllText(
            Path.Combine(_logsDir, "gurafile_2026-09-05.log"),
            "{\"timestamp\":\"2026-09-05T08:00:00Z\",\"level\":\"Warn\",\"category\":\"Watcher\",\"event\":\"Reconciliation\"}\n");

        // Create tag backup file (this MUST NOT be included in export zip!)
        File.WriteAllText(
            Path.Combine(_backupDir, "tags_backup_20260905_120000.json"),
            "{\"format\":\"GuraFile.UserTags\",\"version\":1,\"tags\":[\"SecretTag\"]}");

        // Create fake user document file in temp dir (this MUST NOT be included in export zip!)
        File.WriteAllText(Path.Combine(_tempDir, "MyPersonalDocument.docx"), "Sensitive content");
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
            // Ignore cleanup failures
        }
    }

    [TestMethod]
    public void Export_CreatesValidZipWithStrictWhitelistedFiles()
    {
        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: _logsDir,
            backupDirectory: _backupDir,
            getRoots: () =>
            [
                new ManagedRoot(1, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"))
            ]);

        var result = service.Export(_exportZipPath);

        Assert.IsTrue(result.Succeeded, $"Export failed: {result.ErrorMessage}");
        Assert.IsTrue(File.Exists(_exportZipPath), "Exported zip file must exist");
        Assert.AreEqual(2, result.LogFilesCount);
        Assert.IsGreaterThan(0L, result.TotalZipBytes);

        using var archive = ZipFile.OpenRead(_exportZipPath);
        var entryNames = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

        // Must strictly contain environment.json and config_summary.json
        CollectionAssert.Contains(entryNames, "environment.json");
        CollectionAssert.Contains(entryNames, "config_summary.json");

        // Must contain logs/ entries
        Assert.IsTrue(entryNames.Any(e => e.StartsWith("logs/") && e.EndsWith(".log")));

        // Verify environment.json contents
        var envEntry = archive.GetEntry("environment.json");
        Assert.IsNotNull(envEntry);
        using (var reader = new StreamReader(envEntry.Open()))
        {
            var json = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.IsTrue(root.TryGetProperty("appVersion", out _));
            Assert.IsTrue(root.TryGetProperty("osVersion", out _));
            Assert.IsTrue(root.TryGetProperty("osDescription", out _));
            Assert.IsTrue(root.TryGetProperty("processArchitecture", out _));
            Assert.IsTrue(root.TryGetProperty("dotNetVersion", out _));
            Assert.IsTrue(root.TryGetProperty("is64BitProcess", out _));
        }

        // Verify config_summary.json contents and path sanitization
        var configEntry = archive.GetEntry("config_summary.json");
        Assert.IsNotNull(configEntry);
        using (var reader = new StreamReader(configEntry.Open()))
        {
            var json = reader.ReadToEnd();
            Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.IsTrue(root.TryGetProperty("databaseSchemaVersion", out var schemaVer));
            Assert.AreEqual(SqliteDatabase.CurrentVersion, schemaVer.GetInt32());
            Assert.IsTrue(root.TryGetProperty("tagBackupCount", out var backupCount));
            Assert.AreEqual(1, backupCount.GetInt32());
            Assert.IsTrue(root.TryGetProperty("managedRoots", out var rootsElement));
            Assert.AreEqual(1, rootsElement.GetArrayLength());
        }
    }

    [TestMethod]
    public void Export_StrictBlacklist_NeverContainsDatabaseBackupsOrUserFiles()
    {
        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: _logsDir,
            backupDirectory: _backupDir);

        var result = service.Export(_exportZipPath);
        Assert.IsTrue(result.Succeeded);

        using var archive = ZipFile.OpenRead(_exportZipPath);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.ToLowerInvariant().Replace('\\', '/');

            // Strictly assert NO .db, .wal, .shm, .bak
            Assert.DoesNotEndWith(name, ".db");
            Assert.DoesNotContain("-wal", name);
            Assert.DoesNotContain("-shm", name);
            Assert.DoesNotContain(".bak", name);

            // Strictly assert NO tag backup json
            Assert.DoesNotContain("tags_backup", name);

            // Strictly assert NO user files
            Assert.DoesNotEndWith(name, ".docx");
            Assert.DoesNotEndWith(name, ".txt");

            // Entry must be whitelisted
            bool isWhitelisted = name == "environment.json" ||
                                 name == "config_summary.json" ||
                                 (name.StartsWith("logs/") && name.EndsWith(".log"));
            Assert.IsTrue(isWhitelisted, $"Non-whitelisted entry found in diagnostics zip: {name}");
        }
    }

    [TestMethod]
    public void Export_OperatesCompletelyOffline_NoNetworkCalls()
    {
        // Pure local assembly without any HTTP or remote connections
        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: _logsDir,
            backupDirectory: _backupDir);

        var result = service.Export(_exportZipPath);
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void Export_WithoutAnonymization_PreservesOriginalPaths()
    {
        var rawUserPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: _logsDir,
            backupDirectory: _backupDir,
            getRoots: () =>
            [
                new ManagedRoot(1, rawUserPath)
            ],
            anonymizePaths: false);

        var result = service.Export(_exportZipPath);
        Assert.IsTrue(result.Succeeded, $"Export failed: {result.ErrorMessage}");

        using var archive = ZipFile.OpenRead(_exportZipPath);
        var configEntry = archive.GetEntry("config_summary.json");
        Assert.IsNotNull(configEntry);
        using var reader = new StreamReader(configEntry.Open());
        var json = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.IsTrue(root.TryGetProperty("managedRoots", out var roots));
        var pathInJson = roots[0].GetProperty("path").GetString();
        Assert.AreEqual(rawUserPath, pathInJson);
    }

    [TestMethod]
    public void ReadAndSanitizeLogFile_WhenFileThrowsException_SanitizesExceptionMessageIfAnonymizeEnabled()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nonexistentUserLog = Path.Combine(userProfile, "NonexistentSecretLogsDir", "gurafile_2026-09-08.log");

        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: _logsDir,
            backupDirectory: _backupDir,
            anonymizePaths: true);

        var content = service.ReadAndSanitizeLogFile(nonexistentUserLog);

        Assert.StartsWith("[Log read error:", content);
        Assert.DoesNotContain(Environment.UserName, content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<user>", content);
    }

    [TestMethod]
    public void Export_WhenLogContainsSecretTokenInMessage_ExportedLogRemainsValidJson()
    {
        var testLogsDir = Path.Combine(_tempDir, "token_logs");
        Directory.CreateDirectory(testLogsDir);

        var logger = new DiagnosticLogger(testLogsDir);
        logger.LogInfo(
            DiagnosticCategory.App,
            "ApiRequest",
            message: "token=synthetic_value");

        var testZip = Path.Combine(_tempDir, "token_export.zip");
        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: testLogsDir,
            backupDirectory: _backupDir,
            anonymizePaths: true);

        var result = service.Export(testZip);
        Assert.IsTrue(result.Succeeded, $"Export failed: {result.ErrorMessage}");

        using var archive = ZipFile.OpenRead(testZip);
        var logEntry = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("logs/") && e.FullName.EndsWith(".log"));
        Assert.IsNotNull(logEntry, "Exported zip should contain the log file");

        using var reader = new StreamReader(logEntry.Open());
        var content = reader.ReadToEnd();
        Assert.DoesNotContain("synthetic_value", content);
        Assert.Contains("token=***", content);

        // Every line must parse as valid JSON
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.AreEqual("Info", root.GetProperty("level").GetString());
            var message = root.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("token=***", message);
        }
    }

    [TestMethod]
    public void GetConfigSummaryNote_ReflectsAnonymizationSetting()
    {
        var anonymizedNote = DiagnosticExportService.GetConfigSummaryNote(true);
        var rawNote = DiagnosticExportService.GetConfigSummaryNote(false);

        Assert.AreEqual("• 配置摘要信息（config_summary.json）：脱敏的管理根目录、数据库架构版本、备份元数据；\n", anonymizedNote);
        Assert.AreEqual("• 配置摘要信息（config_summary.json）：管理根目录（未开启脱敏）、数据库架构版本、备份元数据；\n", rawNote);
    }

    [TestMethod]
    public void Export_WhenLogContainsEscapedQuotesOrPasswordSecret_ExportedLogRemainsValidJson()
    {
        var testLogsDir = Path.Combine(_tempDir, "escaped_quote_logs");
        Directory.CreateDirectory(testLogsDir);

        var logger = new DiagnosticLogger(testLogsDir);
        logger.LogInfo(
            DiagnosticCategory.App,
            "AuthEvent",
            message: "token=\"synthetic_secret\" and password: \"secret_123\"");

        var testZip = Path.Combine(_tempDir, "escaped_quote_export.zip");
        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: testLogsDir,
            backupDirectory: _backupDir,
            anonymizePaths: true);

        var result = service.Export(testZip);
        Assert.IsTrue(result.Succeeded, $"Export failed: {result.ErrorMessage}");

        using var archive = ZipFile.OpenRead(testZip);
        var logEntry = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("logs/") && e.FullName.EndsWith(".log"));
        Assert.IsNotNull(logEntry, "Exported zip should contain the log file");

        using var reader = new StreamReader(logEntry.Open());
        var content = reader.ReadToEnd();
        Assert.DoesNotContain("synthetic_secret", content);
        Assert.DoesNotContain("secret_123", content);

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.IsGreaterThan(0, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var message = root.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("token=\"***\"", message);
            Assert.Contains("password: \"***\"", message);
        }
    }

    [TestMethod]
    public void Export_WhenLogContainsTrailingUserPath_ExportedLogRemainsValidJson()
    {
        var testLogsDir = Path.Combine(_tempDir, "trailing_path_logs");
        Directory.CreateDirectory(testLogsDir);

        var logger = new DiagnosticLogger(testLogsDir);
        logger.LogInfo(
            DiagnosticCategory.App,
            "RootCheck",
            message: "Root C:/Users/SyntheticUser");

        var testZip = Path.Combine(_tempDir, "trailing_path_export.zip");
        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: testLogsDir,
            backupDirectory: _backupDir,
            anonymizePaths: true);

        var result = service.Export(testZip);
        Assert.IsTrue(result.Succeeded, $"Export failed: {result.ErrorMessage}");

        using var archive = ZipFile.OpenRead(testZip);
        var logEntry = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("logs/") && e.FullName.EndsWith(".log"));
        Assert.IsNotNull(logEntry, "Exported zip should contain the log file");

        using var reader = new StreamReader(logEntry.Open());
        var content = reader.ReadToEnd();
        Assert.DoesNotContain("SyntheticUser", content);

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.IsGreaterThan(0, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var message = root.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("<user>", message);
            Assert.AreEqual("Root C:/Users/<user>", message);
        }
    }

    [TestMethod]
    public async Task ExportAsync_EndToEndRealLoggerLogs_AllExportedLinesParseSuccessfully()
    {
        var testLogsDir = Path.Combine(_tempDir, "e2e_logs");
        Directory.CreateDirectory(testLogsDir);

        var logger = new DiagnosticLogger(testLogsDir);
        logger.LogInfo(DiagnosticCategory.App, "Startup", message: "token=\"synthetic_secret\"");
        logger.LogWarning(DiagnosticCategory.Scanner, "ScanWarning", message: "Encountered password: \"secret_123\" in config");
        logger.LogError(DiagnosticCategory.Database, "DbError", message: "Failed accessing Root C:/Users/SyntheticUser", errorCode: "ERR_PATH_USER");
        logger.LogInfo(DiagnosticCategory.Watcher, "WatcherOnline", properties: new Dictionary<string, object?>
        {
            ["path"] = @"C:\Users\SyntheticUser\Documents\SecretFolder",
            ["secretKey"] = "apikey: \"secret_key_val\""
        });

        var testZip = Path.Combine(_tempDir, "e2e_export.zip");
        var service = new DiagnosticExportService(
            databasePath: _dbPath,
            logsDirectory: testLogsDir,
            backupDirectory: _backupDir,
            anonymizePaths: true);

        var result = await service.ExportAsync(testZip);
        Assert.IsTrue(result.Succeeded, $"Export failed: {result.ErrorMessage}");

        using var archive = ZipFile.OpenRead(testZip);
        var logEntries = archive.Entries.Where(e => e.FullName.StartsWith("logs/") && e.FullName.EndsWith(".log")).ToList();
        Assert.IsGreaterThan(0, logEntries.Count);

        int totalParsedLines = 0;
        foreach (var entry in logEntries)
        {
            using var reader = new StreamReader(entry.Open());
            var content = await reader.ReadToEndAsync();
            var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert.AreNotEqual(JsonValueKind.Undefined, doc.RootElement.ValueKind);
                totalParsedLines++;
            }
        }

        Assert.AreEqual(4, totalParsedLines);
    }
}
