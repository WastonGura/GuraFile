namespace GuraFile.Storage;

public enum GraphSnapshotStatus
{
    Ready,
    FileLimitExceeded
}

public enum GraphTagSource
{
    User,
    Automatic
}

public sealed record GraphFileNode(string Id, long FileId, string Label, string Category = "其他");

public sealed record GraphTagNode(
    string Id,
    long TagId,
    string Label,
    GraphTagSource Source,
    bool IsBroad);

public sealed record GraphEdge(string SourceId, string TargetId);

public sealed record GraphSnapshot(
    GraphSnapshotStatus Status,
    int FileCount,
    IReadOnlyList<GraphFileNode> FileNodes,
    IReadOnlyList<GraphTagNode> TagNodes,
    IReadOnlyList<GraphEdge> Edges);

public sealed class GraphSnapshotService
{
    public const int MaxFileNodes = 300;

    private readonly TagService _tags;

    public GraphSnapshotService(string databasePath)
    {
        _tags = new TagService(databasePath);
    }

    public Task<GraphSnapshot> CreateAsync(
        IReadOnlyList<IndexedFile> files,
        bool includeBroadAutomaticTags = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<GraphSnapshot>(cancellationToken);
        }

        if (files.Count > MaxFileNodes)
        {
            return Task.FromResult(new GraphSnapshot(
                GraphSnapshotStatus.FileLimitExceeded,
                files.Count,
                [],
                [],
                []));
        }

        return Task.Run(
            () => Create(files, includeBroadAutomaticTags, cancellationToken),
            cancellationToken);
    }

    private GraphSnapshot Create(
        IReadOnlyList<IndexedFile> files,
        bool includeBroadAutomaticTags,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var orderedFiles = files.OrderBy(file => file.Id).ToArray();
        if (orderedFiles.Select(file => file.Id).Distinct().Count() != orderedFiles.Length)
        {
            throw new ArgumentException("File IDs must be unique.", nameof(files));
        }

        var fileNodes = orderedFiles
            .Select(file => new GraphFileNode(
                FileNodeId(file.Id),
                file.Id,
                file.Name,
                GraphCategoryResolver.Resolve(file.Extension)))
            .ToArray();
        var relations = _tags.ListTagRelationsForFiles(
                orderedFiles.Select(file => file.Id).ToArray(),
                cancellationToken)
            .Where(relation => includeBroadAutomaticTags || !IsBroadAutomaticTag(relation))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var tagNodes = relations
            .DistinctBy(relation => relation.TagId)
            .OrderBy(relation => relation.TagId)
            .Select(relation => new GraphTagNode(
                TagNodeId(relation.TagId),
                relation.TagId,
                relation.Name,
                relation.IsAutomatic ? GraphTagSource.Automatic : GraphTagSource.User,
                IsBroadAutomaticTag(relation)))
            .ToArray();
        var edges = relations
            .OrderBy(relation => relation.FileId)
            .ThenBy(relation => relation.TagId)
            .Select(relation => new GraphEdge(FileNodeId(relation.FileId), TagNodeId(relation.TagId)))
            .ToArray();

        return new(GraphSnapshotStatus.Ready, files.Count, fileNodes, tagNodes, edges);
    }

    private static bool IsBroadAutomaticTag(FileTagRelation relation) =>
        relation.IsAutomatic && relation.Name.StartsWith("类型/", StringComparison.Ordinal);

    private static string FileNodeId(long fileId) => $"file:{fileId}";

    private static string TagNodeId(long tagId) => $"tag:{tagId}";
}
