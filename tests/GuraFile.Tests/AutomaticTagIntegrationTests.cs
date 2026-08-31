using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class AutomaticTagIntegrationTests
{
    [TestMethod]
    public async Task ScanReclassifiesOnlyChangedMetadataAndKeepsUserTags()
    {
        using var temp = TempDirectory.Create();
        var originalPath = Path.Combine(temp.RootPath, "notes.txt");
        await File.WriteAllTextAsync(originalPath, "notes");
        var calls = 0;
        var scanner = new ManagedRootScanner(
            temp.DatabasePath,
            _ => new("volume", "stable-file", true, null),
            Directory.GetFileSystemEntries,
            File.GetAttributes,
            path =>
            {
                calls++;
                return Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
                    ? Classification("类型/文档", "格式/Markdown")
                    : Classification("类型/文档", "格式/TXT");
            });
        var root = scanner.AddRoot(temp.RootPath);

        await scanner.ScanAsync(root.Id);
        await scanner.ScanAsync(root.Id);
        var indexed = (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single();
        var tags = new TagService(temp.DatabasePath);
        var userTag = tags.CreateTag("Keep me");
        tags.AddTagToFiles(userTag.Id, [indexed.Id]);
        var renamedPath = Path.Combine(temp.RootPath, "notes.md");
        File.Move(originalPath, renamedPath);

        await scanner.ScanAsync(root.Id);

        Assert.AreEqual(2, calls, "An unchanged second scan reread the file header.");
        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/Markdown" },
            tags.ListAutomaticTagsForFile(indexed.Id).Select(tag => tag.Name).ToArray());
        Assert.AreEqual("Keep me", tags.ListTagsForFile(indexed.Id).Single().Name);
    }

    [TestMethod]
    public void ManualReclassificationAtomicallyReplacesOnlyAutomaticRelations()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.RootPath, "notes.txt");
        File.WriteAllText(filePath, "notes");
        var fileId = temp.SeedFile(filePath);
        var results = new Queue<FileTypeClassification>(
        [
            Classification("类型/文档", "格式/TXT"),
            Classification("类型/文档", "格式/Markdown"),
            Classification(new string('x', 101), "格式/INVALID")
        ]);
        var tags = new TagService(
            temp.DatabasePath,
            _ => results.Dequeue(),
            _ => new("volume", "file", true, null));
        var userTag = tags.CreateTag("User");
        tags.AddTagToFiles(userTag.Id, [fileId]);

        tags.ReclassifyFile(fileId);
        tags.ReclassifyFile(fileId);
        Assert.ThrowsExactly<ArgumentException>(() => tags.ReclassifyFile(fileId));

        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/Markdown" },
            tags.ListAutomaticTagsForFile(fileId).Select(tag => tag.Name).ToArray());
        Assert.AreEqual("User", tags.ListTagsForFile(fileId).Single().Name);
    }

    [TestMethod]
    public void ManualReclassificationRejectsOfflineNode()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.RootPath, "offline.txt");
        var fileId = temp.SeedFile(filePath);
        using (var connection = SqliteDatabase.Open(temp.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE files SET is_online = 0 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", fileId);
            command.ExecuteNonQuery();
        }

        var tags = new TagService(
            temp.DatabasePath,
            _ => Classification("类型/文档", "格式/TXT"),
            _ => new("volume", "file", true, null));

        Assert.ThrowsExactly<InvalidOperationException>(() => tags.ReclassifyFile(fileId));
        Assert.IsEmpty(tags.ListAutomaticTagsForFile(fileId));
    }

    [TestMethod]
    public void ManualReclassificationRejectsReplacedFileIdentity()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.RootPath, "replaced.txt");
        var fileId = temp.SeedFile(filePath, "old-file");
        var tags = new TagService(
            temp.DatabasePath,
            _ => Classification("类型/文档", "格式/TXT"),
            _ => new("volume", "new-file", true, null));

        Assert.ThrowsExactly<InvalidOperationException>(() => tags.ReclassifyFile(fileId));
        Assert.IsEmpty(tags.ListAutomaticTagsForFile(fileId));
    }

    [TestMethod]
    public void ManualReclassificationRollsBackWhenIdentityChangesDuringClassification()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.RootPath, "racing.txt");
        var fileId = temp.SeedFile(filePath, "old-file");
        new TagService(
            temp.DatabasePath,
            _ => Classification("类型/文档", "格式/TXT"),
            _ => new("volume", "old-file", true, null)).ReclassifyFile(fileId);
        var identities = new Queue<FileIdentity>(
        [
            new("volume", "old-file", true, null),
            new("volume", "new-file", true, null)
        ]);
        var tags = new TagService(
            temp.DatabasePath,
            _ => Classification("类型/文档", "格式/Markdown"),
            _ => identities.Dequeue());

        Assert.ThrowsExactly<InvalidOperationException>(() => tags.ReclassifyFile(fileId));
        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/TXT" },
            tags.ListAutomaticTagsForFile(fileId).Select(tag => tag.Name).ToArray());
    }

    [TestMethod]
    public async Task AutomaticTagsCanFilterFiles()
    {
        using var temp = TempDirectory.Create();
        var firstId = temp.SeedFile(Path.Combine(temp.RootPath, "first.txt"), "first");
        temp.SeedFile(Path.Combine(temp.RootPath, "second.bin"), "second");
        var tags = new TagService(
            temp.DatabasePath,
            _ => Classification("类型/文档", "格式/TXT"),
            _ => new("volume", "first", true, null));
        tags.ReclassifyFile(firstId);
        var automaticTag = tags.ListAutomaticTags().Single(tag => tag.Name == "类型/文档");

        var result = await new FileQueryService(temp.DatabasePath).QueryAsync(
            new(TagIds: [automaticTag.Id], TagMatch: TagMatchMode.All));

        CollectionAssert.AreEqual(new[] { firstId }, result.Select(file => file.Id).ToArray());
    }

    [TestMethod]
    public async Task ScanRollsBackMetadataAndAutomaticTagsTogether()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.RootPath, "notes.txt");
        await File.WriteAllTextAsync(filePath, "old");
        var scanner = new ManagedRootScanner(
            temp.DatabasePath,
            _ => new("volume", "stable-file", true, null),
            Directory.GetFileSystemEntries,
            File.GetAttributes,
            _ => Classification("类型/文档", "格式/TXT"));
        var root = scanner.AddRoot(temp.RootPath);
        await scanner.ScanAsync(root.Id);
        var original = (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single();
        await File.AppendAllTextAsync(filePath, "-changed");
        var failingScanner = new ManagedRootScanner(
            temp.DatabasePath,
            _ => new("volume", "stable-file", true, null),
            Directory.GetFileSystemEntries,
            File.GetAttributes,
            _ => Classification("类型/文档", new string('x', 101)));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => failingScanner.ScanAsync(root.Id));

        Assert.AreEqual(original.Size, (await new FileQueryService(temp.DatabasePath).QueryAsync(new())).Single().Size);
        CollectionAssert.AreEquivalent(
            new[] { "类型/文档", "格式/TXT" },
            new TagService(temp.DatabasePath).ListAutomaticTagsForFile(original.Id).Select(tag => tag.Name).ToArray());
    }

    [TestMethod]
    public async Task ClassificationFailureDoesNotBlockFileCommit()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.RootPath, "locked.bin");
        await File.WriteAllTextAsync(filePath, "content");
        var scanner = new ManagedRootScanner(
            temp.DatabasePath,
            _ => new("volume", "locked", true, null),
            Directory.GetFileSystemEntries,
            File.GetAttributes,
            _ => throw new InvalidDataException("classifier failed"));
        var root = scanner.AddRoot(temp.RootPath);

        var result = await scanner.ScanAsync(root.Id);

        Assert.AreEqual(1, result.CommittedFiles);
        Assert.HasCount(1, result.Failures);
        StringAssert.Contains(result.Failures[0].Error, "classifier failed");
        Assert.HasCount(1, await new FileQueryService(temp.DatabasePath).QueryAsync(new()));
    }

    private static FileTypeClassification Classification(string type, string format) =>
        new(type, format, null, false, null);

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
            RootPath = System.IO.Path.Combine(path, "root");
            DatabasePath = System.IO.Path.Combine(path, "index.db");
            Directory.CreateDirectory(RootPath);
            using var _ = SqliteDatabase.Open(DatabasePath);
        }

        public string Path { get; }
        public string RootPath { get; }
        public string DatabasePath { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"GuraFile.AutomaticTags.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
        }

        public long SeedFile(string path, string identity = "file")
        {
            File.WriteAllText(path, "content");
            using var connection = SqliteDatabase.Open(DatabasePath);
            using var root = connection.CreateCommand();
            root.CommandText =
                "INSERT INTO roots (path, normalized_path) VALUES ($path, $path) ON CONFLICT DO NOTHING;";
            root.Parameters.AddWithValue("$path", RootPath);
            root.ExecuteNonQuery();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO files (
                    root_id, volume_id, file_id, path, normalized_path,
                    name, extension, size, modified_utc, identity_kind)
                VALUES (
                    (SELECT id FROM roots WHERE normalized_path = $rootPath),
                    'volume', $identity, $path, $path, $name, $extension, 7,
                    '2026-09-01T00:00:00Z', 'stable')
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$rootPath", RootPath);
            command.Parameters.AddWithValue("$identity", identity);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$name", System.IO.Path.GetFileName(path));
            command.Parameters.AddWithValue("$extension", System.IO.Path.GetExtension(path));
            return (long)command.ExecuteScalar()!;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
