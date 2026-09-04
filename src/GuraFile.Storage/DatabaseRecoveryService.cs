using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public sealed record DatabaseQuarantineResult(
    string? CorruptedDatabaseBackupPath,
    string? WalBackupPath,
    string? ShmBackupPath,
    bool Succeeded,
    string? ErrorMessage = null);

public sealed record DatabaseRecoveryReport(
    bool Succeeded,
    string? QuarantineBackupPath,
    string? RestoredBackupPath,
    int ScannedRoots,
    int DiscoveredFiles,
    int IndexedFiles,
    int RestoredTags,
    int RestoredRelations,
    int TagConflictsCount,
    IReadOnlyList<TagImportConflict> TagConflicts,
    IReadOnlyList<MissingBackupFile> UnmatchedFiles,
    IReadOnlyList<string> Failures,
    string? ErrorMessage = null);

public sealed class DatabaseRecoveryService
{
    public DatabaseQuarantineResult QuarantineCorruptedDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            return new DatabaseQuarantineResult(null, null, null, true, "数据库文件不存在，无需隔离。");
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            var fileName = Path.GetFileName(fullPath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            var backupFileName = $"{fileName}.corrupt_{timestamp}.bak";
            var backupPath = Path.Combine(directory, backupFileName);
            int counter = 1;
            while (File.Exists(backupPath))
            {
                backupPath = Path.Combine(directory, $"{fileName}.corrupt_{timestamp}_{counter++}.bak");
            }

            File.Move(fullPath, backupPath);

            var walPath = $"{fullPath}-wal";
            string? walBackupPath = null;
            if (File.Exists(walPath))
            {
                walBackupPath = $"{backupPath}-wal";
                File.Move(walPath, walBackupPath, overwrite: true);
            }

            var shmPath = $"{fullPath}-shm";
            string? shmBackupPath = null;
            if (File.Exists(shmPath))
            {
                shmBackupPath = $"{backupPath}-shm";
                File.Move(shmPath, shmBackupPath, overwrite: true);
            }

            return new DatabaseQuarantineResult(backupPath, walBackupPath, shmBackupPath, true);
        }
        catch (Exception ex)
        {
            return new DatabaseQuarantineResult(null, null, null, false, $"隔离数据库文件失败：{ex.Message}");
        }
    }

    public static IReadOnlyList<string> TryExtractRoots(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var roots = new List<string>();
        try
        {
            var fullPath = Path.GetFullPath(databasePath);
            if (!File.Exists(fullPath))
            {
                return roots;
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT path FROM roots ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                roots.Add(reader.GetString(0));
            }
        }
        catch
        {
            // Corrupted database may fail; return whatever was safely read
        }

        return roots;
    }

    public async Task<DatabaseRecoveryReport> RebuildIndexAndRestoreTagsAsync(
        string databasePath,
        IReadOnlyList<string> roots,
        string? tagBackupPath = null,
        string? tagBackupDirectory = null,
        Action<string>? statusCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(roots);

        var fullPath = Path.GetFullPath(databasePath);

        // 1. Safe quarantine: isolate corrupted database before anything else
        statusCallback?.Invoke("正在安全隔离损坏的数据库...");
        DatabaseQuarantineResult quarantineResult = QuarantineCorruptedDatabase(fullPath);
        if (!quarantineResult.Succeeded)
        {
            return new DatabaseRecoveryReport(
                Succeeded: false,
                QuarantineBackupPath: null,
                RestoredBackupPath: null,
                ScannedRoots: 0,
                DiscoveredFiles: 0,
                IndexedFiles: 0,
                RestoredTags: 0,
                RestoredRelations: 0,
                TagConflictsCount: 0,
                TagConflicts: [],
                UnmatchedFiles: [],
                Failures: [],
                ErrorMessage: quarantineResult.ErrorMessage);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 2. Initialize fresh blank database with current schema
            statusCallback?.Invoke("正在初始化全新数据库...");
            using (var initConn = SqliteDatabase.Open(fullPath))
            {
                // Blank database is created and migrated to CurrentVersion v5
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 3. Register managed roots and rescan disk files
            statusCallback?.Invoke("正在重新扫描管理根目录建立文件索引...");
            var scanner = new ManagedRootScanner(fullPath);
            int scannedRoots = 0;
            int totalDiscovered = 0;
            int totalCommitted = 0;
            var scanFailures = new List<string>();

            foreach (var rootPath in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var managedRoot = scanner.AddRoot(rootPath);
                    scannedRoots++;
                    var scanResult = await scanner.ScanAsync(managedRoot.Id, cancellationToken: cancellationToken);
                    totalDiscovered += scanResult.DiscoveredFiles;
                    totalCommitted += scanResult.CommittedFiles;

                    foreach (var failure in scanResult.Failures)
                    {
                        scanFailures.Add($"{failure.Path}: {failure.Error}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    scanFailures.Add($"{rootPath}: {ex.Message}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 4. Restore user tags from rolling tag backup
            statusCallback?.Invoke("正在从有效备份恢复用户标签...");
            string? resolvedBackupPath = tagBackupPath;
            if (string.IsNullOrWhiteSpace(resolvedBackupPath))
            {
                var rollingService = new RollingTagBackupService(fullPath, tagBackupDirectory);
                var latestValid = rollingService.ListBackups().FirstOrDefault(b => b.IsValid);
                resolvedBackupPath = latestValid?.Path;
            }

            int restoredTags = 0;
            int restoredRelations = 0;
            var tagConflicts = new List<TagImportConflict>();
            var unmatchedFiles = new List<MissingBackupFile>();

            if (!string.IsNullOrWhiteSpace(resolvedBackupPath) && File.Exists(resolvedBackupPath))
            {
                var json = File.ReadAllText(resolvedBackupPath, Encoding.UTF8);
                var backupService = new UserTagBackupService(fullPath);
                var importResult = backupService.Import(json);

                restoredTags = importResult.CreatedTags + importResult.ReusedTags;
                restoredRelations = importResult.RestoredRelations;
                tagConflicts.AddRange(importResult.Conflicts);
                unmatchedFiles.AddRange(importResult.MissingFiles);
            }

            return new DatabaseRecoveryReport(
                Succeeded: true,
                QuarantineBackupPath: quarantineResult.CorruptedDatabaseBackupPath,
                RestoredBackupPath: resolvedBackupPath,
                ScannedRoots: scannedRoots,
                DiscoveredFiles: totalDiscovered,
                IndexedFiles: totalCommitted,
                RestoredTags: restoredTags,
                RestoredRelations: restoredRelations,
                TagConflictsCount: tagConflicts.Count,
                TagConflicts: tagConflicts,
                UnmatchedFiles: unmatchedFiles,
                Failures: scanFailures);
        }
        catch (Exception ex)
        {
            return new DatabaseRecoveryReport(
                Succeeded: false,
                QuarantineBackupPath: quarantineResult.CorruptedDatabaseBackupPath,
                RestoredBackupPath: null,
                ScannedRoots: 0,
                DiscoveredFiles: 0,
                IndexedFiles: 0,
                RestoredTags: 0,
                RestoredRelations: 0,
                TagConflictsCount: 0,
                TagConflicts: [],
                UnmatchedFiles: [],
                Failures: [],
                ErrorMessage: ex.Message);
        }
    }
}
