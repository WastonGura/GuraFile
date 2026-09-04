using System.Text;
using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class DatabaseRecoveryServiceTests
{
    private readonly DatabaseRecoveryService _recoveryService = new();

    [TestMethod]
    public void QuarantineCorruptedDatabaseMovesFilesAndPreservesExactContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"GuraFile.QuarantineTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "index.db");
        var walPath = $"{dbPath}-wal";
        var shmPath = $"{dbPath}-shm";

        try
        {
            var dbBytes = Encoding.UTF8.GetBytes("CORRUPTED_DB_BYTES_MOCK_CONTENT");
            var walBytes = Encoding.UTF8.GetBytes("CORRUPTED_WAL_BYTES");
            var shmBytes = Encoding.UTF8.GetBytes("CORRUPTED_SHM_BYTES");

            File.WriteAllBytes(dbPath, dbBytes);
            File.WriteAllBytes(walPath, walBytes);
            File.WriteAllBytes(shmPath, shmBytes);

            var result = _recoveryService.QuarantineCorruptedDatabase(dbPath);

            Assert.IsTrue(result.Succeeded);
            Assert.IsNotNull(result.CorruptedDatabaseBackupPath);
            Assert.IsNotNull(result.WalBackupPath);
            Assert.IsNotNull(result.ShmBackupPath);

            // Assert original files no longer exist
            Assert.IsFalse(File.Exists(dbPath), "Original db file should be moved");
            Assert.IsFalse(File.Exists(walPath), "Original wal file should be moved");
            Assert.IsFalse(File.Exists(shmPath), "Original shm file should be moved");

            // Assert backup files exist and match exact bytes
            Assert.IsTrue(File.Exists(result.CorruptedDatabaseBackupPath));
            CollectionAssert.AreEqual(dbBytes, File.ReadAllBytes(result.CorruptedDatabaseBackupPath));

            Assert.IsTrue(File.Exists(result.WalBackupPath));
            CollectionAssert.AreEqual(walBytes, File.ReadAllBytes(result.WalBackupPath));

            Assert.IsTrue(File.Exists(result.ShmBackupPath));
            CollectionAssert.AreEqual(shmBytes, File.ReadAllBytes(result.ShmBackupPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    [TestMethod]
    public async Task RebuildIndexAndRestoreTagsFullyRestoresIndexAndTagsFromBackup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"GuraFile.RebuildTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var rootDir = Path.Combine(tempDir, "ManagedRoot");
        Directory.CreateDirectory(rootDir);
        var backupDir = Path.Combine(tempDir, "Backups");
        Directory.CreateDirectory(backupDir);
        var dbPath = Path.Combine(tempDir, "index.db");

        // Create some sample user files in rootDir
        var fileA = Path.Combine(rootDir, "fileA.txt");
        var fileB = Path.Combine(rootDir, "fileB.txt");
        var deletedFile = Path.Combine(rootDir, "deletedFile.txt");

        File.WriteAllText(fileA, "Hello World A");
        File.WriteAllText(fileB, "Hello World B");
        File.WriteAllText(deletedFile, "To be deleted before rebuild");

        try
        {
            // 1. Initialize database and add root, scan files, assign tags
            var scanner = new ManagedRootScanner(dbPath);
            var root = scanner.AddRoot(rootDir);
            var scanResult = await scanner.ScanAsync(root.Id);
            Assert.AreEqual(3, scanResult.CommittedFiles);

            var rollingBackup = new RollingTagBackupService(dbPath, backupDir);
            var tagService = new TagService(dbPath, rollingBackup);

            var tagImportant = tagService.CreateTag("重要");
            var tagProject = tagService.CreateTag("项目");

            // Find file ids
            long idA = 0, idB = 0, idDel = 0;
            using (var conn = SqliteDatabase.Open(dbPath))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, path FROM files;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var path = reader.GetString(1);
                    if (path.EndsWith("fileA.txt", StringComparison.OrdinalIgnoreCase)) idA = id;
                    else if (path.EndsWith("fileB.txt", StringComparison.OrdinalIgnoreCase)) idB = id;
                    else if (path.EndsWith("deletedFile.txt", StringComparison.OrdinalIgnoreCase)) idDel = id;
                }
            }

            Assert.IsTrue(idA > 0 && idB > 0 && idDel > 0);

            tagService.AddTagToFiles(tagImportant.Id, [idA, idDel]);
            tagService.AddTagToFiles(tagProject.Id, [idB]);

            // 2. Trigger rolling tag backup
            var backupResult = rollingBackup.TriggerBackup();
            Assert.IsTrue(backupResult.Success);
            Assert.IsNotNull(backupResult.BackupPath);

            // Delete deletedFile.txt from disk so it becomes an unmatched backup entry during recovery
            File.Delete(deletedFile);

            // Add a new file fileC.txt to disk that wasn't in the tag backup
            var fileC = Path.Combine(rootDir, "fileC.txt");
            File.WriteAllText(fileC, "Brand new file C");

            // 3. Artificially corrupt the database
            var corruptData = Encoding.UTF8.GetBytes("DEFINITELY_NOT_A_VALID_SQLITE_DATABASE_HEADER");
            File.WriteAllBytes(dbPath, corruptData);

            var healthService = new DatabaseHealthService();
            var health = healthService.CheckHealth(dbPath);
            Assert.AreEqual(DatabaseHealthStatus.Corrupted, health.Status);

            // 4. Perform full rebuild and restore
            var report = await _recoveryService.RebuildIndexAndRestoreTagsAsync(
                dbPath,
                [rootDir],
                tagBackupPath: backupResult.BackupPath,
                tagBackupDirectory: backupDir);

            // 5. Assert report & restored state
            Assert.IsTrue(report.Succeeded);
            Assert.AreEqual(1, report.ScannedRoots);
            Assert.AreEqual(3, report.DiscoveredFiles); // fileA, fileB, fileC
            Assert.AreEqual(3, report.IndexedFiles);
            Assert.AreEqual(2, report.RestoredTags); // 重要, 项目
            Assert.AreEqual(2, report.RestoredRelations); // fileA -> 重要, fileB -> 项目
            Assert.AreEqual(0, report.TagConflictsCount);
            Assert.HasCount(1, report.UnmatchedFiles); // deletedFile.txt
            StringAssert.Contains(report.UnmatchedFiles[0].Path, "deletedFile.txt");

            // Assert quarantine backup exists and has the corrupted data
            Assert.IsNotNull(report.QuarantineBackupPath);
            Assert.IsTrue(File.Exists(report.QuarantineBackupPath));
            CollectionAssert.AreEqual(corruptData, File.ReadAllBytes(report.QuarantineBackupPath));

            // Assert new database is healthy and has current schema
            var newHealth = healthService.CheckHealth(dbPath);
            Assert.AreEqual(DatabaseHealthStatus.Healthy, newHealth.Status);
            Assert.AreEqual(SqliteDatabase.CurrentVersion, newHealth.UserVersion);

            // Assert real user files on disk were untouched
            Assert.AreEqual("Hello World A", File.ReadAllText(fileA));
            Assert.AreEqual("Hello World B", File.ReadAllText(fileB));
            Assert.AreEqual("Brand new file C", File.ReadAllText(fileC));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    [TestMethod]
    public async Task RebuildFailurePreservesQuarantinedDatabaseAndTagBackups()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"GuraFile.ResilienceTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var backupDir = Path.Combine(tempDir, "Backups");
        Directory.CreateDirectory(backupDir);
        var dbPath = Path.Combine(tempDir, "index.db");

        // Write initial corrupted database and mock backup file
        var corruptBytes = Encoding.UTF8.GetBytes("CORRUPT_BYTES_FOR_RESILIENCE_TEST");
        File.WriteAllBytes(dbPath, corruptBytes);

        var validBackupJson = """
        {
          "format": "GuraFile.UserTags",
          "version": 1,
          "tags": [ { "name": "重要" } ],
          "files": []
        }
        """;
        var backupFile = Path.Combine(backupDir, "tags_backup_2026-09-05.json");
        File.WriteAllText(backupFile, validBackupJson, Encoding.UTF8);

        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-canceled to inject immediate cancellation failure

            DatabaseRecoveryReport? report = null;
            Exception? caughtException = null;
            try
            {
                report = await _recoveryService.RebuildIndexAndRestoreTagsAsync(
                    dbPath,
                    ["C:\\Injected\\Invalid\\Path"],
                    tagBackupPath: backupFile,
                    cancellationToken: cts.Token);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // Either report returned Succeeded: false or exception was thrown
            if (report != null)
            {
                Assert.IsFalse(report.Succeeded);
            }
            else
            {
                Assert.IsNotNull(caughtException);
            }

            // In both cases, quarantined corrupt database MUST exist and contain exact corrupt bytes
            var backupFiles = Directory.GetFiles(tempDir, "index.db.corrupt_*.bak");
            Assert.HasCount(1, backupFiles, "Quarantined corrupt database backup must exist");
            CollectionAssert.AreEqual(corruptBytes, File.ReadAllBytes(backupFiles[0]));

            // Tag backup file must remain untouched
            Assert.IsTrue(File.Exists(backupFile));
            Assert.AreEqual(validBackupJson, File.ReadAllText(backupFile, Encoding.UTF8));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
