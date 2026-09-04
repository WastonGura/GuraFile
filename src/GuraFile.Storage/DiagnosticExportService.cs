using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public sealed record DiagnosticExportResult(
    bool Succeeded,
    string DestinationZipPath,
    int LogFilesCount,
    long TotalZipBytes,
    string? ErrorMessage = null);

public sealed class DiagnosticExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _databasePath;
    private readonly string _logsDirectory;
    private readonly string _backupDirectory;
    private readonly Func<IReadOnlyList<ManagedRoot>>? _getRoots;

    public DiagnosticExportService(
        string? databasePath = null,
        string? logsDirectory = null,
        string? backupDirectory = null,
        Func<IReadOnlyList<ManagedRoot>>? getRoots = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? AppPaths.DefaultDatabasePath
            : Path.GetFullPath(databasePath);
        _logsDirectory = string.IsNullOrWhiteSpace(logsDirectory)
            ? AppPaths.DefaultLogsDirectory
            : Path.GetFullPath(logsDirectory);
        _backupDirectory = string.IsNullOrWhiteSpace(backupDirectory)
            ? AppPaths.DefaultTagBackupDirectory
            : Path.GetFullPath(backupDirectory);
        _getRoots = getRoots;
    }

    public Task<DiagnosticExportResult> ExportAsync(
        string destinationZipPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Export(destinationZipPath), cancellationToken);
    }

    public DiagnosticExportResult Export(string destinationZipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        try
        {
            var fullZipPath = Path.GetFullPath(destinationZipPath);
            var zipDir = Path.GetDirectoryName(fullZipPath);
            if (!string.IsNullOrEmpty(zipDir) && !Directory.Exists(zipDir))
            {
                Directory.CreateDirectory(zipDir);
            }

            if (File.Exists(fullZipPath))
            {
                File.Delete(fullZipPath);
            }

            int logFilesCount = 0;

            using (var zipStream = new FileStream(fullZipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // 1. environment.json
                AddWhitelistedEntry(archive, "environment.json", CreateEnvironmentJson());

                // 2. config_summary.json
                AddWhitelistedEntry(archive, "config_summary.json", CreateConfigSummaryJson());

                // 3. logs/*.log
                if (Directory.Exists(_logsDirectory))
                {
                    var logFiles = Directory.GetFiles(_logsDirectory, "*.log");
                    foreach (var logFile in logFiles)
                    {
                        var fileName = Path.GetFileName(logFile);
                        if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var entryName = $"logs/{fileName}";
                        var content = ReadAndSanitizeLogFile(logFile);
                        AddWhitelistedEntry(archive, entryName, content);
                        logFilesCount++;
                    }
                }
            }

            var fileInfo = new FileInfo(fullZipPath);
            return new DiagnosticExportResult(
                Succeeded: true,
                DestinationZipPath: fullZipPath,
                LogFilesCount: logFilesCount,
                TotalZipBytes: fileInfo.Length);
        }
        catch (Exception ex)
        {
            return new DiagnosticExportResult(
                Succeeded: false,
                DestinationZipPath: destinationZipPath,
                LogFilesCount: 0,
                TotalZipBytes: 0,
                ErrorMessage: ex.Message);
        }
    }

    private static void AddWhitelistedEntry(ZipArchive archive, string entryName, string content)
    {
        var normalizedName = entryName.Replace('\\', '/');

        // Blacklist assertions: strictly forbid db, wal, shm, bak, tag backup json, or user files
        var lower = normalizedName.ToLowerInvariant();
        if (lower.EndsWith(".db") ||
            lower.Contains("-wal") ||
            lower.Contains("-shm") ||
            lower.Contains(".bak") ||
            lower.Contains("tags_backup") ||
            lower.EndsWith(".docx") ||
            lower.EndsWith(".txt") && !lower.StartsWith("logs/"))
        {
            throw new InvalidOperationException($"Security policy violated: entry '{entryName}' is blacklisted.");
        }

        // Whitelist assertions: only environment.json, config_summary.json, and logs/*.log
        bool isAllowed = normalizedName is "environment.json" or "config_summary.json" ||
                         (normalizedName.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) &&
                          normalizedName.EndsWith(".log", StringComparison.OrdinalIgnoreCase));

        if (!isAllowed)
        {
            throw new InvalidOperationException($"Security policy violated: entry '{entryName}' is not whitelisted.");
        }

        var entry = archive.CreateEntry(normalizedName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        writer.Write(content);
    }

    private static string ReadAndSanitizeLogFile(string logFilePath)
    {
        try
        {
            var lines = File.ReadAllLines(logFilePath);
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                sb.AppendLine(DiagnosticLogger.SanitizeText(line));
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"[Log read error: {ex.Message}]";
        }
    }

    private string CreateEnvironmentJson()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "0.5.0";

        var envData = new Dictionary<string, object?>
        {
            ["appVersion"] = version,
            ["osVersion"] = Environment.OSVersion.ToString(),
            ["osDescription"] = RuntimeInformation.OSDescription,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["dotNetVersion"] = Environment.Version.ToString(),
            ["frameworkDescription"] = RuntimeInformation.FrameworkDescription,
            ["is64BitProcess"] = Environment.Is64BitProcess,
            ["is64BitOperatingSystem"] = Environment.Is64BitOperatingSystem,
            ["exportTimestampUtc"] = DateTime.UtcNow
        };

        return JsonSerializer.Serialize(envData, JsonOptions);
    }

    private string CreateConfigSummaryJson()
    {
        // 1. Managed roots
        IReadOnlyList<ManagedRoot> roots = [];
        if (_getRoots != null)
        {
            try
            {
                roots = _getRoots();
            }
            catch
            {
                // Ignore if getting roots fails
            }
        }
        else if (File.Exists(_databasePath))
        {
            roots = DatabaseRecoveryService.TryExtractRoots(_databasePath)
                .Select((p, idx) => new ManagedRoot(idx + 1, p))
                .ToList();
        }

        var sanitizedRoots = roots.Select(r => new Dictionary<string, object?>
        {
            ["path"] = DiagnosticLogger.SanitizePath(r.Path),
            ["status"] = r.Status.ToString(),
            ["lastCheckedUtc"] = r.LastCheckedUtc
        }).ToList();

        // 2. Database schema version and health
        int? schemaVersion = null;
        string dbHealthStatus = "Unknown";
        try
        {
            var health = new DatabaseHealthService().CheckHealth(_databasePath);
            dbHealthStatus = health.Status.ToString();
            schemaVersion = health.UserVersion;

            if (schemaVersion is null && File.Exists(_databasePath))
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version;";
                schemaVersion = Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
        catch
        {
            // Ignore failure to query db
        }

        // 3. Tag backup metadata
        int tagBackupCount = 0;
        DateTimeOffset? latestBackupTime = null;
        try
        {
            if (Directory.Exists(_backupDirectory))
            {
                var backupFiles = Directory.GetFiles(_backupDirectory, "tags_backup_*.json")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                tagBackupCount = backupFiles.Count;
                if (backupFiles.Count > 0)
                {
                    latestBackupTime = backupFiles[0].LastWriteTimeUtc;
                }
            }
        }
        catch
        {
            // Ignore failure to read backups directory
        }

        var summary = new Dictionary<string, object?>
        {
            ["managedRoots"] = sanitizedRoots,
            ["databaseSchemaVersion"] = schemaVersion,
            ["databaseHealthStatus"] = dbHealthStatus,
            ["tagBackupCount"] = tagBackupCount,
            ["latestTagBackupTimestampUtc"] = latestBackupTime
        };

        return JsonSerializer.Serialize(summary, JsonOptions);
    }
}
