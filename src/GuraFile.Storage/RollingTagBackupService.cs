using System.Globalization;
using System.Text;

namespace GuraFile.Storage;

public enum BackupWriteStatus
{
    Created,
    Updated,
    Unchanged,
    Failed
}

public sealed record BackupResult(
    BackupWriteStatus Status,
    string? BackupPath,
    string? ErrorMessage = null)
{
    public bool Success => Status != BackupWriteStatus.Failed;

    public static BackupResult Created(string path) => new(BackupWriteStatus.Created, path);
    public static BackupResult Updated(string path) => new(BackupWriteStatus.Updated, path);
    public static BackupResult Unchanged(string path) => new(BackupWriteStatus.Unchanged, path);
    public static BackupResult Failed(string errorMessage, string? path = null) =>
        new(BackupWriteStatus.Failed, path, errorMessage);
}

public sealed record TagBackupInfo(
    string Path,
    string FileName,
    DateTimeOffset CreatedTime,
    DateTimeOffset LastModifiedTime,
    long ByteCount,
    bool IsValid,
    string? ValidationErrorMessage = null,
    DateTime? BackupDate = null)
{
    public string DisplayText =>
        IsValid
            ? $"{FileName} ({FormatBytes(ByteCount)}) - 有效"
            : $"{FileName} ({FormatBytes(ByteCount)}) - 损坏：{ValidationErrorMessage}";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

public sealed class RollingTagBackupService
{
    public const int DefaultRetentionLimit = 14;
    public const string BackupFileNamePrefix = "tags_backup_";
    public const string BackupFileNameExtension = ".json";

    private readonly object _syncLock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly UserTagBackupService _backupService;
    private readonly DiagnosticLogger _logger;

    public RollingTagBackupService(
        string databasePath,
        string? backupDirectory = null,
        int retentionLimit = DefaultRetentionLimit,
        Func<DateTimeOffset>? clock = null,
        DiagnosticLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        BackupDirectory = string.IsNullOrWhiteSpace(backupDirectory)
            ? AppPaths.DefaultTagBackupDirectory
            : Path.GetFullPath(backupDirectory);
        RetentionLimit = retentionLimit > 0 ? retentionLimit : DefaultRetentionLimit;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _logger = logger ?? DiagnosticLogger.Default;
        _backupService = new UserTagBackupService(DatabasePath);
    }

    internal RollingTagBackupService(
        UserTagBackupService backupService,
        string backupDirectory,
        int retentionLimit = DefaultRetentionLimit,
        Func<DateTimeOffset>? clock = null,
        DiagnosticLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(backupService);
        DatabasePath = backupService.DatabasePath;
        BackupDirectory = Path.GetFullPath(backupDirectory);
        RetentionLimit = retentionLimit > 0 ? retentionLimit : DefaultRetentionLimit;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _logger = logger ?? DiagnosticLogger.Default;
        _backupService = backupService;
    }

    public string DatabasePath { get; }

    public string BackupDirectory { get; }

    public int RetentionLimit { get; }

    public string? LastError { get; private set; }

    public BackupResult SafeTriggerBackup()
    {
        try
        {
            var result = TriggerBackup();
            if (!result.Success)
            {
                LastError = result.ErrorMessage;
            }

            return result;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            return BackupResult.Failed(exception.Message);
        }
    }

    public BackupResult TriggerBackup()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger.LogInfo(
            DiagnosticCategory.Backup,
            "TagBackupStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: "Triggering rolling tag backup.");

        lock (_syncLock)
        {
            try
            {
                if (!Directory.Exists(BackupDirectory))
                {
                    Directory.CreateDirectory(BackupDirectory);
                }
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                _logger.LogError(
                    DiagnosticCategory.Backup,
                    "TagBackupFailed",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: $"创建备份目录失败：{exception.Message}",
                    errorCode: "ERR_BACKUP_DIR",
                    exception: exception);
                return BackupResult.Failed($"创建备份目录失败：{exception.Message}");
            }

            string currentJson;
            try
            {
                currentJson = _backupService.Export();
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                _logger.LogError(
                    DiagnosticCategory.Backup,
                    "TagBackupFailed",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: $"导出用户标签失败：{exception.Message}",
                    errorCode: "ERR_BACKUP_EXPORT",
                    exception: exception);
                return BackupResult.Failed($"导出用户标签失败：{exception.Message}");
            }

            CleanStaleTempFiles();

            var now = _clock();
            var dateString = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var targetFileName = $"{BackupFileNamePrefix}{dateString}{BackupFileNameExtension}";
            var targetPath = Path.Combine(BackupDirectory, targetFileName);

            var fileExists = File.Exists(targetPath);
            if (fileExists)
            {
                try
                {
                    var existingJson = File.ReadAllText(targetPath, Encoding.UTF8);
                    if (string.Equals(existingJson, currentJson, StringComparison.Ordinal))
                    {
                        PruneOldBackups();
                        _logger.LogInfo(
                            DiagnosticCategory.Backup,
                            "TagBackupUnchanged",
                            correlationId: correlationId,
                            status: DiagnosticResultStatus.Skipped,
                            message: "Tag backup snapshot is identical to existing backup; skipped write.",
                            properties: new Dictionary<string, object?> { ["targetPath"] = targetPath });
                        return BackupResult.Unchanged(targetPath);
                    }
                }
                catch
                {
                    // If reading existing file fails, proceed to rewrite with fresh snapshot
                }
            }

            var tempFileName = $"{targetFileName}.{Guid.NewGuid():N}.tmp";
            var tempPath = Path.Combine(BackupDirectory, tempFileName);

            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    writer.Write(currentJson);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch (Exception exception)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                LastError = exception.Message;
                _logger.LogError(
                    DiagnosticCategory.Backup,
                    "TagBackupFailed",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: $"写入备份文件失败：{exception.Message}",
                    errorCode: "ERR_BACKUP_WRITE",
                    exception: exception,
                    properties: new Dictionary<string, object?> { ["targetPath"] = targetPath });
                return BackupResult.Failed($"写入备份文件失败：{exception.Message}", targetPath);
            }

            PruneOldBackups();
            var eventName = fileExists ? "TagBackupUpdated" : "TagBackupCreated";
            _logger.LogInfo(
                DiagnosticCategory.Backup,
                eventName,
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: $"Successfully {(fileExists ? "updated" : "created")} tag backup.",
                properties: new Dictionary<string, object?>
                {
                    ["targetPath"] = targetPath,
                    ["bytes"] = currentJson.Length
                });

            return fileExists ? BackupResult.Updated(targetPath) : BackupResult.Created(targetPath);
        }
    }

    public IReadOnlyList<TagBackupInfo> ListBackups()
    {
        lock (_syncLock)
        {
            if (!Directory.Exists(BackupDirectory))
            {
                _logger.LogInfo(
                    DiagnosticCategory.Backup,
                    "TagBackupListQueried",
                    status: DiagnosticResultStatus.Success,
                    message: "Queried rolling backups (directory does not exist).",
                    properties: new Dictionary<string, object?> { ["count"] = 0 });
                return [];
            }

            var results = new List<TagBackupInfo>();
            string[] filePaths;
            try
            {
                filePaths = Directory.GetFiles(BackupDirectory, $"{BackupFileNamePrefix}*{BackupFileNameExtension}");
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    DiagnosticCategory.Backup,
                    "TagBackupListQueried",
                    status: DiagnosticResultStatus.Failed,
                    message: $"Failed to list backups: {exception.Message}",
                    errorCode: "ERR_BACKUP_LIST",
                    exception: exception);
                return [];
            }

            foreach (var filePath in filePaths)
            {
                FileInfo fileInfo;
                try
                {
                    fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                DateTime? backupDate = null;
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileInfo.Name);
                if (nameWithoutExt.StartsWith(BackupFileNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var datePart = nameWithoutExt.Substring(BackupFileNamePrefix.Length);
                    if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    {
                        backupDate = parsedDate;
                    }
                }

                bool isValid;
                string? errorMessage = null;

                try
                {
                    if (fileInfo.Length > UserTagBackupService.MaximumBackupBytes)
                    {
                        isValid = false;
                        errorMessage = "备份文件过大。";
                    }
                    else if (fileInfo.Length == 0)
                    {
                        isValid = false;
                        errorMessage = "备份文件为空。";
                    }
                    else
                    {
                        var json = File.ReadAllText(filePath, Encoding.UTF8);
                        isValid = UserTagBackupService.TryValidate(json, out errorMessage);
                    }
                }
                catch (Exception exception)
                {
                    isValid = false;
                    errorMessage = $"读取或校验备份失败：{exception.Message}";
                }

                results.Add(new TagBackupInfo(
                    Path: fileInfo.FullName,
                    FileName: fileInfo.Name,
                    CreatedTime: fileInfo.CreationTimeUtc,
                    LastModifiedTime: fileInfo.LastWriteTimeUtc,
                    ByteCount: fileInfo.Length,
                    IsValid: isValid,
                    ValidationErrorMessage: errorMessage,
                    BackupDate: backupDate));
            }

            var sorted = results
                .OrderByDescending(b => b.BackupDate ?? b.LastModifiedTime.Date)
                .ThenByDescending(b => b.LastModifiedTime)
                .ToList();

            _logger.LogInfo(
                DiagnosticCategory.Backup,
                "TagBackupListQueried",
                status: DiagnosticResultStatus.Success,
                message: $"Queried rolling backups, found {sorted.Count} backups.",
                properties: new Dictionary<string, object?> { ["count"] = sorted.Count });

            return sorted;
        }
    }

    public TagImportResult RestoreBackup(string backupPath)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger.LogInfo(
            DiagnosticCategory.Backup,
            "TagRestoreStarted",
            correlationId: correlationId,
            status: DiagnosticResultStatus.Started,
            message: "Starting tag backup restore.",
            properties: new Dictionary<string, object?> { ["backupPath"] = backupPath });

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
            if (!File.Exists(backupPath))
            {
                var ex = new FileNotFoundException("备份文件不存在。", backupPath);
                _logger.LogError(
                    DiagnosticCategory.Backup,
                    "TagRestoreFailed",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: "Backup file does not exist.",
                    errorCode: "ERR_FILE_NOT_FOUND",
                    exception: ex);
                throw ex;
            }

            var fileInfo = new FileInfo(backupPath);
            if (fileInfo.Length > UserTagBackupService.MaximumBackupBytes)
            {
                var ex = new InvalidDataException("备份文件过大。");
                _logger.LogError(
                    DiagnosticCategory.Backup,
                    "TagRestoreFailed",
                    correlationId: correlationId,
                    status: DiagnosticResultStatus.Failed,
                    message: "Backup file exceeds maximum allowed size.",
                    errorCode: "ERR_BACKUP_TOO_LARGE",
                    exception: ex);
                throw ex;
            }

            var json = File.ReadAllText(backupPath, Encoding.UTF8);
            var result = _backupService.Import(json);

            _logger.LogInfo(
                DiagnosticCategory.Backup,
                "TagRestoreCompleted",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Success,
                message: "Tag backup restore completed successfully.",
                properties: new Dictionary<string, object?>
                {
                    ["backupPath"] = backupPath,
                    ["createdTags"] = result.CreatedTags,
                    ["reusedTags"] = result.ReusedTags,
                    ["restoredRelations"] = result.RestoredRelations,
                    ["conflicts"] = result.Conflicts.Count,
                    ["missingFiles"] = result.MissingFiles.Count
                });

            return result;
        }
        catch (Exception exception) when (exception is not FileNotFoundException && exception is not InvalidDataException)
        {
            _logger.LogError(
                DiagnosticCategory.Backup,
                "TagRestoreFailed",
                correlationId: correlationId,
                status: DiagnosticResultStatus.Failed,
                message: $"Failed to restore tag backup: {exception.Message}",
                errorCode: "ERR_BACKUP_RESTORE",
                exception: exception);
            throw;
        }
    }

    public TagImportResult? RestoreLatestValidBackup()
    {
        var latest = ListBackups().FirstOrDefault(b => b.IsValid);
        if (latest is null)
        {
            _logger.LogWarning(
                DiagnosticCategory.Backup,
                "TagRestoreSkipped",
                status: DiagnosticResultStatus.Skipped,
                message: "No valid backup available to restore.");
            return null;
        }

        return RestoreBackup(latest.Path);
    }

    private void PruneOldBackups()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory) || RetentionLimit <= 0)
            {
                return;
            }

            var files = Directory.GetFiles(BackupDirectory, $"{BackupFileNamePrefix}*{BackupFileNameExtension}")
                .Select(path => new FileInfo(path))
                .OrderByDescending(GetFileSortDate)
                .ThenByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            if (files.Count > RetentionLimit)
            {
                foreach (var file in files.Skip(RetentionLimit))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static DateTime GetFileSortDate(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        if (name.StartsWith(BackupFileNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var datePart = name.Substring(BackupFileNamePrefix.Length);
            if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }

        return file.LastWriteTimeUtc.Date;
    }

    private void CleanStaleTempFiles()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory))
            {
                return;
            }

            var tempFiles = Directory.GetFiles(BackupDirectory, "*.tmp");
            foreach (var temp in tempFiles)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
