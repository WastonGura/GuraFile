using System.Globalization;
using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class RollingTagBackupServiceTests
{
    [TestMethod]
    public void AppPathsDefaultsAreUnderLocalApplicationData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expectedBase = Path.Combine(localAppData, "GuraFile");

        Assert.AreEqual(expectedBase, AppPaths.DefaultUserDataDirectory);
        Assert.AreEqual(Path.Combine(expectedBase, "index.db"), AppPaths.DefaultDatabasePath);
        Assert.AreEqual(Path.Combine(expectedBase, "backups", "tags"), AppPaths.DefaultTagBackupDirectory);
    }

    [TestMethod]
    public void TagCrudAndBatchOperationsTriggerAutomaticBackup()
    {
        using var tempDir = new TempDirectory();
        using var db = TestDatabase.Create();
        db.SeedFile(1, @"C:\Root\file1.txt", "VOL-1", "FILE-1", "stable");
        db.SeedFile(2, @"C:\Root\file2.txt", "VOL-1", "FILE-2", "stable");

        var rollingBackup = new RollingTagBackupService(
            db.Path,
            tempDir.Path,
            clock: () => new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));
        var tags = new TagService(db.Path, rollingBackup);

        // 1. CreateTag triggers backup
        var projectTag = tags.CreateTag("Project");
        var backups = rollingBackup.ListBackups();
        Assert.HasCount(1, backups);
        Assert.IsTrue(backups[0].IsValid);
        Assert.AreEqual("tags_backup_2026-09-05.json", backups[0].FileName);
        var content = File.ReadAllText(backups[0].Path);
        StringAssert.Contains(content, "Project");

        // 2. RenameTag triggers backup
        tags.RenameTag(projectTag.Id, "Work");
        content = File.ReadAllText(backups[0].Path);
        StringAssert.Contains(content, "Work");
        Assert.IsFalse(content.Contains("\"Project\"", StringComparison.Ordinal));

        // 3. AddTagToFiles triggers backup
        tags.AddTagToFiles(projectTag.Id, [1, 2]);
        content = File.ReadAllText(backups[0].Path);
        StringAssert.Contains(content, "file1.txt");
        StringAssert.Contains(content, "file2.txt");

        // 4. RemoveTagFromFiles triggers backup
        tags.RemoveTagFromFiles(projectTag.Id, [2]);
        content = File.ReadAllText(backups[0].Path);
        StringAssert.Contains(content, "file1.txt");
        Assert.IsFalse(content.Contains("file2.txt", StringComparison.Ordinal));

        // 5. DeleteTag triggers backup
        tags.DeleteTag(projectTag.Id);
        content = File.ReadAllText(backups[0].Path);
        Assert.IsFalse(content.Contains("\"Work\"", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("file1.txt", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SameDayUnchangedSkipAndModifiedAtomicUpdate()
    {
        using var tempDir = new TempDirectory();
        using var db = TestDatabase.Create();
        db.SeedFile(1, @"C:\Root\doc.txt", "VOL-A", "FILE-A", "stable");

        var fixedClock = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var rollingBackup = new RollingTagBackupService(
            db.Path,
            tempDir.Path,
            clock: () => fixedClock);

        var tags = new TagService(db.Path, rollingBackup);
        tags.CreateTag("Initial");

        var backupFile = Path.Combine(tempDir.Path, "tags_backup_2026-09-05.json");
        Assert.IsTrue(File.Exists(backupFile));
        var initialWriteTime = File.GetLastWriteTimeUtc(backupFile);

        // Manually trigger backup with no changes -> should be Unchanged, file not touched
        var skipResult = rollingBackup.TriggerBackup();
        Assert.AreEqual(BackupWriteStatus.Unchanged, skipResult.Status);
        Assert.AreEqual(initialWriteTime, File.GetLastWriteTimeUtc(backupFile));

        // Modify tag -> should be Updated, file atomically updated
        tags.CreateTag("Second");
        var updateBackups = rollingBackup.ListBackups();
        Assert.HasCount(1, updateBackups);
        var updatedContent = File.ReadAllText(backupFile);
        StringAssert.Contains(updatedContent, "Initial");
        StringAssert.Contains(updatedContent, "Second");
    }

    [TestMethod]
    public void CrossDayCreatesNewDailyBackup()
    {
        using var tempDir = new TempDirectory();
        using var db = TestDatabase.Create();
        db.SeedFile(1, @"C:\Root\doc.txt", "VOL-A", "FILE-A", "stable");

        var currentClock = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var rollingBackup = new RollingTagBackupService(
            db.Path,
            tempDir.Path,
            clock: () => currentClock);

        var tags = new TagService(db.Path, rollingBackup);
        tags.CreateTag("Day1");

        Assert.IsTrue(File.Exists(Path.Combine(tempDir.Path, "tags_backup_2026-09-05.json")));

        // Advance to next day
        currentClock = currentClock.AddDays(1);
        tags.CreateTag("Day2");

        var backups = rollingBackup.ListBackups();
        Assert.HasCount(2, backups);
        Assert.IsTrue(File.Exists(Path.Combine(tempDir.Path, "tags_backup_2026-09-05.json")));
        Assert.IsTrue(File.Exists(Path.Combine(tempDir.Path, "tags_backup_2026-09-06.json")));
    }

    [TestMethod]
    public void RetentionPruningDeletesOldestBackups()
    {
        using var tempDir = new TempDirectory();
        using var db = TestDatabase.Create();
        db.SeedFile(1, @"C:\Root\doc.txt", "VOL-A", "FILE-A", "stable");

        var startDate = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var clockDate = startDate;

        // Retention limit is 3
        var rollingBackup = new RollingTagBackupService(
            db.Path,
            tempDir.Path,
            retentionLimit: 3,
            clock: () => clockDate);

        var tags = new TagService(db.Path, rollingBackup);

        for (int i = 0; i < 5; i++)
        {
            clockDate = startDate.AddDays(i);
            tags.CreateTag($"Tag-Day-{i + 1}");
        }

        var backups = rollingBackup.ListBackups();
        Assert.HasCount(3, backups);

        // Days 3, 4, 5 should be preserved, Days 1 and 2 pruned
        CollectionAssert.AreEquivalent(
            new[] { "tags_backup_2026-09-03.json", "tags_backup_2026-09-04.json", "tags_backup_2026-09-05.json" },
            backups.Select(b => b.FileName).ToArray());
    }

    [TestMethod]
    public void WriteFailureDoesNotBlockCoreTagOperationsOrCorruptExistingBackup()
    {
        using var tempDir = new TempDirectory();
        using var db = TestDatabase.Create();
        db.SeedFile(1, @"C:\Root\doc.txt", "VOL-A", "FILE-A", "stable");

        var rollingBackup = new RollingTagBackupService(
            db.Path,
            tempDir.Path,
            clock: () => new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero));

        var tags = new TagService(db.Path, rollingBackup);
        tags.CreateTag("ExistingTag");

        var backupFile = Path.Combine(tempDir.Path, "tags_backup_2026-09-05.json");
        Assert.IsTrue(File.Exists(backupFile));
        var validContent = File.ReadAllText(backupFile);

        // Now simulate a write failure: point rolling backup to an invalid read-only path (e.g., a file path instead of directory)
        var invalidDir = Path.Combine(tempDir.Path, "invalid_file_dir");
        File.WriteAllText(invalidDir, "blocking file");

        var brokenBackupService = new RollingTagBackupService(
            db.Path,
            backupDirectory: Path.Combine(invalidDir, "cannot_create_subdir"),
            clock: () => new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero));

        var resilientTags = new TagService(db.Path, brokenBackupService);

        // Core tag operation must NOT throw despite backup failure!
        var tag = resilientTags.CreateTag("MustSucceed");
        Assert.IsNotNull(tag);
        Assert.AreEqual("MustSucceed", tag.Name);

        // Verify SQLite committed successfully
        Assert.AreEqual(2L, db.Scalar("SELECT COUNT(*) FROM tags WHERE source = 'user';"));

        // Original backup remains intact and uncorrupted
        Assert.AreEqual(validContent, File.ReadAllText(backupFile));
    }

    [TestMethod]
    public void CorruptedBackupFilesAreSkippedInListAndValidBackupsCanBeRestored()
    {
        using var tempDir = new TempDirectory();
        using var sourceDb = TestDatabase.Create();
        sourceDb.SeedFile(1, @"C:\Root\doc.txt", "VOL-A", "FILE-A", "stable");
        var sourceTags = new TagService(sourceDb.Path);
        var t1 = sourceTags.CreateTag("ValidTag");
        sourceTags.AddTagToFiles(t1.Id, [1]);
        var validJson = new UserTagBackupService(sourceDb.Path).Export();

        // 1. Valid backup 2026-09-01
        File.WriteAllText(Path.Combine(tempDir.Path, "tags_backup_2026-09-01.json"), validJson);

        // 2. Corrupted backup (truncated json) 2026-09-02
        File.WriteAllText(Path.Combine(tempDir.Path, "tags_backup_2026-09-02.json"), "{\"format\": \"GuraFile.UserTags\", \"version\": 1, \"tags\": [");

        // 3. Corrupted backup (zero bytes) 2026-09-03
        File.WriteAllText(Path.Combine(tempDir.Path, "tags_backup_2026-09-03.json"), "");

        // 4. Valid backup 2026-09-04
        File.WriteAllText(Path.Combine(tempDir.Path, "tags_backup_2026-09-04.json"), validJson);

        var rollingBackup = new RollingTagBackupService(sourceDb.Path, tempDir.Path);
        var list = rollingBackup.ListBackups();

        Assert.HasCount(4, list);

        var validBackups = list.Where(b => b.IsValid).ToList();
        var corruptBackups = list.Where(b => !b.IsValid).ToList();

        Assert.HasCount(2, validBackups);
        Assert.HasCount(2, corruptBackups);
        Assert.IsTrue(corruptBackups.All(b => !string.IsNullOrWhiteSpace(b.ValidationErrorMessage)));

        // Restoring corrupted backup throws InvalidDataException
        Assert.ThrowsExactly<InvalidDataException>(() =>
            rollingBackup.RestoreBackup(Path.Combine(tempDir.Path, "tags_backup_2026-09-02.json")));

        // Restoring valid backup succeeds
        using var targetDb = TestDatabase.Create();
        targetDb.SeedFile(10, @"C:\Root\doc.txt", "VOL-A", "FILE-A", "stable");
        var targetRolling = new RollingTagBackupService(targetDb.Path, tempDir.Path);

        var result = targetRolling.RestoreBackup(Path.Combine(tempDir.Path, "tags_backup_2026-09-04.json"));
        Assert.AreEqual(1, result.CreatedTags);
        Assert.AreEqual(1, result.RestoredRelations);
        Assert.AreEqual(1L, targetDb.Scalar("SELECT COUNT(*) FROM tags WHERE source = 'user';"));
    }

    [TestMethod]
    public void RestoreUserTagsLeavesAutomaticTagsIntactAndReportsConflictsAndMissingFiles()
    {
        using var tempDir = new TempDirectory();
        using var sourceDb = TestDatabase.Create();
        sourceDb.SeedFile(1, @"C:\Root\matched.txt", "VOL-A", "FILE-A", "stable");
        sourceDb.SeedFile(2, @"C:\Root\missing.txt", "VOL-B", "FILE-B", "stable");

        var sourceTags = new TagService(sourceDb.Path);
        var tagWork = sourceTags.CreateTag("Work");
        sourceTags.AddTagToFiles(tagWork.Id, [1, 2]);

        var rollingBackup = new RollingTagBackupService(sourceDb.Path, tempDir.Path);
        var backupResult = rollingBackup.TriggerBackup();
        Assert.IsTrue(backupResult.Success);

        // Prepare target database
        using var targetDb = TestDatabase.Create();
        // File 1 is present
        targetDb.SeedFile(100, @"C:\Root\matched.txt", "VOL-A", "FILE-A", "stable");
        // Add automatic tag to file 1 in target
        targetDb.Execute("INSERT INTO tags (id, name, normalized_name, source) VALUES (88, 'DocType', 'DOCTYPE', 'automatic');");
        targetDb.Execute("INSERT INTO file_tags (file_id, tag_id, source) VALUES (100, 88, 'automatic');");

        // Add conflicting user tag with different casing in target ("work")
        var targetTags = new TagService(targetDb.Path);
        var existingTag = targetTags.CreateTag("work");

        var targetRolling = new RollingTagBackupService(targetDb.Path, tempDir.Path);
        var importResult = targetRolling.RestoreBackup(backupResult.BackupPath!);

        // CreatedTags = 0 (reused existing "work"), ReusedTags = 1, RestoredRelations = 1 (file 1)
        Assert.AreEqual(0, importResult.CreatedTags);
        Assert.AreEqual(1, importResult.ReusedTags);
        Assert.AreEqual(1, importResult.RestoredRelations);
        Assert.HasCount(1, importResult.Conflicts);
        Assert.AreEqual("Work", importResult.Conflicts[0].ImportedName);
        Assert.AreEqual("work", importResult.Conflicts[0].ExistingName);

        // Missing files has file 2
        Assert.HasCount(1, importResult.MissingFiles);
        Assert.AreEqual(@"C:\Root\missing.txt", importResult.MissingFiles[0].Path);

        // Automatic tag on file 1 is completely intact!
        Assert.AreEqual(1L, targetDb.Scalar("SELECT COUNT(*) FROM file_tags WHERE file_id = 100 AND tag_id = 88 AND source = 'automatic';"));
        Assert.AreEqual(1L, targetDb.Scalar("SELECT COUNT(*) FROM tags WHERE id = 88 AND source = 'automatic';"));

        // User tag relation restored on file 1
        Assert.AreEqual(1L, targetDb.Scalar("SELECT COUNT(*) FROM file_tags WHERE file_id = 100 AND tag_id = $id AND source = 'user';", ("$id", existingTag.Id)));
    }

    [TestMethod]
    public void RollingTagBackupServiceInitializesWhenDatabaseParentDirectoryDoesNotExist()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"GuraFile.NonExistent.{Guid.NewGuid():N}", "sub");
        var dbPath = Path.Combine(nonExistentDir, "index.db");
        var backupDir = Path.Combine(nonExistentDir, "backups", "tags");

        try
        {
            Assert.IsFalse(Directory.Exists(nonExistentDir));

            // Initializing RollingTagBackupService triggers UserTagBackupService -> SqliteDatabase.Open
            var service = new RollingTagBackupService(dbPath, backupDir);
            Assert.IsTrue(Directory.Exists(nonExistentDir));
            Assert.IsTrue(File.Exists(dbPath));

            var result = service.TriggerBackup();
            Assert.IsTrue(result.Success);
            Assert.IsTrue(Directory.Exists(backupDir));
            Assert.IsTrue(File.Exists(result.BackupPath!));
        }
        finally
        {
            var rootDir = Directory.GetParent(nonExistentDir)?.FullName;
            if (rootDir is not null && Directory.Exists(rootDir))
            {
                try
                {
                    Directory.Delete(rootDir, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.BackupTests.{Guid.NewGuid():N}");

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private bool _hasRoot;

        private TestDatabase(string path)
        {
            Path = path;
            using var _ = SqliteDatabase.Open(path);
        }

        public string Path { get; }

        public static TestDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Tests.{Guid.NewGuid():N}.db"));

        public void SeedFile(long id, string path, string volumeId, string fileId, string identityKind)
        {
            if (!_hasRoot)
            {
                Execute("INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');");
                _hasRoot = true;
            }

            Execute(
                """
                INSERT INTO files (
                    id, root_id, volume_id, file_id, path, normalized_path,
                    name, extension, size, modified_utc, identity_kind, is_online)
                VALUES ($id, 1, $volumeId, $fileId, $path, $path, $name, '.txt', 1, '2026-09-01T00:00:00Z', $identityKind, 1);
                """,
                ("$id", id),
                ("$volumeId", volumeId),
                ("$fileId", fileId),
                ("$path", path),
                ("$name", System.IO.Path.GetFileName(path)),
                ("$identityKind", identityKind));
        }

        public void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
        }

        public long Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (long)command.ExecuteScalar()!;
        }

        public void Dispose()
        {
            foreach (var path in new[] { Path, $"{Path}-shm", $"{Path}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
