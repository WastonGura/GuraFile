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
}
