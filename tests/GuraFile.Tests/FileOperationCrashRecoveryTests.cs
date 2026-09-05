using System.Runtime.Versioning;
using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class FileOperationCrashRecoveryTests
{
    [TestMethod]
    public async Task ShellCall_TerminatedBeforeExecution_ReconcilesWithoutWriting_KeepsOriginalFileAndTags()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("source.txt", "source content");
        var destDir = env.CreateDirectory("Moved");
        var expectedTarget = Path.Combine(destDir, "source.txt");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, initialFiles);
        var sourceFileId = initialFiles[0].Id;

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("Important");
        tagService.AddTagToFiles(tag.Id, [sourceFileId]);

        // Simulate crash before Shell call: insert pending intent for move
        var committer = new FileOperationIndexCommitter(env.Scanner);
        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-crash-before-shell",
                operationType: "move",
                collisionPolicy: "auto_rename",
                items: [(sourcePath, destDir, "source.txt", expectedTarget)]);
        }

        // Verify target does NOT exist on disk
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.IsFalse(File.Exists(expectedTarget));

        // Act: Run crash recovery
        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report = await recoveryService.RecoverAsync();

        Assert.AreEqual(1, report.RecoveredIntentsCount);
        Assert.AreEqual(0, report.IndeterminateIntentsCount);
        Assert.IsFalse(report.HasIndeterminateOperations);

        // Assert: Target was NEVER written (Shell never replayed)
        Assert.IsFalse(File.Exists(expectedTarget));
        Assert.IsTrue(File.Exists(sourcePath));

        // In DB, source file is online and tags intact
        var currentFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, currentFiles);
        Assert.AreEqual(sourcePath, currentFiles[0].Path);
        Assert.IsTrue(currentFiles[0].IsOnline);

        var tags = tagService.ListTagsForFile(currentFiles[0].Id);
        Assert.HasCount(1, tags);
        Assert.AreEqual("Important", tags[0].Name);

        // Intent should be marked committed with failed item
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            var status = committer.GetIntentStatus(connection, intentId);
            Assert.AreEqual("committed", status);
        }
    }

    [TestMethod]
    public async Task ShellCall_CompletedBeforeCrash_MoveRename_ReconcilesIndex_PreservesIdentityAndTags_NoShellReplay()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("old_name.txt", "content for move");
        var destDir = env.CreateDirectory("NewDir");
        var targetPath = Path.Combine(destDir, "new_name.txt");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, initialFiles);
        var originalFileId = initialFiles[0].Id;
        var originalIdentity = FileIdentityReader.Read(sourcePath);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("Project Alpha");
        tagService.AddTagToFiles(tag.Id, [originalFileId]);

        // Simulate Shell completed: physically move file on disk
        File.Move(sourcePath, targetPath);
        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(targetPath));

        // In DB: insert intent with status = shell_completed
        var committer = new FileOperationIndexCommitter(env.Scanner);
        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-move-shell-completed",
                operationType: "move",
                collisionPolicy: "auto_rename",
                items: [(sourcePath, destDir, "new_name.txt", targetPath)]);

            committer.UpdateIntentShellCompleted(
                connection,
                intentId,
                [(sourcePath, targetPath, "completed", null)]);
        }

        // Act: Run crash recovery
        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report = await recoveryService.RecoverAsync();

        Assert.AreEqual(1, report.RecoveredIntentsCount);
        Assert.AreEqual(0, report.IndeterminateIntentsCount);

        // Assert: Index reconciled to targetPath
        var currentFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, currentFiles);
        Assert.AreEqual(targetPath, currentFiles[0].Path);
        Assert.IsTrue(currentFiles[0].IsOnline);

        // Stable identity preserved if supported
        if (originalIdentity.IsStable)
        {
            using var conn = SqliteDatabase.Open(env.DatabasePath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT volume_id, file_id FROM files WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", currentFiles[0].Id);
            using var reader = cmd.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(originalIdentity.VolumeId, reader.GetString(0));
            Assert.AreEqual(originalIdentity.FileId, reader.GetString(1));
        }

        // Tags preserved
        var tags = tagService.ListTagsForFile(currentFiles[0].Id);
        Assert.HasCount(1, tags);
        Assert.AreEqual("Project Alpha", tags[0].Name);

        // Intent marked committed
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            Assert.AreEqual("committed", committer.GetIntentStatus(connection, intentId));
        }
    }

    [TestMethod]
    public async Task Copy_CrashDuringBatch_IndexesExistingTargetWithTags_DoesNotReplayUncopiedFiles()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("file1.txt", "content 1");
        var file2 = env.CreateFile("file2.txt", "content 2");
        var destDir = env.CreateDirectory("Copies");
        var target1 = Path.Combine(destDir, "file1.txt");
        var target2 = Path.Combine(destDir, "file2.txt");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var allInitial = await queryService.QueryAsync(new());
        var id1 = allInitial.Single(f => f.Path == file1).Id;
        var id2 = allInitial.Single(f => f.Path == file2).Id;

        var tagService = new TagService(env.DatabasePath);
        var tag1 = tagService.CreateTag("Tag1");
        var tag2 = tagService.CreateTag("Tag2");
        tagService.AddTagToFiles(tag1.Id, [id1]);
        tagService.AddTagToFiles(tag2.Id, [id2]);

        // Simulate crash during copy: file1 copied on disk, file2 NOT copied
        File.Copy(file1, target1);
        Assert.IsTrue(File.Exists(target1));
        Assert.IsFalse(File.Exists(target2));

        var committer = new FileOperationIndexCommitter(env.Scanner);
        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-copy-partial",
                operationType: "copy",
                collisionPolicy: "auto_rename",
                items: [
                    (file1, destDir, "file1.txt", target1),
                    (file2, destDir, "file2.txt", target2)
                ]);
        }

        // Act: Run crash recovery
        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report = await recoveryService.RecoverAsync();

        Assert.AreEqual(1, report.RecoveredIntentsCount);
        Assert.AreEqual(0, report.IndeterminateIntentsCount);

        // Assert: target2 was NOT written to disk
        Assert.IsFalse(File.Exists(target2));

        // target1 was indexed and inherited tag1
        var allCurrent = await queryService.QueryAsync(new());
        var indexedTarget1 = allCurrent.FirstOrDefault(f => f.Path == target1);
        Assert.IsNotNull(indexedTarget1);
        Assert.IsTrue(indexedTarget1.IsOnline);

        var target1Tags = tagService.ListTagsForFile(indexedTarget1.Id);
        Assert.IsTrue(target1Tags.Any(t => t.Name == "Tag1"));

        var autoTags = tagService.ListAutomaticTagsForFile(indexedTarget1.Id);
        Assert.IsTrue(autoTags.Any(t => t.Name == "格式/TXT"));
    }

    [TestMethod]
    public async Task RecycleBinDelete_Crash_WhenSourceGone_MarksOffline_RetainsTags_NeverDeletesAgain()
    {
        using var env = TestEnvironment.Create();
        var deleteFile = env.CreateFile("delete_me.txt", "to be deleted");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        var fileId = initialFiles[0].Id;

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("DoNotLoseTag");
        tagService.AddTagToFiles(tag.Id, [fileId]);

        // Simulate Shell delete already happened: file gone from disk
        File.Delete(deleteFile);
        Assert.IsFalse(File.Exists(deleteFile));

        var committer = new FileOperationIndexCommitter(env.Scanner);
        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-delete-gone",
                operationType: "recycle_bin_delete",
                collisionPolicy: "auto_rename",
                items: [(deleteFile, null, null, null)]);
        }

        // Act: Run crash recovery
        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report = await recoveryService.RecoverAsync();

        Assert.AreEqual(1, report.RecoveredIntentsCount);
        Assert.AreEqual(0, report.IndeterminateIntentsCount);

        // Assert: In DB, file is offline, but record and tags retained
        var allFiles = await queryService.QueryAsync(new());
        var dbFile = allFiles.Single(f => f.Path == deleteFile);
        Assert.IsFalse(dbFile.IsOnline);

        var tags = tagService.ListTagsForFile(dbFile.Id);
        Assert.HasCount(1, tags);
        Assert.AreEqual("DoNotLoseTag", tags[0].Name);
    }

    [TestMethod]
    public async Task RecycleBinDelete_Crash_WhenSourceStillExists_KeepsOnlineAndTags_NeverDeletesAgain()
    {
        using var env = TestEnvironment.Create();
        var stayFile = env.CreateFile("stay.txt", "should stay");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        var fileId = initialFiles[0].Id;

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("SafeTag");
        tagService.AddTagToFiles(tag.Id, [fileId]);

        // File still on disk (crash before Shell delete executed)
        Assert.IsTrue(File.Exists(stayFile));

        var committer = new FileOperationIndexCommitter(env.Scanner);
        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-delete-stay",
                operationType: "recycle_bin_delete",
                collisionPolicy: "auto_rename",
                items: [(stayFile, null, null, null)]);
        }

        // Act: Run crash recovery
        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report = await recoveryService.RecoverAsync();

        Assert.AreEqual(1, report.RecoveredIntentsCount);

        // Assert: File STILL exists on disk!
        Assert.IsTrue(File.Exists(stayFile));

        // File remains online and tags intact
        var currentFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, currentFiles);
        Assert.AreEqual(stayFile, currentFiles[0].Path);
        Assert.IsTrue(currentFiles[0].IsOnline);

        var tags = tagService.ListTagsForFile(currentFiles[0].Id);
        Assert.HasCount(1, tags);
        Assert.AreEqual("SafeTag", tags[0].Name);
    }

    [TestMethod]
    public async Task Ambiguity_NeitherExists_Or_Conflict_MarkedAsIndeterminate_PreservesNodesAndTags()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = Path.Combine(env.RootPath, "ghost_source.txt");
        var targetPath = Path.Combine(env.RootPath, "ghost_target.txt");

        // Neither source nor target exists on disk
        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsFalse(File.Exists(targetPath));

        var root = env.Scanner.AddRoot(env.RootPath);

        // Insert a record into files table for ghost_source with tag
        long ghostDbId;
        using (var conn = SqliteDatabase.Open(env.DatabasePath))
        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, is_online)
                VALUES (1, 'vol-1', 'fid-ghost', $path, $path, 'ghost_source.txt', '.txt', 10, '2026-09-05T00:00:00Z', 1)
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$path", sourcePath);
            ghostDbId = (long)cmd.ExecuteScalar()!;
            tx.Commit();
        }

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("PreserveMe");
        tagService.AddTagToFiles(tag.Id, [ghostDbId]);

        // Insert intent with ambiguous move
        var committer = new FileOperationIndexCommitter(env.Scanner);
        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-ambiguous",
                operationType: "move",
                collisionPolicy: "auto_rename",
                items: [(sourcePath, env.RootPath, "ghost_target.txt", targetPath)]);
        }

        // Act: Run crash recovery
        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report = await recoveryService.RecoverAsync();

        // Assert: Marked indeterminate!
        Assert.AreEqual(1, report.IndeterminateIntentsCount);
        Assert.IsTrue(report.HasIndeterminateOperations);

        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            Assert.AreEqual("indeterminate", committer.GetIntentStatus(connection, intentId));
        }

        // Source node and tags in DB are NOT deleted or corrupted
        var queryService = new FileQueryService(env.DatabasePath);
        var files = await queryService.QueryAsync(new());
        Assert.IsTrue(files.Any(f => f.Id == ghostDbId));

        var tags = tagService.ListTagsForFile(ghostDbId);
        Assert.HasCount(1, tags);
        Assert.AreEqual("PreserveMe", tags[0].Name);
    }

    [TestMethod]
    public void BoundedCleanup_PurgesOldCommittedIntents_RetainsPendingAndIndeterminateIntents()
    {
        using var env = TestEnvironment.Create();
        var committer = new FileOperationIndexCommitter(env.Scanner);

        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        using (var tx = connection.BeginTransaction())
        {
            // Insert 120 old committed intents (> 14 days ago)
            var oldDate = DateTime.UtcNow.AddDays(-20).ToString("O");
            for (int i = 1; i <= 120; i++)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    $"""
                    INSERT INTO file_operation_intents (correlation_id, operation_type, status, created_utc, completed_utc)
                    VALUES ('old-{i}', 'copy', 'committed', '{oldDate}', '{oldDate}');
                    """;
                cmd.ExecuteNonQuery();
            }

            // Insert 30 recent committed intents
            var recentDate = DateTime.UtcNow.AddDays(-1).ToString("O");
            for (int i = 121; i <= 150; i++)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    $"""
                    INSERT INTO file_operation_intents (correlation_id, operation_type, status, created_utc, completed_utc)
                    VALUES ('recent-{i}', 'copy', 'committed', '{recentDate}', '{recentDate}');
                    """;
                cmd.ExecuteNonQuery();
            }

            // Insert 2 pending intents
            for (int i = 1; i <= 2; i++)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    $"""
                    INSERT INTO file_operation_intents (correlation_id, operation_type, status, created_utc, completed_utc)
                    VALUES ('pending-{i}', 'move', 'pending', '{oldDate}', NULL);
                    """;
                cmd.ExecuteNonQuery();
            }

            // Insert 2 indeterminate intents
            for (int i = 1; i <= 2; i++)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    $"""
                    INSERT INTO file_operation_intents (correlation_id, operation_type, status, created_utc, completed_utc)
                    VALUES ('indeterminate-{i}', 'move', 'indeterminate', '{oldDate}', '{oldDate}');
                    """;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // Run bounded cleanup
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            committer.PurgeCommittedIntents(connection, maxCommittedToRetain: 100, retentionDays: 14);

            // Assert: committed intents are purged down to bounded count (<= 100)
            using var cmd1 = connection.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(*) FROM file_operation_intents WHERE status = 'committed';";
            var committedCount = (long)cmd1.ExecuteScalar()!;
            Assert.IsLessThanOrEqualTo(100L, committedCount);

            // Assert: pending and indeterminate intents are completely intact!
            using var cmd2 = connection.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(*) FROM file_operation_intents WHERE status = 'pending';";
            Assert.AreEqual(2L, cmd2.ExecuteScalar());

            using var cmd3 = connection.CreateCommand();
            cmd3.CommandText = "SELECT COUNT(*) FROM file_operation_intents WHERE status = 'indeterminate';";
            Assert.AreEqual(2L, cmd3.ExecuteScalar());
        }
    }

    private sealed class TestEnvironment : IDisposable
    {
        public string RootPath { get; }
        public string DatabasePath { get; }
        public ManagedRootScanner Scanner { get; }

        private TestEnvironment(string rootPath, string databasePath, ManagedRootScanner scanner)
        {
            RootPath = rootPath;
            DatabasePath = databasePath;
            Scanner = scanner;
        }

        public static TestEnvironment Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"GuraFile_CrashTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, ".gurafile", "index.db");
            var scanner = new ManagedRootScanner(dbPath);
            return new(root, dbPath, scanner);
        }

        public string CreateFile(string relativeName, string content)
        {
            var filePath = Path.Combine(RootPath, relativeName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public string CreateDirectory(string relativeName)
        {
            var dirPath = Path.Combine(RootPath, relativeName);
            Directory.CreateDirectory(dirPath);
            return dirPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
