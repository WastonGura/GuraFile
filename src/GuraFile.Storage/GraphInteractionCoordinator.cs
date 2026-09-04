namespace GuraFile.Storage;

public enum GraphSelectionKind
{
    File,
    Tag,
    Unknown
}

public sealed record GraphSelectionResult(
    GraphSelectionKind Kind,
    IndexedFile? File = null,
    GraphTagNode? Tag = null,
    string? TagName = null,
    string? TagTypeSummary = null,
    string? NodeId = null);

public enum GraphActivationStatus
{
    Success,
    RejectedNotFile,
    RejectedFileNotFound,
    RejectedOffline
}

public sealed record GraphActivationResult(
    GraphActivationStatus Status,
    IndexedFile? File = null,
    string? FilePath = null,
    string? ErrorMessage = null);

public sealed class GraphInteractionCoordinator
{
    private long _queryGeneration;
    private long _graphGeneration;
    private readonly object _lock = new();

    public long CurrentQueryGeneration => Volatile.Read(ref _queryGeneration);
    public long CurrentGraphGeneration => Volatile.Read(ref _graphGeneration);

    public IReadOnlyList<IndexedFile> CurrentFiles { get; private set; } = [];
    public GraphSnapshot? CurrentSnapshot { get; private set; }

    public long BeginQuery() => Interlocked.Increment(ref _queryGeneration);

    public bool CanCommitQuery(long generation) => generation == Volatile.Read(ref _queryGeneration);

    public bool CommitQuery(long generation, IReadOnlyList<IndexedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        lock (_lock)
        {
            if (generation != _queryGeneration)
            {
                return false;
            }

            CurrentFiles = files;
            return true;
        }
    }

    public long BeginGraphRefresh() => Interlocked.Increment(ref _graphGeneration);

    public bool CanCommitSnapshot(long generation) => generation == Volatile.Read(ref _graphGeneration);

    public bool CommitSnapshot(long generation, GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock)
        {
            if (generation != _graphGeneration)
            {
                return false;
            }

            CurrentSnapshot = snapshot;
            return true;
        }
    }

    public GraphSelectionResult EvaluateSelection(GraphNodeActionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.Equals(payload.Kind, "file", StringComparison.OrdinalIgnoreCase))
        {
            if (payload.FileId is null)
            {
                return new GraphSelectionResult(GraphSelectionKind.Unknown, NodeId: payload.NodeId);
            }

            var file = CurrentFiles.FirstOrDefault(f => f.Id == payload.FileId.Value);
            if (file is null)
            {
                return new GraphSelectionResult(GraphSelectionKind.Unknown, NodeId: payload.NodeId);
            }

            return new GraphSelectionResult(GraphSelectionKind.File, File: file, NodeId: payload.NodeId);
        }

        if (string.Equals(payload.Kind, "tag", StringComparison.OrdinalIgnoreCase))
        {
            GraphTagNode? tagNode = null;
            if (CurrentSnapshot is not null)
            {
                tagNode = CurrentSnapshot.TagNodes.FirstOrDefault(t =>
                    t.Id == payload.NodeId || (payload.TagId.HasValue && t.TagId == payload.TagId.Value));
            }

            string typeSummary;
            if (tagNode is not null)
            {
                typeSummary = tagNode.Source == GraphTagSource.Automatic
                    ? (tagNode.IsBroad ? "宽泛自动标签" : "自动分类标签")
                    : "用户标签";
            }
            else
            {
                typeSummary = "标签";
            }

            var tagName = tagNode?.Label ?? payload.Label ?? "未知标签";
            return new GraphSelectionResult(
                GraphSelectionKind.Tag,
                Tag: tagNode,
                TagName: tagName,
                TagTypeSummary: typeSummary,
                NodeId: payload.NodeId);
        }

        return new GraphSelectionResult(GraphSelectionKind.Unknown, NodeId: payload.NodeId);
    }

    public IReadOnlyList<IndexedFile> EvaluateBatchSelection(IReadOnlyList<long>? fileIds, long? expectedGeneration = null)
    {
        if (expectedGeneration.HasValue && expectedGeneration.Value != Volatile.Read(ref _queryGeneration))
        {
            return [];
        }

        if (fileIds is null || fileIds.Count == 0)
        {
            return [];
        }

        lock (_lock)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _queryGeneration)
            {
                return [];
            }

            var distinctIds = fileIds.Distinct().ToHashSet();
            var snapshotFileIds = CurrentSnapshot is not null
                ? CurrentSnapshot.FileNodes.Select(f => f.FileId).ToHashSet()
                : null;

            var validFiles = new List<IndexedFile>();
            foreach (var file in CurrentFiles)
            {
                if (distinctIds.Contains(file.Id))
                {
                    if (snapshotFileIds is null || snapshotFileIds.Contains(file.Id))
                    {
                        validFiles.Add(file);
                    }
                }
            }

            return validFiles;
        }
    }

    public GraphActivationResult EvaluateActivation(GraphNodeActionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!string.Equals(payload.Kind, "file", StringComparison.OrdinalIgnoreCase))
        {
            return new GraphActivationResult(GraphActivationStatus.RejectedNotFile, ErrorMessage: "仅支持打开文件节点。");
        }

        if (payload.FileId is null)
        {
            return new GraphActivationResult(GraphActivationStatus.RejectedFileNotFound, ErrorMessage: "文件节点未包含有效文件 ID。");
        }

        var file = CurrentFiles.FirstOrDefault(f => f.Id == payload.FileId.Value);
        if (file is null)
        {
            return new GraphActivationResult(GraphActivationStatus.RejectedFileNotFound, ErrorMessage: "未在当前文件快照中找到该文件。");
        }

        if (CurrentSnapshot is not null && !CurrentSnapshot.FileNodes.Any(f => f.FileId == payload.FileId.Value))
        {
            return new GraphActivationResult(GraphActivationStatus.RejectedFileNotFound, ErrorMessage: "未在当前图谱快照中找到该文件。");
        }

        if (!file.IsOnline)
        {
            return new GraphActivationResult(
                GraphActivationStatus.RejectedOffline,
                File: file,
                FilePath: file.Path,
                ErrorMessage: $"文件“{file.Name}”处于离线状态，无法打开。");
        }

        return new GraphActivationResult(
            GraphActivationStatus.Success,
            File: file,
            FilePath: file.Path);
    }
}
