namespace GuraFile.Storage;

public enum GraphViewDisplayMode
{
    Loading,
    Empty,
    LimitExceeded,
    Ready,
    Error
}

public sealed record GraphViewState(
    GraphViewDisplayMode Mode,
    string? Message = null,
    int FileCount = 0)
{
    public const string EmptyMessage = "当前筛选无文件";

    public static string FormatLimitExceededMessage(int count) =>
        $"当前筛选结果超过 {GraphSnapshotService.MaxFileNodes} 个文件（共 {count} 个），请收窄筛选条件以查看图谱";

    public static GraphViewState Loading() => new(GraphViewDisplayMode.Loading, "正在加载图谱…");

    public static GraphViewState Empty() => new(GraphViewDisplayMode.Empty, EmptyMessage, 0);

    public static GraphViewState LimitExceeded(int count) =>
        new(GraphViewDisplayMode.LimitExceeded, FormatLimitExceededMessage(count), count);

    public static GraphViewState Ready(int fileCount) =>
        new(GraphViewDisplayMode.Ready, null, fileCount);

    public static GraphViewState Error(string message) =>
        new(GraphViewDisplayMode.Error, $"图谱加载失败: {message}");

    public static GraphViewState FromSnapshot(GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Status == GraphSnapshotStatus.FileLimitExceeded)
        {
            return LimitExceeded(snapshot.FileCount);
        }

        if (snapshot.FileCount == 0)
        {
            return Empty();
        }

        return Ready(snapshot.FileCount);
    }
}
