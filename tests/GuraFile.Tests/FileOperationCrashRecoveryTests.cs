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

            committer.UpdateIntentShellCompleted(
                connection,
                intentId,
                [
                    (file1, target1, "completed", null),
                    (file2, target2, "failed", "Shell interrupted")
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

    [TestMethod]
    public async Task Copy_CrashRecovery_PendingOrSkipped_PreventsOverwritingExistingTargetTags()
    {
        using var env = TestEnvironment.Create();
        var sourceFile = env.CreateFile("source.txt", "source content");
        var destDir = env.CreateDirectory("Dest");
        var targetFile = Path.Combine(destDir, "source.txt");
        File.WriteAllText(targetFile, "target original content");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        var sourceDb = initialFiles.Single(f => f.Path == sourceFile);
        var targetDb = initialFiles.Single(f => f.Path == targetFile);

        var tagService = new TagService(env.DatabasePath);
        var tagA = tagService.CreateTag("TagA");
        var tagB = tagService.CreateTag("TagB");
        tagService.AddTagToFiles(tagA.Id, [sourceDb.Id]);
        tagService.AddTagToFiles(tagB.Id, [targetDb.Id]);

        // Case 1: Crash while intent is pending (Shell never called)
        var committer = new FileOperationIndexCommitter(env.Scanner);
        long pendingIntentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            pendingIntentId = committer.InsertIntent(
                connection,
                correlationId: "test-copy-pending-collision",
                operationType: "copy",
                collisionPolicy: "skip",
                items: [(sourceFile, destDir, "source.txt", targetFile)]);
        }

        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report1 = await recoveryService.RecoverAsync();

        // Target's TagB must be preserved intact and NOT replaced by TagA!
        var targetTags1 = tagService.ListTagsForFile(targetDb.Id);
        Assert.IsTrue(targetTags1.Any(t => t.Name == "TagB"), "Target's existing TagB should be preserved.");
        Assert.IsFalse(targetTags1.Any(t => t.Name == "TagA"), "Target should not inherit source's TagA on pending crash.");
        Assert.AreEqual("target original content", File.ReadAllText(targetFile));

        // Case 2: Intent reached Shell, but collision policy was skip and item was recorded skipped
        long skippedIntentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            skippedIntentId = committer.InsertIntent(
                connection,
                correlationId: "test-copy-skipped-collision",
                operationType: "copy",
                collisionPolicy: "skip",
                items: [(sourceFile, destDir, "source.txt", targetFile)]);

            committer.UpdateIntentShellCompleted(
                connection,
                skippedIntentId,
                [(sourceFile, targetFile, "skipped", "File already exists")]);
        }

        var report2 = await recoveryService.RecoverAsync();

        var targetTags2 = tagService.ListTagsForFile(targetDb.Id);
        Assert.IsTrue(targetTags2.Any(t => t.Name == "TagB"), "Target's existing TagB should still be preserved after skipped recovery.");
        Assert.IsFalse(targetTags2.Any(t => t.Name == "TagA"), "Target should not inherit TagA when item was skipped.");
    }

    [TestMethod]
    public async Task Move_ReconciledByScannerBeforeRecovery_PreventsErasingTargetTags()
    {
        using var env = TestEnvironment.Create();
        var sourceFile = env.CreateFile("source_move.txt", "content to move");
        var destDir = env.CreateDirectory("Dest");
        var targetFile = Path.Combine(destDir, "moved.txt");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        var sourceDb = initialFiles.Single(f => f.Path == sourceFile);

        var tagService = new TagService(env.DatabasePath);
        var tagA = tagService.CreateTag("TagA");
        tagService.AddTagToFiles(tagA.Id, [sourceDb.Id]);

        // Shell move succeeded: physically move file
        File.Move(sourceFile, targetFile);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-move-scanner-first",
                operationType: "move",
                collisionPolicy: "auto_rename",
                items: [(sourceFile, destDir, "moved.txt", targetFile)]);

            committer.UpdateIntentShellCompleted(
                connection,
                intentId,
                [(sourceFile, targetFile, "completed", null)]);
        }

        // Simulate scanner running BEFORE crash recovery service runs
        await env.Scanner.ScanAsync(root.Id);

        // Verify that scanner updated path to targetFile and kept TagA in DB
        var postScanFiles = await queryService.QueryAsync(new());
        var scannedTarget = postScanFiles.Single(f => f.Path == targetFile);
        var postScanTags = tagService.ListTagsForFile(scannedTarget.Id);
        Assert.IsTrue(postScanTags.Any(t => t.Name == "TagA"));

        // Now run crash recovery
        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report = await recoveryService.RecoverAsync();

        Assert.AreEqual(1, report.RecoveredIntentsCount);

        // Assert: Target file in DB still has TagA and was NOT erased!
        var finalFiles = await queryService.QueryAsync(new());
        var finalTarget = finalFiles.Single(f => f.Path == targetFile);
        var finalTags = tagService.ListTagsForFile(finalTarget.Id);
        Assert.IsTrue(finalTags.Any(t => t.Name == "TagA"), "Target's TagA must be preserved and not erased by recovery.");
    }

    [TestMethod]
    public async Task IndeterminateIntents_PersistAcrossRecoveryRuns_CorrectlyReported()
    {
        using var env = TestEnvironment.Create();
        var committer = new FileOperationIndexCommitter(env.Scanner);

        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-indeterminate-persist",
                operationType: "move",
                collisionPolicy: "auto_rename",
                items: [("C:\\nonexistent\\file1.txt", "C:\\nonexistent\\dest", "file1.txt", "C:\\nonexistent\\dest\\file1.txt")]);

            // Set intent as indeterminate (as would happen when recovery flags ambiguity or user hasn't resolved)
            committer.UpdateIntentIndeterminate(
                connection,
                intentId,
                [("C:\\nonexistent\\file1.txt", "indeterminate", "Ambiguous: source and target missing")]);
        }

        // Run recovery service (e.g. on application startup)
        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);
        var report = await recoveryService.RecoverAsync();

        // Assert: Indeterminate intent is loaded and reported
        Assert.AreEqual(1, report.IndeterminateIntentsCount);
        Assert.IsTrue(report.HasIndeterminateOperations);
        Assert.HasCount(1, report.IndeterminateDetails);

        // Assert: Status in database remains indeterminate and was NOT deleted or purged
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            Assert.AreEqual("indeterminate", committer.GetIntentStatus(connection, intentId));
        }
    }

    [TestMethod]
    public async Task CrossVolume_TargetScannedFirst_InheritsTagsAndMarksSourceOffline()
    {
        using var env = TestEnvironment.Create();
        var sourceFile = env.CreateFile("source_cross.txt", "content before cross move");
        var destDir = env.CreateDirectory("DestCross");
        var targetFile = Path.Combine(destDir, "target_cross.txt");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        var sourceDb = initialFiles.Single(f => f.Path == sourceFile);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("CrossVolumeTag");
        tagService.AddTagToFiles(tag.Id, [sourceDb.Id]);

        // Shell move completed: source deleted, target created on disk
        File.Delete(sourceFile);
        File.WriteAllText(targetFile, "content before cross move");

        // Use custom readIdentity to simulate different volumes:
        // source was on VOL1, target is on VOL2
        FileIdentity ReadIdentity(string path)
        {
            var norm = SafeFileOperationExecutor.Normalize(path);
            var normSrc = SafeFileOperationExecutor.Normalize(sourceFile);
            var normDst = SafeFileOperationExecutor.Normalize(targetFile);
            if (string.Equals(norm, normSrc, StringComparison.OrdinalIgnoreCase))
            {
                return new FileIdentity("VOL1", "SRC_FILE_ID", true, null);
            }
            if (string.Equals(norm, normDst, StringComparison.OrdinalIgnoreCase))
            {
                return new FileIdentity("VOL2", "DEST_FILE_ID", true, null);
            }
            return FileIdentityReader.Read(path);
        }

        // Set source file record in DB to VOL1 / SRC_FILE_ID
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE files SET volume_id = 'VOL1', file_id = 'SRC_FILE_ID' WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", sourceDb.Id);
            cmd.ExecuteNonQuery();
        }

        // Record intent as shell_completed
        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: ReadIdentity,
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: File.Exists,
            directoryExists: Directory.Exists);

        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-cross-volume-recovery",
                operationType: "move",
                collisionPolicy: "auto_rename",
                items: [(sourceFile, destDir, "target_cross.txt", targetFile)]);

            committer.UpdateIntentShellCompleted(
                connection,
                intentId,
                [(sourceFile, targetFile, "completed", null)]);
        }

        // Simulate scanner scanning target first on VOL2:
        // Target is inserted as a brand new node with (VOL2, DEST_FILE_ID) and no tags
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            using var insertTargetCmd = connection.CreateCommand();
            insertTargetCmd.CommandText =
                """
                INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, is_online, scan_token)
                VALUES ($rootId, 'VOL2', 'DEST_FILE_ID', $path, $normalizedPath, 'target_cross.txt', '.txt', 25, '2026-09-09T00:00:00Z', 'stable', 1, 'token123');
                """;
            insertTargetCmd.Parameters.AddWithValue("$rootId", root.Id);
            insertTargetCmd.Parameters.AddWithValue("$path", targetFile);
            insertTargetCmd.Parameters.AddWithValue("$normalizedPath", SafeFileOperationExecutor.Normalize(targetFile));
            insertTargetCmd.ExecuteNonQuery();
        }

        // Now run crash recovery
        var recoveryService = new FileOperationCrashRecoveryService(
            env.DatabasePath,
            env.Scanner,
            committer,
            diagnosticLogger: null,
            readIdentity: ReadIdentity);

        var report = await recoveryService.RecoverAsync();

        Assert.AreEqual(1, report.RecoveredIntentsCount);
        Assert.AreEqual(1, report.ReconciledItemsCount);
        Assert.AreEqual(0, report.IndeterminateIntentsCount);

        // Verify target inherited CrossVolumeTag
        var allFiles = await queryService.QueryAsync(new());
        var targetRecord = allFiles.Single(f => f.Path == targetFile);
        Assert.IsTrue(targetRecord.IsOnline);
        var targetTags = tagService.ListTagsForFile(targetRecord.Id);
        Assert.IsTrue(targetTags.Any(t => t.Name == "CrossVolumeTag"), "Target should inherit source's tag.");

        // Verify source node was marked offline in DB
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT is_online FROM files WHERE id = $id;";
            checkCmd.Parameters.AddWithValue("$id", sourceDb.Id);
            var isOnline = Convert.ToInt32(checkCmd.ExecuteScalar());
            Assert.AreEqual(0, isOnline, "Source node in DB must be marked offline (is_online = 0).");
        }
    }

    [TestMethod]
    public async Task OfflinePathFallback_DoesNotTakePrecedenceOverCurrentOnlineIdentity()
    {
        using var env = TestEnvironment.Create();
        var pathX = Path.Combine(env.RootPath, "fileX.txt");
        var pathY = Path.Combine(env.RootPath, "fileY.txt");

        var root = env.Scanner.AddRoot(env.RootPath);

        // In DB:
        // 1. File A at pathX is offline (is_online = 0), identity (VOL1, FILE_A), has tag STALE-TAG
        // 2. File B at pathY is online (is_online = 1), identity (VOL1, FILE_B), has tag CURRENT-TAG
        var tagService = new TagService(env.DatabasePath);
        var staleTag = tagService.CreateTag("STALE-TAG");
        var currentTag = tagService.CreateTag("CURRENT-TAG");

        long fileAId;
        long fileBId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, is_online, scan_token)
                    VALUES ($rootId, 'VOL1', 'FILE_A', $pathX, $normPathX, 'fileX.txt', '.txt', 100, '2026-09-01T00:00:00Z', 'stable', 0, 'tok1')
                    RETURNING id;
                    """;
                cmd.Parameters.AddWithValue("$rootId", root.Id);
                cmd.Parameters.AddWithValue("$pathX", pathX);
                cmd.Parameters.AddWithValue("$normPathX", SafeFileOperationExecutor.Normalize(pathX));
                fileAId = (long)cmd.ExecuteScalar()!;
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, is_online, scan_token)
                    VALUES ($rootId, 'VOL1', 'FILE_B', $pathY, $normPathY, 'fileY.txt', '.txt', 200, '2026-09-02T00:00:00Z', 'stable', 1, 'tok2')
                    RETURNING id;
                    """;
                cmd.Parameters.AddWithValue("$rootId", root.Id);
                cmd.Parameters.AddWithValue("$pathY", pathY);
                cmd.Parameters.AddWithValue("$normPathY", SafeFileOperationExecutor.Normalize(pathY));
                fileBId = (long)cmd.ExecuteScalar()!;
            }
        }

        tagService.AddTagToFiles(staleTag.Id, [fileAId]);
        tagService.AddTagToFiles(currentTag.Id, [fileBId]);

        // On disk:
        // File B is placed at pathX (replacing old file A).
        // So pathX exists physically on disk with stable identity (VOL1, FILE_B)!
        File.WriteAllText(pathX, "content of file B");

        FileIdentity ReadIdentity(string path)
        {
            var norm = SafeFileOperationExecutor.Normalize(path);
            var normX = SafeFileOperationExecutor.Normalize(pathX);
            if (string.Equals(norm, normX, StringComparison.OrdinalIgnoreCase))
            {
                return new FileIdentity("VOL1", "FILE_B", true, null);
            }
            return FileIdentityReader.Read(path);
        }

        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: ReadIdentity,
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: File.Exists,
            directoryExists: Directory.Exists);

        // Query source snapshot for pathX
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            var snapshot = committer.QuerySourceSnapshot(connection, SafeFileOperationExecutor.Normalize(pathX));

            Assert.IsTrue(snapshot.UserTags.Contains("CURRENT-TAG"), "Source snapshot must inherit CURRENT-TAG from online identity.");
            Assert.IsFalse(snapshot.UserTags.Contains("STALE-TAG"), "Source snapshot must never inherit STALE-TAG from offline path record.");
        }
    }

    [TestMethod]
    public async Task PendingCopy_WhenTargetExistsOnDisk_RemainsIndeterminateAcrossRestarts()
    {
        using var env = TestEnvironment.Create();
        var sourceFile = env.CreateFile("source_copy.txt", "source content");
        var destDir = env.CreateDirectory("DestCopy");
        var targetFile = Path.Combine(destDir, "target_copy.txt");
        // Target file already exists on disk!
        File.WriteAllText(targetFile, "existing target content");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        long intentId;
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            intentId = committer.InsertIntent(
                connection,
                correlationId: "test-pending-copy-exists",
                operationType: "copy",
                collisionPolicy: "auto_rename",
                items: [(sourceFile, destDir, "target_copy.txt", targetFile)]);
            // Intent status is "pending" (Shell call not completed before crash)
        }

        var recoveryService = new FileOperationCrashRecoveryService(env.DatabasePath, env.Scanner, committer);

        // 1st Recovery Run
        var report1 = await recoveryService.RecoverAsync();
        Assert.AreEqual(1, report1.IndeterminateIntentsCount, "First recovery must report 1 indeterminate intent.");
        Assert.IsTrue(report1.HasIndeterminateOperations);
        Assert.AreEqual(0, report1.RecoveredIntentsCount);

        // Verify target file on disk was NOT overwritten
        Assert.AreEqual("existing target content", File.ReadAllText(targetFile));

        // Verify database state: intent is indeterminate and item error is set
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            Assert.AreEqual("indeterminate", committer.GetIntentStatus(connection, intentId));
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT commit_status, error FROM file_operation_intent_items WHERE intent_id = $id;";
            cmd.Parameters.AddWithValue("$id", intentId);
            using var reader = cmd.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("indeterminate", reader.GetString(0));
            var error = reader.GetString(1);
            StringAssert.Contains(error, "执行状态未决：操作记录为 pending 但目标文件已存在，未执行覆盖，请核实目标文件及标签");
        }

        // 2nd Recovery Run (simulating restart)
        var report2 = await recoveryService.RecoverAsync();
        Assert.AreEqual(1, report2.IndeterminateIntentsCount, "Second recovery must still report 1 indeterminate intent across restarts.");
        Assert.IsTrue(report2.HasIndeterminateOperations);

        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            Assert.AreEqual("indeterminate", committer.GetIntentStatus(connection, intentId));
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
