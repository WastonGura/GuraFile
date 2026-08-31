using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class TagServiceTests
{
    [TestMethod]
    public void CreateTagPersistsTrimmedName()
    {
        using var database = TestDatabase.Create();

        var tag = new TagService(database.Path).CreateTag("  Work  ");

        Assert.AreEqual("Work", tag.Name);
        Assert.AreEqual("Work", new TagService(database.Path).ListTags().Single().Name);
    }

    [TestMethod]
    public void EquivalentAndEmptyNamesAreRejected()
    {
        using var database = TestDatabase.Create();
        var service = new TagService(database.Path);
        service.CreateTag("Café");

        Assert.ThrowsExactly<InvalidOperationException>(() => service.CreateTag(" cafe\u0301 "));
        Assert.ThrowsExactly<ArgumentException>(() => service.CreateTag("   "));
        Assert.ThrowsExactly<ArgumentException>(() => service.CreateTag(new string('x', 101)));
    }

    [TestMethod]
    public void RenameRejectsConflictWithoutChangingEitherTag()
    {
        using var database = TestDatabase.Create();
        var service = new TagService(database.Path);
        service.CreateTag("Work");
        var personal = service.CreateTag("Personal");

        Assert.ThrowsExactly<InvalidOperationException>(() => service.RenameTag(personal.Id, "WORK"));

        CollectionAssert.AreEquivalent(
            new[] { "Personal", "Work" },
            service.ListTags().Select(tag => tag.Name).ToArray());
    }

    [TestMethod]
    public void BatchAddRollsBackWhenAnyFileIsMissing()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        var service = new TagService(database.Path);
        var tag = service.CreateTag("Keep");

        Assert.ThrowsExactly<ArgumentException>(() => service.AddTagToFiles(tag.Id, [1, 999]));

        Assert.AreEqual(0L, database.Scalar("SELECT COUNT(*) FROM file_tags;"));
    }

    [TestMethod]
    public void UserRelationsAreIdempotentAndDoNotRemoveAutomaticRelations()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        var service = new TagService(database.Path);
        var tag = service.CreateTag("Document");
        database.Execute(
            "INSERT INTO tags (id, name, normalized_name, source) VALUES (99, 'Document', 'DOCUMENT', 'automatic');");
        database.Execute(
            "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 99, 'automatic');");

        service.AddTagToFiles(tag.Id, [1, 1]);
        service.AddTagToFiles(tag.Id, [1]);
        service.RemoveTagFromFiles(tag.Id, [1]);

        Assert.AreEqual(0L, database.Scalar(
            "SELECT COUNT(*) FROM file_tags WHERE file_id = 1 AND tag_id = $tagId AND source = 'user';",
            ("$tagId", tag.Id)));
        Assert.AreEqual(1L, database.Scalar(
            "SELECT COUNT(*) FROM file_tags WHERE file_id = 1 AND tag_id = 99 AND source = 'automatic';"));
    }

    [TestMethod]
    public void UserOperationsCannotChangeAutomaticTags()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        database.Execute(
            "INSERT INTO tags (id, name, normalized_name, source) VALUES (99, 'Automatic', 'AUTOMATIC', 'automatic');");

        var service = new TagService(database.Path);

        Assert.IsEmpty(service.ListTags());
        Assert.ThrowsExactly<ArgumentException>(() => service.RenameTag(99, "Changed"));
        Assert.IsFalse(service.DeleteTag(99));
        Assert.ThrowsExactly<ArgumentException>(() => service.AddTagToFiles(99, [1]));
        Assert.AreEqual("Automatic", database.TextScalar("SELECT name FROM tags WHERE id = 99;"));
    }

    [TestMethod]
    public void DeleteTagKeepsFilesAndOtherTags()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        var service = new TagService(database.Path);
        var remove = service.CreateTag("Remove");
        var keep = service.CreateTag("Keep");
        service.AddTagToFiles(remove.Id, [1]);
        service.AddTagToFiles(keep.Id, [1]);

        Assert.IsTrue(service.DeleteTag(remove.Id));

        Assert.AreEqual(3L, database.Scalar("SELECT COUNT(*) FROM files;"));
        Assert.AreEqual(1L, database.Scalar("SELECT COUNT(*) FROM tags;"));
        Assert.AreEqual(1L, database.Scalar(
            "SELECT COUNT(*) FROM file_tags WHERE file_id = 1 AND tag_id = $tagId AND source = 'user';",
            ("$tagId", keep.Id)));
    }

    [TestMethod]
    public void TagsAndRelationsSurviveReopen()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        var tag = new TagService(database.Path).CreateTag("Persistent");
        new TagService(database.Path).AddTagToFiles(tag.Id, [2]);

        var reopened = new TagService(database.Path);

        Assert.AreEqual("Persistent", reopened.ListTags().Single().Name);
        Assert.AreEqual(tag.Id, reopened.ListTagsForFile(2).Single().Id);
    }

    [TestMethod]
    public async Task AnyAndAllTagFiltersHaveDefinedResults()
    {
        using var database = TestDatabase.Create();
        database.SeedFiles();
        var tags = new TagService(database.Path);
        var alpha = tags.CreateTag("Alpha");
        var beta = tags.CreateTag("Beta");
        tags.AddTagToFiles(alpha.Id, [1, 2]);
        tags.AddTagToFiles(beta.Id, [2, 3]);
        var files = new FileQueryService(database.Path);

        var any = await files.QueryAsync(new(TagIds: [alpha.Id, beta.Id], TagMatch: TagMatchMode.Any));
        var all = await files.QueryAsync(new(TagIds: [alpha.Id, beta.Id], TagMatch: TagMatchMode.All));
        var emptyAny = await files.QueryAsync(new(TagIds: [], TagMatch: TagMatchMode.Any));
        var emptyAll = await files.QueryAsync(new(TagIds: [], TagMatch: TagMatchMode.All));

        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, any.Select(file => file.Id).ToArray());
        CollectionAssert.AreEqual(new long[] { 2 }, all.Select(file => file.Id).ToArray());
        Assert.IsEmpty(emptyAny);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, emptyAll.Select(file => file.Id).ToArray());
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

        public long Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return (long)command.ExecuteScalar()!;
        }

        public string TextScalar(string sql)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)command.ExecuteScalar()!;
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
