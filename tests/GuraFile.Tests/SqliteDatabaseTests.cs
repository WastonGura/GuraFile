using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class SqliteDatabaseTests
{
    [TestMethod]
    public void NewDatabaseReachesCurrentSchema()
    {
        using var database = TempDatabase.Create();
        using var connection = SqliteDatabase.Open(database.Path);

        Assert.AreEqual(SqliteDatabase.CurrentVersion, Scalar<long>(connection, "PRAGMA user_version;"));
        Assert.AreEqual("wal", Scalar<string>(connection, "PRAGMA journal_mode;"));
        Assert.AreEqual(1L, Scalar<long>(connection, "PRAGMA foreign_keys;"));
        Assert.AreEqual(4L, Scalar<long>(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('roots', 'files', 'tags', 'file_tags');"));
        Assert.AreEqual(4L, Scalar<long>(connection,
            "SELECT COUNT(*) FROM pragma_table_info('files') WHERE name IN ('name', 'extension', 'size', 'modified_utc');"));
        Assert.AreEqual(2L, Scalar<long>(connection,
            "SELECT COUNT(*) FROM pragma_table_info('files') WHERE name IN ('identity_kind', 'is_online');"));
    }

    [TestMethod]
    public void ReopenPreservesData()
    {
        using var database = TempDatabase.Create();

        using (var connection = SqliteDatabase.Open(database.Path))
        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection,
                "INSERT INTO roots (path, normalized_path) VALUES ($path, $normalizedPath);",
                transaction,
                ("$path", @"C:\Users\Example"),
                ("$normalizedPath", @"c:\users\example"));
            transaction.Commit();
        }

        using var reopened = SqliteDatabase.Open(database.Path);
        Assert.AreEqual(@"C:\Users\Example", Scalar<string>(reopened, "SELECT path FROM roots;"));
        Assert.AreEqual(SqliteDatabase.CurrentVersion, Scalar<long>(reopened, "PRAGMA user_version;"));
    }

    [TestMethod]
    public void SchemaRejectsDuplicateIdentitiesPathsTagsAndRelations()
    {
        using var database = TempDatabase.Create();
        using var connection = SqliteDatabase.Open(database.Path);

        Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'c:\\root');");
        Execute(connection, "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) VALUES (1, 1, 'volume-a', 'file-a', 'C:\\Root\\a.txt', 'c:\\root\\a.txt', 'a.txt', '.txt', 12, '2026-08-31T00:00:00Z');");
        Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (1, 'Work', 'work');");
        Execute(connection, "INSERT INTO file_tags (file_id, tag_id) VALUES (1, 1);");
        Assert.AreEqual("user", Scalar<string>(connection, "SELECT source FROM file_tags WHERE file_id = 1 AND tag_id = 1;"));
        Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'automatic');");
        Assert.AreEqual(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE file_id = 1 AND tag_id = 1;"));

        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "INSERT INTO roots (path, normalized_path) VALUES ('C:\\ROOT', 'C:\\ROOT');"));
        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) VALUES (1, 'volume-a', 'file-a', 'C:\\Root\\b.txt', 'c:\\root\\b.txt', 'b.txt', '.txt', 12, '2026-08-31T00:00:00Z');"));
        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) VALUES (1, 'volume-a', 'file-b', 'C:\\ROOT\\A.TXT', 'C:\\ROOT\\A.TXT', 'A.TXT', '.TXT', 12, '2026-08-31T00:00:00Z');"));
        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "INSERT INTO files (root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) VALUES (1, 'volume-b', 'file-b', 'C:\\Root\\negative.txt', 'c:\\root\\negative.txt', 'negative.txt', '.txt', -1, '2026-08-31T00:00:00Z');"));
        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "INSERT INTO tags (name, normalized_name) VALUES ('WORK', 'WORK');"));
        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id) VALUES (1, 1);"));
        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'automatic');"));
        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id) VALUES (999, 1);"));
        Assert.ThrowsExactly<SqliteException>(() =>
            Execute(connection, "UPDATE file_tags SET source = 'unknown' WHERE file_id = 1 AND tag_id = 1;"));

        Execute(connection, "DELETE FROM file_tags WHERE file_id = 1 AND tag_id = 1 AND source = 'automatic';");
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE file_id = 1 AND tag_id = 1 AND source = 'user';"));
    }

    [TestMethod]
    public void VersionOneMigratesWithoutLosingUserTags()
    {
        using var database = TempDatabase.Create();
        using (var versionOne = SqliteDatabase.Open(database.Path, 1))
        using (var transaction = versionOne.BeginTransaction())
        {
            Execute(versionOne, "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\Root', 'c:\\root');", transaction);
            Execute(versionOne, "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) VALUES (1, 1, 'volume-a', 'file-a', 'C:\\Root\\a.txt', 'c:\\root\\a.txt', 'a.txt', '.txt', 12, '2026-08-31T00:00:00Z');", transaction);
            Execute(versionOne, "INSERT INTO tags (id, name, normalized_name) VALUES (1, 'Keep', 'keep');", transaction);
            Execute(versionOne, "INSERT INTO file_tags (file_id, tag_id) VALUES (1, 1);", transaction);
            transaction.Commit();
        }

        using var migrated = SqliteDatabase.Open(database.Path);
        Assert.AreEqual(SqliteDatabase.CurrentVersion, Scalar<long>(migrated, "PRAGMA user_version;"));
        Assert.AreEqual("user", Scalar<string>(migrated, "SELECT source FROM file_tags WHERE file_id = 1 AND tag_id = 1;"));
    }

    [TestMethod]
    public void FailedMigrationRollsBackCompletely()
    {
        using var database = TempDatabase.Create();
        using (var versionOne = SqliteDatabase.Open(database.Path, 1))
        {
            Execute(versionOne, "PRAGMA foreign_keys = OFF;");
            Execute(versionOne, "INSERT INTO file_tags (file_id, tag_id) VALUES (999, 999);");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(database.Path);
        });

        using var unchanged = SqliteDatabase.Open(database.Path, 1);
        Assert.AreEqual(1L, Scalar<long>(unchanged, "PRAGMA user_version;"));
        Assert.AreEqual(1L, Scalar<long>(unchanged, "SELECT COUNT(*) FROM file_tags WHERE file_id = 999 AND tag_id = 999;"));
        Assert.AreEqual(0L, Scalar<long>(unchanged, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'file_tags_v2';"));
    }

    [TestMethod]
    public void FailedBusinessTransactionRollsBackEarlierWrites()
    {
        using var database = TempDatabase.Create();
        using var connection = SqliteDatabase.Open(database.Path);

        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection, "INSERT INTO roots (path, normalized_path) VALUES ('C:\\First', 'c:\\first');", transaction);
            Assert.ThrowsExactly<SqliteException>(() =>
                Execute(connection, "INSERT INTO roots (path, normalized_path) VALUES ('C:\\Duplicate', 'C:\\FIRST');", transaction));
        }

        Assert.AreEqual(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM roots;"));
    }

    [TestMethod]
    public void FutureSchemaIsRejectedWithoutChangingJournalMode()
    {
        using var database = TempDatabase.Create();
        using (var raw = OpenRaw(database.Path))
        {
            Execute(raw, $"PRAGMA user_version = {SqliteDatabase.CurrentVersion + 1};");
            Execute(raw, "PRAGMA journal_mode = DELETE;");
        }

        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            using var _ = SqliteDatabase.Open(database.Path);
        });

        using var unchanged = OpenRaw(database.Path);
        Assert.AreEqual("delete", Scalar<string>(unchanged, "PRAGMA journal_mode;"));
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private sealed class TempDatabase : IDisposable
    {
        private TempDatabase(string path) => Path = path;

        public string Path { get; }

        public static TempDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Tests.{Guid.NewGuid():N}.db"));

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
