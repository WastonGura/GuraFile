using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class GraphSnapshotServiceTests
{
    [TestMethod]
    public async Task EmptyInputCreatesEmptySnapshot()
    {
        using var database = TestDatabase.Create();

        var snapshot = await new GraphSnapshotService(database.Path).CreateAsync([]);

        Assert.AreEqual(GraphSnapshotStatus.Ready, snapshot.Status);
        Assert.AreEqual(0, snapshot.FileCount);
        Assert.IsEmpty(snapshot.FileNodes);
        Assert.IsEmpty(snapshot.TagNodes);
        Assert.IsEmpty(snapshot.Edges);
    }

    [TestMethod]
    public async Task SnapshotUsesStablePrefixedIdsAndPreservesHostileTextAsData()
    {
        using var database = TestDatabase.Create();
        const string fileName = "</script>\n\"file\".txt";
        const string tagName = "tag\n<script>alert('x')</script>";
        database.AddFile(2, fileName);
        database.AddFile(1, "first.txt");
        database.AddTag(10, tagName, "user", 2);
        var files = new[] { File(2, fileName), File(1, "first.txt") };
        var service = new GraphSnapshotService(database.Path);

        var first = await service.CreateAsync(files);
        var second = await service.CreateAsync(files.Reverse().ToArray());

        CollectionAssert.AreEqual(new long[] { 1, 2 }, first.FileNodes.Select(node => node.FileId).ToArray());
        CollectionAssert.AreEqual(new[] { "file:1", "file:2" }, first.FileNodes.Select(node => node.Id).ToArray());
        Assert.AreEqual(fileName, first.FileNodes[1].Label);
        Assert.AreEqual(tagName, first.TagNodes.Single().Label);
        Assert.AreEqual("tag:10", first.TagNodes.Single().Id);
        CollectionAssert.AreEqual(first.FileNodes.ToArray(), second.FileNodes.ToArray());
        CollectionAssert.AreEqual(first.TagNodes.ToArray(), second.TagNodes.ToArray());
        CollectionAssert.AreEqual(first.Edges.ToArray(), second.Edges.ToArray());
    }

    [TestMethod]
    public async Task ThreeHundredFilesAreAcceptedButThreeHundredOneReturnOnlyLimitStatus()
    {
        using var database = TestDatabase.Create();
        var service = new GraphSnapshotService(database.Path);
        var threeHundred = Enumerable.Range(1, 300).Select(id => File(id, $"{id}.txt")).ToArray();

        var accepted = await service.CreateAsync(threeHundred);
        var rejected = await service.CreateAsync([.. threeHundred, File(301, "301.txt")]);

        Assert.AreEqual(GraphSnapshotStatus.Ready, accepted.Status);
        Assert.HasCount(300, accepted.FileNodes);
        Assert.AreEqual(GraphSnapshotStatus.FileLimitExceeded, rejected.Status);
        Assert.AreEqual(301, rejected.FileCount);
        Assert.IsEmpty(rejected.FileNodes);
        Assert.IsEmpty(rejected.TagNodes);
        Assert.IsEmpty(rejected.Edges);
    }

    [TestMethod]
    public async Task BroadAutomaticTagsAreExcludedByDefaultAndMarkedWhenIncluded()
    {
        using var database = TestDatabase.Create();
        database.AddFile(1, "image.png");
        database.AddTag(10, "类型/图片", "automatic", 1);
        database.AddTag(11, "格式/PNG", "automatic", 1);
        database.AddTag(12, "类型/自定义", "user", 1);
        var service = new GraphSnapshotService(database.Path);

        var defaults = await service.CreateAsync([File(1, "image.png")]);
        var included = await service.CreateAsync([File(1, "image.png")], includeBroadAutomaticTags: true);

        CollectionAssert.AreEqual(new long[] { 11, 12 }, defaults.TagNodes.Select(node => node.TagId).ToArray());
        var broad = included.TagNodes.Single(node => node.TagId == 10);
        Assert.AreEqual(GraphTagSource.Automatic, broad.Source);
        Assert.IsTrue(broad.IsBroad);
        Assert.IsFalse(included.TagNodes.Single(node => node.TagId == 12).IsBroad);
    }

    [TestMethod]
    public async Task SharedTagsCreateOnlyFileToTagEdges()
    {
        using var database = TestDatabase.Create();
        database.AddFile(1, "one.txt");
        database.AddFile(2, "two.txt");
        database.AddTag(10, "Shared", "user", 1, 2);

        var snapshot = await new GraphSnapshotService(database.Path).CreateAsync(
            [File(2, "two.txt"), File(1, "one.txt")]);

        Assert.HasCount(1, snapshot.TagNodes);
        CollectionAssert.AreEqual(
            new[] { new GraphEdge("file:1", "tag:10"), new GraphEdge("file:2", "tag:10") },
            snapshot.Edges.ToArray());
        Assert.IsTrue(snapshot.Edges.All(edge => edge.SourceId.StartsWith("file:", StringComparison.Ordinal)));
        Assert.IsTrue(snapshot.Edges.All(edge => edge.TargetId.StartsWith("tag:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PreCanceledCreationIsCanceled()
    {
        using var database = TestDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            new GraphSnapshotService(database.Path).CreateAsync([], cancellationToken: cancellation.Token));
    }

    private static IndexedFile File(long id, string name) =>
        new(id, name, $@"C:\Root\{name}", Path.GetExtension(name), 0, DateTimeOffset.UnixEpoch, true, null);

    private sealed class TestDatabase : IDisposable
    {
        private TestDatabase(string path)
        {
            Path = path;
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');";
            command.ExecuteNonQuery();
        }

        public string Path { get; }

        public static TestDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Tests.{Guid.NewGuid():N}.db"));

        public void AddFile(long id, string name)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO files (
                    id, root_id, volume_id, file_id, path, normalized_path,
                    name, extension, size, modified_utc, identity_kind)
                VALUES ($id, 1, 'volume', $identity, $path, $path, $name, '.txt', 0, '1970-01-01T00:00:00Z', 'stable');
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$identity", $"file-{id}");
            command.Parameters.AddWithValue("$path", $@"C:\Root\{id}.txt");
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }

        public void AddTag(long id, string name, string source, params long[] fileIds)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var transaction = connection.BeginTransaction();
            using (var tag = connection.CreateCommand())
            {
                tag.Transaction = transaction;
                tag.CommandText =
                    "INSERT INTO tags (id, name, normalized_name, source) VALUES ($id, $name, $normalizedName, $source);";
                tag.Parameters.AddWithValue("$id", id);
                tag.Parameters.AddWithValue("$name", name);
                tag.Parameters.AddWithValue("$normalizedName", $"TAG-{id}");
                tag.Parameters.AddWithValue("$source", source);
                tag.ExecuteNonQuery();
            }

            foreach (var fileId in fileIds)
            {
                using var relation = connection.CreateCommand();
                relation.Transaction = transaction;
                relation.CommandText =
                    "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fileId, $tagId, $source);";
                relation.Parameters.AddWithValue("$fileId", fileId);
                relation.Parameters.AddWithValue("$tagId", id);
                relation.Parameters.AddWithValue("$source", source);
                relation.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void Dispose()
        {
            foreach (var path in new[] { Path, $"{Path}-shm", $"{Path}-wal" })
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
        }
    }
}
