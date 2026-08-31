using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class UserTagBackupServiceTests
{
    [TestMethod]
    public void ExportImportsUserTagsAndRelationsIntoANewDatabase()
    {
        using var source = TestDatabase.Create();
        source.SeedFile(1, @"C:\Old\stable.txt", "VOL-A", "FILE-A", "stable");
        source.SeedFile(2, @"C:\Path\fallback.txt", "path-fallback", "PATH-A", "path");
        var sourceTags = new TagService(source.Path);
        var work = sourceTags.CreateTag("Work");
        var personal = sourceTags.CreateTag("Personal");
        sourceTags.CreateTag("Unused");
        sourceTags.AddTagToFiles(work.Id, [1, 2]);
        sourceTags.AddTagToFiles(personal.Id, [1]);
        source.Execute("INSERT INTO tags (id, name, normalized_name, source) VALUES (99, 'Automatic', 'AUTOMATIC', 'automatic');");
        source.Execute("INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 99, 'automatic');");

        var json = new UserTagBackupService(source.Path).Export();

        Assert.IsFalse(json.Contains("Automatic", StringComparison.Ordinal));
        using var target = TestDatabase.Create();
        target.SeedFile(10, @"D:\Moved\stable.txt", "VOL-A", "FILE-A", "stable");
        target.SeedFile(20, @"C:\Path\fallback.txt", "path-fallback", "OTHER-PATH-ID", "path");

        var result = new UserTagBackupService(target.Path).Import(json);

        Assert.AreEqual(3, result.CreatedTags);
        Assert.AreEqual(3, result.RestoredRelations);
        Assert.IsEmpty(result.MissingFiles);
        Assert.AreEqual(3L, target.Scalar("SELECT COUNT(*) FROM tags WHERE source = 'user';"));
        Assert.AreEqual(3L, target.Scalar("SELECT COUNT(*) FROM file_tags WHERE source = 'user';"));
    }

    [TestMethod]
    public void ImportMatchesStableIdentityAndPathFallbackConservatively()
    {
        using var source = TestDatabase.Create();
        source.SeedFile(1, @"C:\Old\stable.txt", "VOL-A", "FILE-A", "stable");
        source.SeedFile(2, @"C:\Path\fallback.txt", "path-fallback", "PATH-A", "path");
        source.SeedFile(3, @"C:\Missing\lost.txt", "VOL-M", "FILE-M", "stable");
        var tags = new TagService(source.Path);
        var keep = tags.CreateTag("Keep");
        tags.AddTagToFiles(keep.Id, [1, 2, 3]);
        var json = new UserTagBackupService(source.Path).Export();

        using var target = TestDatabase.Create();
        target.SeedFile(10, @"D:\Moved\stable.txt", "VOL-A", "FILE-A", "stable");
        target.SeedFile(20, @"C:\Path\fallback.txt", "path-fallback", "NEW-PATH-ID", "path");
        target.SeedFile(30, @"C:\Missing\lost.txt", "VOL-REPLACEMENT", "FILE-REPLACEMENT", "stable");

        var result = new UserTagBackupService(target.Path).Import(json);

        Assert.AreEqual(2, result.RestoredRelations);
        Assert.HasCount(1, result.MissingFiles);
        Assert.AreEqual(@"C:\Missing\lost.txt", result.MissingFiles[0].Path);
        CollectionAssert.AreEquivalent(
            new long[] { 10, 20 },
            target.LongList("SELECT file_id FROM file_tags WHERE source = 'user' ORDER BY file_id;"));
    }

    [TestMethod]
    public void ImportReportsNormalizedNameConflictsAndReusesTheExistingTag()
    {
        using var source = TestDatabase.Create();
        source.SeedFile(1, @"C:\Root\a.txt", "VOL-A", "FILE-A", "stable");
        var imported = new TagService(source.Path).CreateTag("WORK");
        new TagService(source.Path).AddTagToFiles(imported.Id, [1]);
        var json = new UserTagBackupService(source.Path).Export();

        using var target = TestDatabase.Create();
        target.SeedFile(2, @"D:\Root\a.txt", "VOL-A", "FILE-A", "stable");
        var existing = new TagService(target.Path).CreateTag("Work");

        var result = new UserTagBackupService(target.Path).Import(json);

        Assert.AreEqual(0, result.CreatedTags);
        Assert.AreEqual(1, result.ReusedTags);
        Assert.HasCount(1, result.Conflicts);
        Assert.AreEqual(existing.Id, target.Scalar("SELECT tag_id FROM file_tags WHERE file_id = 2 AND source = 'user';"));
    }

    [TestMethod]
    public void PathFallbackDoesNotBindToAStableReplacementAtTheSamePath()
    {
        using var source = TestDatabase.Create();
        source.SeedFile(1, @"C:\Root\same.txt", "path-fallback", "PATH-A", "path");
        var tag = new TagService(source.Path).CreateTag("Original");
        new TagService(source.Path).AddTagToFiles(tag.Id, [1]);
        var json = new UserTagBackupService(source.Path).Export();

        using var target = TestDatabase.Create();
        target.SeedFile(2, @"C:\Root\same.txt", "VOL-NEW", "FILE-NEW", "stable");

        var result = new UserTagBackupService(target.Path).Import(json);

        Assert.AreEqual(0, result.RestoredRelations);
        Assert.HasCount(1, result.MissingFiles);
        Assert.AreEqual(0L, target.Scalar("SELECT COUNT(*) FROM file_tags;"));
    }

    [TestMethod]
    public void InvalidOrUnknownDocumentsAreRejectedBeforeWrites()
    {
        using var database = TestDatabase.Create();
        var service = new UserTagBackupService(database.Path);

        Assert.ThrowsExactly<InvalidDataException>(() => service.Import("{"));
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import("""{"format":"Other","version":1,"tags":[],"files":[]}"""));
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import("""{"format":"GuraFile.UserTags","version":2,"tags":[],"files":[]}"""));
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import("""{"format":"GuraFile.UserTags","version":1,"tags":[null],"files":[]}"""));
        var longName = new string('x', 101);
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import(
            $$"""{"format":"GuraFile.UserTags","version":1,"tags":[{"name":"{{longName}}"}],"files":[]}"""));

        Assert.AreEqual(0L, database.Scalar("SELECT COUNT(*) FROM tags;"));
    }

    [TestMethod]
    public void DatabaseFailureRollsBackTagsAndEarlierRelations()
    {
        using var source = TestDatabase.Create();
        source.SeedFile(1, @"C:\Root\a.txt", "VOL-A", "FILE-A", "stable");
        source.SeedFile(2, @"C:\Root\b.txt", "VOL-B", "FILE-B", "stable");
        var tag = new TagService(source.Path).CreateTag("Rollback");
        new TagService(source.Path).AddTagToFiles(tag.Id, [1, 2]);
        var json = new UserTagBackupService(source.Path).Export();

        using var target = TestDatabase.Create();
        target.SeedFile(1, @"D:\Root\a.txt", "VOL-A", "FILE-A", "stable");
        target.SeedFile(2, @"D:\Root\b.txt", "VOL-B", "FILE-B", "stable");
        target.Execute(
            "CREATE TRIGGER fail_second BEFORE INSERT ON file_tags WHEN NEW.file_id = 2 BEGIN SELECT RAISE(ABORT, 'forced'); END;");

        Assert.ThrowsExactly<SqliteException>(() => new UserTagBackupService(target.Path).Import(json));

        Assert.AreEqual(0L, target.Scalar("SELECT COUNT(*) FROM tags;"));
        Assert.AreEqual(0L, target.Scalar("SELECT COUNT(*) FROM file_tags;"));
    }

    [TestMethod]
    public void ExportRejectsStructuresThatItsImporterWouldReject()
    {
        using var database = TestDatabase.Create();
        database.SeedFile(1, @"C:\Root\many.txt", "VOL-A", "FILE-A", "stable");
        database.Execute(
            """
            WITH RECURSIVE numbers(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 1001
            )
            INSERT INTO tags (id, name, normalized_name, source)
            SELECT value, 'Tag-' || value, 'TAG-' || value, 'user' FROM numbers;

            INSERT INTO file_tags (file_id, tag_id, source)
            SELECT 1, id, 'user' FROM tags;
            """);

        Assert.ThrowsExactly<InvalidDataException>(() => new UserTagBackupService(database.Path).Export());
    }

    private sealed class TestDatabase : IDisposable
    {
        private bool _hasRoot;

        private TestDatabase(string path)
        {
            Path = path;
            using var _ = SqliteDatabase.Open(path);
        }

        public string Path { get; }

        public static TestDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Tests.{Guid.NewGuid():N}.db"));

        public void SeedFile(long id, string path, string volumeId, string fileId, string identityKind)
        {
            if (!_hasRoot)
            {
                Execute("INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'C:\\Root');");
                _hasRoot = true;
            }

            Execute(
                """
                INSERT INTO files (
                    id, root_id, volume_id, file_id, path, normalized_path,
                    name, extension, size, modified_utc, identity_kind, is_online)
                VALUES ($id, 1, $volumeId, $fileId, $path, $path, $name, '.txt', 1, '2026-09-01T00:00:00Z', $identityKind, 1);
                """,
                ("$id", id),
                ("$volumeId", volumeId),
                ("$fileId", fileId),
                ("$path", path),
                ("$name", System.IO.Path.GetFileName(path)),
                ("$identityKind", identityKind));
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

        public long Scalar(string sql)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }

        public long[] LongList(string sql)
        {
            using var connection = SqliteDatabase.Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var values = new List<long>();
            while (reader.Read())
            {
                values.Add(reader.GetInt64(0));
            }

            return values.ToArray();
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
