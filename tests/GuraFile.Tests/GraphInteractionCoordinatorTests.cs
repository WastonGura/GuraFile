using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class GraphInteractionCoordinatorTests
{
    private static IndexedFile CreateTestFile(long id, string name, bool isOnline) =>
        new(
            Id: id,
            Name: name,
            Path: $@"C:\Test\{name}",
            Extension: Path.GetExtension(name),
            Size: 1024,
            Modified: DateTimeOffset.UtcNow,
            IsOnline: isOnline,
            Diagnostic: null,
            IdentityKind: "stable");

    [TestMethod]
    public void QueryGeneration_PreventsConcurrentOldResultOverwrite()
    {
        var coordinator = new GraphInteractionCoordinator();

        var gen1 = coordinator.BeginQuery();
        var gen2 = coordinator.BeginQuery();

        Assert.IsGreaterThan(gen1, gen2);
        Assert.IsFalse(coordinator.CanCommitQuery(gen1));
        Assert.IsTrue(coordinator.CanCommitQuery(gen2));

        var files1 = new[] { CreateTestFile(1, "file1.txt", true) };
        var committedOld = coordinator.CommitQuery(gen1, files1);
        Assert.IsFalse(committedOld);
        Assert.IsEmpty(coordinator.CurrentFiles);

        var files2 = new[] { CreateTestFile(2, "file2.txt", true) };
        var committedNew = coordinator.CommitQuery(gen2, files2);
        Assert.IsTrue(committedNew);
        Assert.HasCount(1, coordinator.CurrentFiles);
        Assert.AreEqual(2L, coordinator.CurrentFiles[0].Id);
    }

    [TestMethod]
    public void GraphGeneration_PreventsOldSnapshotOverwrite()
    {
        var coordinator = new GraphInteractionCoordinator();

        var gGen1 = coordinator.BeginGraphRefresh();
        var gGen2 = coordinator.BeginGraphRefresh();

        Assert.IsGreaterThan(gGen1, gGen2);
        Assert.IsFalse(coordinator.CanCommitSnapshot(gGen1));
        Assert.IsTrue(coordinator.CanCommitSnapshot(gGen2));

        var snapshot1 = new GraphSnapshot(GraphSnapshotStatus.Ready, 1, [new GraphFileNode("file:1", 1, "f1.txt", "文档")], [], []);
        var snapshot2 = new GraphSnapshot(GraphSnapshotStatus.Ready, 2, [new GraphFileNode("file:2", 2, "f2.txt", "图片")], [], []);

        var committedOld = coordinator.CommitSnapshot(gGen1, snapshot1);
        Assert.IsFalse(committedOld);
        Assert.IsNull(coordinator.CurrentSnapshot);

        var committedNew = coordinator.CommitSnapshot(gGen2, snapshot2);
        Assert.IsTrue(committedNew);
        Assert.AreEqual(snapshot2, coordinator.CurrentSnapshot);
    }

    [TestMethod]
    public void EvaluateSelection_ForFileNode_MatchesCurrentFile()
    {
        var coordinator = new GraphInteractionCoordinator();
        var file = CreateTestFile(10, "doc.pdf", true);
        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, [file]);

        var payload = new GraphNodeActionPayload("file:10", "file", 10, null, "doc.pdf");
        var result = coordinator.EvaluateSelection(payload);

        Assert.AreEqual(GraphSelectionKind.File, result.Kind);
        Assert.IsNotNull(result.File);
        Assert.AreEqual(10L, result.File.Id);
        Assert.AreEqual("file:10", result.NodeId);
    }

    [TestMethod]
    public void EvaluateSelection_ForStaleOrUnknownFile_ReturnsUnknown()
    {
        var coordinator = new GraphInteractionCoordinator();
        var file = CreateTestFile(10, "doc.pdf", true);
        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, [file]);

        var payload = new GraphNodeActionPayload("file:999", "file", 999, null, "ghost.pdf");
        var result = coordinator.EvaluateSelection(payload);

        Assert.AreEqual(GraphSelectionKind.Unknown, result.Kind);
        Assert.IsNull(result.File);
    }

    [TestMethod]
    public void EvaluateSelection_ForTagNode_ReturnsTagDetailsWithCorrectTypeSummary()
    {
        var coordinator = new GraphInteractionCoordinator();
        var sGen = coordinator.BeginGraphRefresh();
        var userTagNode = new GraphTagNode("tag:1", 1, "工作", GraphTagSource.User, false);
        var autoTagNode = new GraphTagNode("tag:2", 2, "类型/图片", GraphTagSource.Automatic, true);
        var snapshot = new GraphSnapshot(GraphSnapshotStatus.Ready, 0, [], [userTagNode, autoTagNode], []);
        coordinator.CommitSnapshot(sGen, snapshot);

        var userPayload = new GraphNodeActionPayload("tag:1", "tag", null, 1, "工作");
        var userResult = coordinator.EvaluateSelection(userPayload);

        Assert.AreEqual(GraphSelectionKind.Tag, userResult.Kind);
        Assert.AreEqual("工作", userResult.TagName);
        Assert.AreEqual("用户标签", userResult.TagTypeSummary);

        var autoPayload = new GraphNodeActionPayload("tag:2", "tag", null, 2, "类型/图片");
        var autoResult = coordinator.EvaluateSelection(autoPayload);

        Assert.AreEqual(GraphSelectionKind.Tag, autoResult.Kind);
        Assert.AreEqual("类型/图片", autoResult.TagName);
        Assert.AreEqual("宽泛自动标签", autoResult.TagTypeSummary);
    }

    [TestMethod]
    public void EvaluateActivation_ForLegalOnlineFile_ReturnsSuccessAndPathFromMemory()
    {
        var coordinator = new GraphInteractionCoordinator();
        var file = CreateTestFile(5, "online.txt", isOnline: true);
        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, [file]);

        var sGen = coordinator.BeginGraphRefresh();
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            1,
            [new GraphFileNode("file:5", 5, "online.txt", "文档")],
            [],
            []);
        coordinator.CommitSnapshot(sGen, snapshot);

        var payload = new GraphNodeActionPayload("file:5", "file", 5, null, "online.txt");
        var result = coordinator.EvaluateActivation(payload);

        Assert.AreEqual(GraphActivationStatus.Success, result.Status);
        Assert.IsNotNull(result.File);
        Assert.AreEqual(@"C:\Test\online.txt", result.FilePath);
        Assert.IsNull(result.ErrorMessage);
    }

    [TestMethod]
    public void EvaluateActivation_ForOfflineFile_RejectsWithOfflineStatus()
    {
        var coordinator = new GraphInteractionCoordinator();
        var file = CreateTestFile(7, "offline.txt", isOnline: false);
        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, [file]);

        var sGen = coordinator.BeginGraphRefresh();
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            1,
            [new GraphFileNode("file:7", 7, "offline.txt", "文档")],
            [],
            []);
        coordinator.CommitSnapshot(sGen, snapshot);

        var payload = new GraphNodeActionPayload("file:7", "file", 7, null, "offline.txt");
        var result = coordinator.EvaluateActivation(payload);

        Assert.AreEqual(GraphActivationStatus.RejectedOffline, result.Status);
        Assert.IsNotNull(result.File);
        Assert.IsFalse(result.File.IsOnline);
        Assert.IsNotNull(result.ErrorMessage);
        StringAssert.Contains(result.ErrorMessage, "离线");
    }

    [TestMethod]
    public void EvaluateActivation_ForTagNode_RejectsNotFile()
    {
        var coordinator = new GraphInteractionCoordinator();
        var payload = new GraphNodeActionPayload("tag:3", "tag", null, 3, "工作");
        var result = coordinator.EvaluateActivation(payload);

        Assert.AreEqual(GraphActivationStatus.RejectedNotFile, result.Status);
        Assert.IsNull(result.File);
        Assert.IsNull(result.FilePath);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public void EvaluateActivation_ForUnknownOrStaleFile_RejectsFileNotFound()
    {
        var coordinator = new GraphInteractionCoordinator();
        var file = CreateTestFile(1, "existing.txt", true);
        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, [file]);

        var sGen = coordinator.BeginGraphRefresh();
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            1,
            [new GraphFileNode("file:1", 1, "existing.txt", "文档")],
            [],
            []);
        coordinator.CommitSnapshot(sGen, snapshot);

        // Web attempts to activate fileId 999 which is not in currentFiles or snapshot
        var payload = new GraphNodeActionPayload("file:999", "file", 999, null, "unknown.txt");
        var result = coordinator.EvaluateActivation(payload);

        Assert.AreEqual(GraphActivationStatus.RejectedFileNotFound, result.Status);
        Assert.IsNull(result.File);
        Assert.IsNull(result.FilePath);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public void EvaluateActivation_NeverTrustsWebSuppliedPaths_AlwaysUsesInMemoryFile()
    {
        var coordinator = new GraphInteractionCoordinator();
        var file = CreateTestFile(100, "trusted.docx", isOnline: true);
        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, [file]);

        var sGen = coordinator.BeginGraphRefresh();
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            1,
            [new GraphFileNode("file:100", 100, "trusted.docx", "文档")],
            [],
            []);
        coordinator.CommitSnapshot(sGen, snapshot);

        // Even if an attacker tried to inject a malicious path through payload label or id:
        var payload = new GraphNodeActionPayload("file:100", "file", 100, null, @"C:\Windows\System32\cmd.exe");
        var result = coordinator.EvaluateActivation(payload);

        Assert.AreEqual(GraphActivationStatus.Success, result.Status);
        Assert.AreEqual(@"C:\Test\trusted.docx", result.FilePath);
        Assert.AreNotEqual(@"C:\Windows\System32\cmd.exe", result.FilePath);
    }

    [TestMethod]
    public void EvaluateBatchSelection_FiltersUnknownStaleAndTagIds()
    {
        var coordinator = new GraphInteractionCoordinator();
        var file1 = CreateTestFile(1, "doc1.txt", true);
        var file2 = CreateTestFile(2, "doc2.txt", true);
        var file3 = CreateTestFile(3, "doc3.txt", true);

        var qGen = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen, [file1, file2, file3]);

        var sGen = coordinator.BeginGraphRefresh();
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            3,
            [
                new GraphFileNode("file:1", 1, "doc1.txt", "文档"),
                new GraphFileNode("file:2", 2, "doc2.txt", "文档"),
                new GraphFileNode("file:3", 3, "doc3.txt", "文档")
            ],
            [
                new GraphTagNode("tag:99", 99, "工作", GraphTagSource.User, false)
            ],
            []);
        coordinator.CommitSnapshot(sGen, snapshot);

        // Box selection includes: valid file 1, valid file 2, unknown/stale file 999, and tag node ID 99
        var inputIds = new long[] { 1, 2, 999, 99 };
        var validFiles = coordinator.EvaluateBatchSelection(inputIds);

        Assert.HasCount(2, validFiles);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, validFiles.Select(f => f.Id).ToArray());
    }

    [TestMethod]
    public void EvaluateBatchSelection_RespectsGenerationConsistency()
    {
        var coordinator = new GraphInteractionCoordinator();
        var file1 = CreateTestFile(1, "doc1.txt", true);
        var qGen1 = coordinator.BeginQuery();
        coordinator.CommitQuery(qGen1, [file1]);

        // Same generation check passes
        var selectionGen1 = coordinator.EvaluateBatchSelection([1], expectedGeneration: qGen1);
        Assert.HasCount(1, selectionGen1);
        Assert.AreEqual(1L, selectionGen1[0].Id);

        // Outdated generation check returns empty
        var staleSelection = coordinator.EvaluateBatchSelection([1], expectedGeneration: qGen1 - 1);
        Assert.IsEmpty(staleSelection);

        // Start new query (increments generation)
        var qGen2 = coordinator.BeginQuery();
        var file2 = CreateTestFile(2, "doc2.txt", true);
        coordinator.CommitQuery(qGen2, [file2]);

        // Old generation fails against current coordinator state
        var oldGenSelection = coordinator.EvaluateBatchSelection([1], expectedGeneration: qGen1);
        Assert.IsEmpty(oldGenSelection);

        // File 1 is no longer in CurrentFiles
        var oldFileSelection = coordinator.EvaluateBatchSelection([1], expectedGeneration: qGen2);
        Assert.IsEmpty(oldFileSelection);

        // File 2 is valid for gen 2
        var currentSelection = coordinator.EvaluateBatchSelection([2], expectedGeneration: qGen2);
        Assert.HasCount(1, currentSelection);
        Assert.AreEqual(2L, currentSelection[0].Id);
    }

    [TestMethod]
    public void BatchTagging_AllOrNothingRollback_OnFailure()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        var tags = new TagService(database.Path);

        var userTag = tags.CreateTag("工作");

        // Attempting to batch add tag to file 1 and non-existent file 999
        Assert.Throws<ArgumentException>(() =>
            tags.AddTagToFiles(userTag.Id, [1L, 999L]));

        // Verify file 1 did NOT get the tag (complete rollback)
        var file1Tags = tags.ListTagsForFile(1);
        Assert.IsEmpty(file1Tags);

        // Attempting to batch remove tag from file 1 and non-existent file 999
        tags.AddTagToFiles(userTag.Id, [1L]);
        Assert.HasCount(1, tags.ListTagsForFile(1));

        Assert.Throws<ArgumentException>(() =>
            tags.RemoveTagFromFiles(userTag.Id, [1L, 999L]));

        // Verify file 1 STILL has the tag (rollback on remove failure)
        file1Tags = tags.ListTagsForFile(1);
        Assert.HasCount(1, file1Tags);
        Assert.AreEqual("工作", file1Tags[0].Name);
    }

    [TestMethod]
    public void BatchTagging_StrictlyRejectsAutomaticTags()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        database.Execute(
            "INSERT INTO tags (id, name, normalized_name, source) VALUES (99, 'Automatic', 'AUTOMATIC', 'automatic');");
        database.Execute(
            "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 99, 'automatic');");

        var tags = new TagService(database.Path);
        var autoTags = tags.ListAutomaticTags();
        Assert.IsNotEmpty(autoTags);
        var autoTag = autoTags[0];
        Assert.AreEqual(99, autoTag.Id);

        // Attempt to call AddTagToFiles with an automatic tag ID
        var addEx = Assert.Throws<ArgumentException>(() =>
            tags.AddTagToFiles(autoTag.Id, [2L]));
        StringAssert.Contains(addEx.Message, "用户标签");

        // Attempt to call RemoveTagFromFiles with an automatic tag ID
        var removeEx = Assert.Throws<ArgumentException>(() =>
            tags.RemoveTagFromFiles(autoTag.Id, [1L]));
        StringAssert.Contains(removeEx.Message, "用户标签");

        // Verify automatic tag on file 1 is still present and unmodified
        var autoTagsAfter = tags.ListAutomaticTagsForFile(1);
        Assert.IsTrue(autoTagsAfter.Any(t => t.Id == autoTag.Id));
    }

    [TestMethod]
    public void ListCommonUserTagsForFiles_ReturnsIntersectionOfUserTags()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        var tags = new TagService(database.Path);

        var tagA = tags.CreateTag("TagA");
        var tagB = tags.CreateTag("TagB");
        var tagC = tags.CreateTag("TagC");

        // File 1 has TagA and TagB
        tags.AddTagToFiles(tagA.Id, [1L]);
        tags.AddTagToFiles(tagB.Id, [1L]);

        // File 2 has TagA and TagC
        tags.AddTagToFiles(tagA.Id, [2L]);
        tags.AddTagToFiles(tagC.Id, [2L]);

        // File 3 has TagA
        tags.AddTagToFiles(tagA.Id, [3L]);

        // Common tags between file 1 and file 2 -> [TagA]
        var common12 = tags.ListCommonUserTagsForFiles([1L, 2L]);
        Assert.HasCount(1, common12);
        Assert.AreEqual("TagA", common12[0].Name);

        // Common tags across all 3 files -> [TagA]
        var common123 = tags.ListCommonUserTagsForFiles([1L, 2L, 3L]);
        Assert.HasCount(1, common123);
        Assert.AreEqual("TagA", common123[0].Name);

        // Single file -> returns its own user tags
        var single1 = tags.ListCommonUserTagsForFiles([1L]);
        Assert.HasCount(2, single1);

        // Empty file list -> returns empty
        var empty = tags.ListCommonUserTagsForFiles([]);
        Assert.IsEmpty(empty);
    }

    private sealed class TestDatabase : IDisposable
    {
        private TestDatabase(string path)
        {
            Path = path;
            using var _ = SqliteDatabase.Open(Path);
        }

        public string Path { get; }

        public static TestDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Tests.{Guid.NewGuid():N}.db"));

        public void SeedFiles()
        {
            Execute("INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');");
            using var connection = SqliteDatabase.Open(Path);
            using var transaction = connection.BeginTransaction();
            for (var id = 1; id <= 3; id++)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO files (
                        id, root_id, volume_id, file_id, path, normalized_path,
                        name, extension, size, modified_utc, identity_kind)
                    VALUES ($id, 1, 'volume', $fileId, $path, $path, $name, '.txt', $id, '2026-08-31T00:00:00Z', 'stable');
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$fileId", $"file-{id}");
                command.Parameters.AddWithValue("$path", $@"C:\Root\{id}.txt");
                command.Parameters.AddWithValue("$name", $"{id}.txt");
                command.ExecuteNonQuery();
            }

            transaction.Commit();
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
