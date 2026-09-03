using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class GraphViewStateTests
{
    [TestMethod]
    public void EmptyFilesProducesEmptyState()
    {
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            0,
            [],
            [],
            []);

        var state = GraphViewState.FromSnapshot(snapshot);

        Assert.AreEqual(GraphViewDisplayMode.Empty, state.Mode);
        Assert.AreEqual("当前筛选无文件", state.Message);
        Assert.AreEqual(0, state.FileCount);
    }

    [TestMethod]
    public void FileLimitExceededProducesSpecificLimitMessageAndZeroNodes()
    {
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.FileLimitExceeded,
            301,
            [],
            [],
            []);

        var state = GraphViewState.FromSnapshot(snapshot);

        Assert.AreEqual(GraphViewDisplayMode.LimitExceeded, state.Mode);
        Assert.AreEqual("当前筛选结果超过 300 个文件（共 301 个），请收窄筛选条件以查看图谱", state.Message);
        Assert.AreEqual(301, state.FileCount);
    }

    [TestMethod]
    public void ValidSnapshotProducesReadyState()
    {
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            5,
            [new GraphFileNode("file:1", 1, "test.txt", "文档")],
            [],
            []);

        var state = GraphViewState.FromSnapshot(snapshot);

        Assert.AreEqual(GraphViewDisplayMode.Ready, state.Mode);
        Assert.IsNull(state.Message);
        Assert.AreEqual(5, state.FileCount);
    }

    [TestMethod]
    public void ErrorStateFormatsMessageCorrectly()
    {
        var state = GraphViewState.Error("WebView2 failed to load");

        Assert.AreEqual(GraphViewDisplayMode.Error, state.Mode);
        Assert.AreEqual("图谱加载失败: WebView2 failed to load", state.Message);
    }

    [TestMethod]
    public void LoadingStateFormatsMessageCorrectly()
    {
        var state = GraphViewState.Loading();

        Assert.AreEqual(GraphViewDisplayMode.Loading, state.Mode);
        Assert.AreEqual("正在加载图谱…", state.Message);
    }
}
