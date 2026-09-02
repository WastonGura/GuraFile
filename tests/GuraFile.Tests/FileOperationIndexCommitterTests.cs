using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class FileOperationIndexCommitterTests
{
    [TestMethod]
    public async Task SameVolumeRename_PreservesUserTags_AndReclassifiesAutomaticTags()
    {
        using var env = TestEnvironment.Create();
        var originalPath = env.CreateFile("notes.txt", "Initial text note");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, initialFiles);
        var fileId = initialFiles[0].Id;

        var tagService = new TagService(env.DatabasePath);
        var userTag = tagService.CreateTag("Project Alpha");
        tagService.AddTagToFiles(userTag.Id, [fileId]);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var renameResult = await committer.RenameAsync(originalPath, "notes.md", [root.Path]);

        Assert.AreEqual(FileOperationItemStatus.Completed, renameResult.Status, renameResult.Error);
        Assert.IsNotNull(renameResult.ActualTargetPath);
        Assert.IsTrue(File.Exists(renameResult.ActualTargetPath));
        Assert.IsFalse(File.Exists(originalPath));

        var updatedFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, updatedFiles);
        Assert.AreEqual("notes.md", updatedFiles[0].Name);
        Assert.IsTrue(updatedFiles[0].IsOnline);

        var userTags = tagService.ListTagsForFile(updatedFiles[0].Id);
        Assert.HasCount(1, userTags);
        Assert.AreEqual("Project Alpha", userTags[0].Name);

        var autoTags = tagService.ListAutomaticTagsForFile(updatedFiles[0].Id);
        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/Markdown" },
            autoTags.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task SameVolumeMove_PreservesUserTags_AcrossDirectories()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("report.pdf", "%PDF-1.4 header dummy content");
        var destDir = env.CreateDirectory("Archive");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, initialFiles);

        var tagService = new TagService(env.DatabasePath);
        var tag1 = tagService.CreateTag("Tag1");
        var tag2 = tagService.CreateTag("Tag2");
        tagService.AddTagToFiles(tag1.Id, [initialFiles[0].Id]);
        tagService.AddTagToFiles(tag2.Id, [initialFiles[0].Id]);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var moveResult = await committer.MoveAsync([sourcePath], destDir, [root.Path]);

        Assert.AreEqual(1, moveResult.TotalCount);
        Assert.AreEqual(1, moveResult.SucceededCount);
        var item = moveResult.Items[0];
        Assert.AreEqual(FileOperationItemStatus.Completed, item.Status);
        Assert.IsNotNull(item.ActualTargetPath);

        var updatedFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, updatedFiles);
        Assert.AreEqual(Path.Combine(destDir, "report.pdf"), updatedFiles[0].Path);
        Assert.IsTrue(updatedFiles[0].IsOnline);

        var userTags = tagService.ListTagsForFile(updatedFiles[0].Id);
        CollectionAssert.AreEquivalent(
            new[] { "Tag1", "Tag2" },
            userTags.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task Copy_CreatesNewIndexRecord_InheritsUserTags_ComputesAutoTags()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("data.txt", "some plain text data");
        var destDir = env.CreateDirectory("Copies");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, initialFiles);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("Important");
        tagService.AddTagToFiles(tag.Id, [initialFiles[0].Id]);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var copyResult = await committer.CopyAsync([sourcePath], destDir, [root.Path]);

        Assert.AreEqual(1, copyResult.TotalCount);
        Assert.AreEqual(1, copyResult.SucceededCount);
        var item = copyResult.Items[0];
        Assert.AreEqual(FileOperationItemStatus.Completed, item.Status);
        Assert.IsNotNull(item.ActualTargetPath);
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(item.ActualTargetPath));

        var allFiles = await queryService.QueryAsync(new());
        Assert.HasCount(2, allFiles);

        var sourceDbFile = allFiles.Single(f => f.Path == sourcePath);
        var targetDbFile = allFiles.Single(f => f.Path == item.ActualTargetPath);
        Assert.AreNotEqual(sourceDbFile.Id, targetDbFile.Id);

        var sourceUserTags = tagService.ListTagsForFile(sourceDbFile.Id);
        var targetUserTags = tagService.ListTagsForFile(targetDbFile.Id);
        Assert.AreEqual("Important", sourceUserTags.Single().Name);
        Assert.AreEqual("Important", targetUserTags.Single().Name);

        var targetAutoTags = tagService.ListAutomaticTagsForFile(targetDbFile.Id);
        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/TXT" },
            targetAutoTags.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task CrossVolumeMove_WhenSourceDeleted_CreatesTargetWithInheritedTagsAndMarksSourceMissing()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("cross.txt", "cross volume content");
        var targetPath = Path.Combine(env.RootPath, "TargetSub", "cross.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var userTag = tagService.CreateTag("CrossVolumeTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(userTag.Id, [initialFiles[0].Id]);

        // Simulate cross-volume move using testable committer:
        // Source identity = Vol1 / File1
        // Target identity = Vol2 / File2 (different volume)
        // Source file is deleted on disk
        File.WriteAllText(targetPath, "cross volume content");
        File.Delete(sourcePath);

        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: path => path == sourcePath
                ? new FileIdentity("VOL1", "SRC_FILE_ID", true, null)
                : new FileIdentity("VOL2", "DEST_FILE_ID", true, null),
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: path => path == targetPath,
            directoryExists: Directory.Exists);

        var simulatedItem = new FileOperationItemResult(
            sourcePath,
            targetPath,
            FileOperationItemStatus.Completed);

        var commitBatchResult = await committer.CommitBatchAsync(
            [simulatedItem],
            isMove: true,
            onlineRootPaths: [root.Path]);

        Assert.AreEqual(1, commitBatchResult.SucceededCount);

        var allFiles = await queryService.QueryAsync(new());
        var onlineFiles = allFiles.Where(f => f.IsOnline).ToList();
        var offlineFiles = allFiles.Where(f => !f.IsOnline).ToList();

        Assert.HasCount(1, onlineFiles);
        Assert.AreEqual(targetPath, onlineFiles[0].Path);
        var targetTags = tagService.ListTagsForFile(onlineFiles[0].Id);
        Assert.AreEqual("CrossVolumeTag", targetTags.Single().Name);

        Assert.HasCount(1, offlineFiles);
        Assert.AreEqual(sourcePath, offlineFiles[0].Path);
        var sourceTags = tagService.ListTagsForFile(offlineFiles[0].Id);
        Assert.AreEqual("CrossVolumeTag", sourceTags.Single().Name);
    }

    [TestMethod]
    public async Task CrossVolumeMove_WhenSourceDeletionFails_KeepsSourceOnline()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("locked.txt", "locked cross volume content");
        var targetPath = Path.Combine(env.RootPath, "TargetSub", "locked.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var userTag = tagService.CreateTag("KeepOnlineTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(userTag.Id, [initialFiles[0].Id]);

        // Target created on disk, BUT source file STILL exists on disk
        File.WriteAllText(targetPath, "locked cross volume content");

        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: path => path == sourcePath
                ? new FileIdentity("VOL1", "SRC_LOCKED_ID", true, null)
                : new FileIdentity("VOL2", "DEST_COPIED_ID", true, null),
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: _ => true, // both source and target exist
            directoryExists: Directory.Exists);

        var simulatedItem = new FileOperationItemResult(
            sourcePath,
            targetPath,
            FileOperationItemStatus.Completed);

        var commitBatchResult = await committer.CommitBatchAsync(
            [simulatedItem],
            isMove: true,
            onlineRootPaths: [root.Path]);

        Assert.AreEqual(1, commitBatchResult.SucceededCount);

        var allFiles = await queryService.QueryAsync(new());
        var onlineFiles = allFiles.Where(f => f.IsOnline).ToList();

        // Both source and target remain online because source was not deleted on disk
        Assert.HasCount(2, onlineFiles);
        var srcDb = onlineFiles.Single(f => f.Path == sourcePath);
        var dstDb = onlineFiles.Single(f => f.Path == targetPath);

        Assert.AreEqual("KeepOnlineTag", tagService.ListTagsForFile(srcDb.Id).Single().Name);
        Assert.AreEqual("KeepOnlineTag", tagService.ListTagsForFile(dstDb.Id).Single().Name);
    }

    [TestMethod]
    public async Task BatchOperation_WithPartialFailure_OnlyCommitsSucceededItems()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("file1.txt", "content 1");
        var file2 = env.CreateFile("file2.txt", "content 2");
        var file3 = env.CreateFile("file3.txt", "content 3");
        var destDir = env.CreateDirectory("BatchDest");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var t1 = tagService.CreateTag("T1");
        var t2 = tagService.CreateTag("T2");
        var t3 = tagService.CreateTag("T3");

        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(t1.Id, [initFiles.Single(f => f.Path == file1).Id]);
        tagService.AddTagToFiles(t2.Id, [initFiles.Single(f => f.Path == file2).Id]);
        tagService.AddTagToFiles(t3.Id, [initFiles.Single(f => f.Path == file3).Id]);

        var target1 = Path.Combine(destDir, "file1.txt");
        var target3 = Path.Combine(destDir, "file3.txt");
        File.WriteAllText(target1, "content 1");
        File.WriteAllText(target3, "content 3");

        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: path => new FileIdentity("VOL1", Path.GetFileName(path), true, null),
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: File.Exists,
            directoryExists: Directory.Exists);

        var items = new List<FileOperationItemResult>
        {
            new(file1, target1, FileOperationItemStatus.Completed),
            new(file2, null, FileOperationItemStatus.Failed, "无法访问：文件正被其他程序占用。"),
            new(file3, target3, FileOperationItemStatus.Completed)
        };

        var batchResult = await committer.CommitBatchAsync(
            items,
            isMove: false,
            onlineRootPaths: [root.Path]);

        Assert.AreEqual(3, batchResult.TotalCount);
        Assert.AreEqual(2, batchResult.SucceededCount);
        Assert.AreEqual(1, batchResult.FailedCount);
        Assert.AreEqual("无法访问：文件正被其他程序占用。", batchResult.Items[1].Error);

        var allFiles = await queryService.QueryAsync(new());
        Assert.IsNotNull(allFiles.FirstOrDefault(f => f.Path == target1));
        Assert.IsNotNull(allFiles.FirstOrDefault(f => f.Path == target3));
        Assert.IsNull(allFiles.FirstOrDefault(f => f.Path == Path.Combine(destDir, "file2.txt")));
    }

    [TestMethod]
    public async Task ExternalReplacement_RejectsCommitAndRequiresRescan()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("source.txt", "original data");
        var targetPath = Path.Combine(env.RootPath, "target.txt");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("DoNotTransfer");
        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [initialFiles[0].Id]);

        // Simulate that target file was created with an unexpected identity (replaced externally)
        File.WriteAllText(targetPath, "imposter data");

        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: path => path == sourcePath
                ? new FileIdentity("VOL1", "ORIGINAL_ID", true, null)
                : new FileIdentity("VOL1", "DIFFERENT_REPLACED_ID", true, null),
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: File.Exists,
            directoryExists: Directory.Exists);

        var simulatedItem = new FileOperationItemResult(
            sourcePath,
            targetPath,
            FileOperationItemStatus.Completed);

        var batchResult = await committer.CommitBatchAsync(
            [simulatedItem],
            isMove: true,
            onlineRootPaths: [root.Path]);

        Assert.AreEqual(0, batchResult.SucceededCount);
        Assert.AreEqual(1, batchResult.FailedCount);
        StringAssert.Contains(batchResult.Items[0].Error, "身份与源文件不匹配");

        // Verify the replaced file did not get the tag
        var allFiles = await queryService.QueryAsync(new());
        Assert.IsFalse(allFiles.Any(f => f.Path == targetPath));
    }

    [TestMethod]
    public async Task Idempotency_WhenWatcherReconcileEventsArriveLater_DoesNotDuplicateNodesOrTags()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("idempotent.txt", "content for idempotency test");
        var destDir = env.CreateDirectory("NewFolder");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("StableTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [initFiles[0].Id]);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var moveResult = await committer.MoveAsync([sourcePath], destDir, [root.Path]);
        Assert.AreEqual(1, moveResult.SucceededCount);
        var targetPath = moveResult.Items[0].ActualTargetPath!;

        // Now simulate FileSystemWatcher event triggering ReconcilePathsAsync on oldPath and newPath
        var reconcileResult = await env.Scanner.ReconcilePathsAsync(root.Id, [sourcePath, targetPath]);
        Assert.IsFalse(reconcileResult.Canceled);

        var allFiles = await queryService.QueryAsync(new());
        var onlineFiles = allFiles.Where(f => f.IsOnline).ToList();

        Assert.HasCount(1, onlineFiles);
        Assert.AreEqual(targetPath, onlineFiles[0].Path);
        var tags = tagService.ListTagsForFile(onlineFiles[0].Id);
        Assert.HasCount(1, tags);
        Assert.AreEqual("StableTag", tags[0].Name);
    }

    [TestMethod]
    public async Task FullScanAfterRestart_MatchesCommittedState()
    {
        using var env = TestEnvironment.Create();
        var file1 = env.CreateFile("file1.txt", "content 1");
        var file2 = env.CreateFile("file2.txt", "content 2");
        var copyDir = env.CreateDirectory("Copies");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag1 = tagService.CreateTag("Tag1");
        var tag2 = tagService.CreateTag("Tag2");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag1.Id, [initFiles.Single(f => f.Path == file1).Id]);
        tagService.AddTagToFiles(tag2.Id, [initFiles.Single(f => f.Path == file2).Id]);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        await committer.CopyAsync([file1], copyDir, [root.Path]);
        await committer.RenameAsync(file2, "file2_renamed.md", [root.Path]);

        var stateBeforeRescan = await queryService.QueryAsync(new());

        // Perform full scan (as on restart)
        var rescanResult = await env.Scanner.ScanAsync(root.Id);
        Assert.AreEqual(0, rescanResult.AddedFiles);
        Assert.AreEqual(0, rescanResult.MissingFiles);

        var stateAfterRescan = await queryService.QueryAsync(new());
        Assert.HasCount(stateBeforeRescan.Count, stateAfterRescan);

        foreach (var fileBefore in stateBeforeRescan)
        {
            var fileAfter = stateAfterRescan.Single(f => f.Path == fileBefore.Path);
            Assert.AreEqual(fileBefore.IsOnline, fileAfter.IsOnline);

            var tagsBefore = tagService.ListTagsForFile(fileBefore.Id).Select(t => t.Name).OrderBy(n => n).ToArray();
            var tagsAfter = tagService.ListTagsForFile(fileAfter.Id).Select(t => t.Name).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(tagsBefore, tagsAfter);

            var autoBefore = tagService.ListAutomaticTagsForFile(fileBefore.Id).Select(t => t.Name).OrderBy(n => n).ToArray();
            var autoAfter = tagService.ListAutomaticTagsForFile(fileAfter.Id).Select(t => t.Name).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(autoBefore, autoAfter);
        }
    }

    [TestMethod]
    public async Task CollisionPolicy_AutoRename_CommitsActualRenamedTargetPath()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("conflict.txt", "new version");
        var destDir = env.CreateDirectory("Dest");
        var existingPath = Path.Combine(destDir, "conflict.txt");
        File.WriteAllText(existingPath, "original existing version");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("AutoRenameTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [initFiles.Single(f => f.Path == sourcePath).Id]);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var copyResult = await committer.CopyAsync(
            [sourcePath],
            destDir,
            [root.Path],
            collisionPolicy: FileCollisionPolicy.AutoRename);

        Assert.AreEqual(1, copyResult.SucceededCount);
        var actualPath = copyResult.Items[0].ActualTargetPath;
        Assert.IsNotNull(actualPath);
        Assert.AreNotEqual(existingPath, actualPath);
        Assert.IsTrue(File.Exists(actualPath));

        var allFiles = await queryService.QueryAsync(new());
        var renamedDb = allFiles.Single(f => f.Path == actualPath);
        var tags = tagService.ListTagsForFile(renamedDb.Id);
        Assert.AreEqual("AutoRenameTag", tags.Single().Name);
    }

    [TestMethod]
    public async Task CollisionPolicy_Skip_DoesNotAlterDatabaseForSkippedItem()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("skip.txt", "source text");
        var destDir = env.CreateDirectory("SkipDest");
        var existingPath = Path.Combine(destDir, "skip.txt");
        File.WriteAllText(existingPath, "existing target");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var queryService = new FileQueryService(env.DatabasePath);
        var filesBefore = await queryService.QueryAsync(new());

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var copyResult = await committer.CopyAsync(
            [sourcePath],
            destDir,
            [root.Path],
            collisionPolicy: FileCollisionPolicy.Skip);

        Assert.AreEqual(1, copyResult.SkippedCount);
        Assert.AreEqual(FileOperationItemStatus.Skipped, copyResult.Items[0].Status);

        var filesAfter = await queryService.QueryAsync(new());
        Assert.HasCount(filesBefore.Count, filesAfter);
    }

    [TestMethod]
    public async Task DestinationOutsideManagedRoots_ThrowsArgumentException()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("source.txt", "content");
        var outsideDir = Path.Combine(Path.GetTempPath(), $"GuraFile_Outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var committer = new FileOperationIndexCommitter(env.Scanner);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await committer.CopyAsync([sourcePath], outsideDir, [env.RootPath]);
            });

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await committer.MoveAsync([sourcePath], outsideDir, [env.RootPath]);
            });
        }
        finally
        {
            if (Directory.Exists(outsideDir))
            {
                Directory.Delete(outsideDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task UnindexedFile_CopyAndMove_SucceedsWithAutomaticTags()
    {
        using var env = TestEnvironment.Create();
        // File created on disk but NOT scanned into database yet
        var unindexedFile = env.CreateFile("unindexed.md", "# Unindexed markdown");
        var destDir = env.CreateDirectory("UnindexedDest");
        var root = env.Scanner.AddRoot(env.RootPath);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var copyResult = await committer.CopyAsync([unindexedFile], destDir, [root.Path]);

        Assert.AreEqual(1, copyResult.SucceededCount);
        var copiedPath = copyResult.Items[0].ActualTargetPath!;
        Assert.IsTrue(File.Exists(copiedPath));

        var queryService = new FileQueryService(env.DatabasePath);
        var allFiles = await queryService.QueryAsync(new());
        var copiedDb = allFiles.Single(f => f.Path == copiedPath);
        Assert.AreEqual("unindexed.md", copiedDb.Name);

        var tagService = new TagService(env.DatabasePath);
        var autoTags = tagService.ListAutomaticTagsForFile(copiedDb.Id);
        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/Markdown" },
            autoTags.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task OverwriteExistingTarget_ReplacesOldTargetNodeInIndex()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("source.txt", "new source data");
        var destDir = env.CreateDirectory("OverwriteDest");
        var existingTarget = Path.Combine(destDir, "source.txt");
        File.WriteAllText(existingTarget, "old stale data");

        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var oldTargetTag = tagService.CreateTag("OldTargetTag");
        var sourceTag = tagService.CreateTag("SourceTag");

        var queryService = new FileQueryService(env.DatabasePath);
        var initialFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(sourceTag.Id, [initialFiles.Single(f => f.Path == sourcePath).Id]);
        tagService.AddTagToFiles(oldTargetTag.Id, [initialFiles.Single(f => f.Path == existingTarget).Id]);

        var committer = new FileOperationIndexCommitter(env.Scanner);
        var copyResult = await committer.CopyAsync(
            [sourcePath],
            destDir,
            [root.Path],
            collisionPolicy: FileCollisionPolicy.Overwrite);

        Assert.AreEqual(1, copyResult.SucceededCount);

        var allFiles = await queryService.QueryAsync(new());
        var onlineFiles = allFiles.Where(f => f.IsOnline).ToList();
        Assert.HasCount(2, onlineFiles);

        var targetDb = onlineFiles.Single(f => f.Path == existingTarget);
        var targetTags = tagService.ListTagsForFile(targetDb.Id);
        // Overwritten target inherits source user tags
        Assert.AreEqual("SourceTag", targetTags.Single().Name);
    }

    [TestMethod]
    public async Task TargetDeletedBeforeCommit_ReturnsFailedStatus()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("deleted_before_commit.txt", "data");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var targetPath = Path.Combine(env.RootPath, "ghost_target.txt");

        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: path => new FileIdentity("VOL1", "SRC_ID", true, null),
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: path => path == sourcePath, // Target does NOT exist
            directoryExists: Directory.Exists);

        var item = new FileOperationItemResult(
            sourcePath,
            targetPath,
            FileOperationItemStatus.Completed);

        var result = await committer.CommitBatchAsync([item], isMove: false, onlineRootPaths: [root.Path]);

        Assert.AreEqual(1, result.FailedCount);
        StringAssert.Contains(result.Items[0].Error, "不存在或无法访问");
    }

    [TestMethod]
    public async Task PathFallback_RenameAndMove_PreservesUserTags()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("fallback.txt", "path fallback data");
        var root = env.Scanner.AddRoot(env.RootPath);

        // Seed with path-fallback identity
        using (var connection = SqliteDatabase.Open(env.DatabasePath))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, is_online)
                VALUES ($rootId, 'path-fallback', $sourcePath, $sourcePath, $sourcePath, 'fallback.txt', '.txt', 18, '2026-09-02T00:00:00Z', 'path', 1);
                """;
            cmd.Parameters.AddWithValue("$rootId", root.Id);
            cmd.Parameters.AddWithValue("$sourcePath", sourcePath);
            cmd.ExecuteNonQuery();
        }

        var tagService = new TagService(env.DatabasePath);
        var userTag = tagService.CreateTag("FallbackTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(userTag.Id, [initFiles[0].Id]);

        var targetPath = Path.Combine(env.RootPath, "fallback_renamed.md");
        File.WriteAllText(targetPath, "path fallback data");
        File.Delete(sourcePath);

        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: path => FileIdentity.PathFallback(path, "FAT32 volume"),
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: path => path == targetPath,
            directoryExists: Directory.Exists);

        var item = new FileOperationItemResult(sourcePath, targetPath, FileOperationItemStatus.Completed);
        var result = await committer.CommitBatchAsync([item], isMove: true, onlineRootPaths: [root.Path]);

        Assert.AreEqual(1, result.SucceededCount);

        var allFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, allFiles);
        Assert.AreEqual(targetPath, allFiles[0].Path);
        Assert.AreEqual("path", (await GetIdentityKindAsync(env.DatabasePath, allFiles[0].Id)));

        var tags = tagService.ListTagsForFile(allFiles[0].Id);
        Assert.AreEqual("FallbackTag", tags.Single().Name);
    }

    [TestMethod]
    public async Task CrossVolumeMove_WhenTargetCreationFails_KeepsSourceOnlineAndTagged()
    {
        using var env = TestEnvironment.Create();
        var sourcePath = env.CreateFile("fail_cross.txt", "source data");
        var targetPath = Path.Combine(env.RootPath, "TargetSub", "fail_cross.txt");
        var root = env.Scanner.AddRoot(env.RootPath);
        await env.Scanner.ScanAsync(root.Id);

        var tagService = new TagService(env.DatabasePath);
        var tag = tagService.CreateTag("SafeSourceTag");
        var queryService = new FileQueryService(env.DatabasePath);
        var initFiles = await queryService.QueryAsync(new());
        tagService.AddTagToFiles(tag.Id, [initFiles[0].Id]);

        var committer = new FileOperationIndexCommitter(
            env.DatabasePath,
            env.Scanner,
            executor: null,
            readIdentity: path => new FileIdentity("VOL1", "SRC_ID", true, null),
            classify: new FileTypeClassifier().Classify,
            getAttributes: File.GetAttributes,
            fileExists: path => path == sourcePath,
            directoryExists: Directory.Exists);

        // Simulated item where Shell failed (e.g. out of disk space)
        var failedItem = new FileOperationItemResult(
            sourcePath,
            null,
            FileOperationItemStatus.Failed,
            "无法操作“fail_cross.txt”：磁盘空间不足。");

        var result = await committer.CommitBatchAsync([failedItem], isMove: true, onlineRootPaths: [root.Path]);

        Assert.AreEqual(1, result.FailedCount);

        var allFiles = await queryService.QueryAsync(new());
        Assert.HasCount(1, allFiles);
        Assert.IsTrue(allFiles[0].IsOnline);
        Assert.AreEqual(sourcePath, allFiles[0].Path);

        var tags = tagService.ListTagsForFile(allFiles[0].Id);
        Assert.AreEqual("SafeSourceTag", tags.Single().Name);
    }

    private static async Task<string> GetIdentityKindAsync(string databasePath, long fileId)
    {
        using var connection = SqliteDatabase.Open(databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT identity_kind FROM files WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", fileId);
        var res = await cmd.ExecuteScalarAsync();
        return (string)res!;
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
            var root = Path.Combine(AppContext.BaseDirectory, $"GuraFile_CommitTests_{Guid.NewGuid():N}");
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
