using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class DatabaseHealthServiceTests
{
    private readonly DatabaseHealthService _healthService = new();

    [TestMethod]
    public void NonExistentDatabaseIsDiagnosedAsHealthy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"GuraFile.NonExistent.{Guid.NewGuid():N}.db");
        try
        {
            var result = _healthService.CheckHealth(path);
            Assert.AreEqual(DatabaseHealthStatus.Healthy, result.Status);
            Assert.IsTrue(result.IsHealthy);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void NormalCurrentDatabaseIsDiagnosedAsHealthy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"GuraFile.Current.{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = SqliteDatabase.Open(path))
            {
                Assert.IsNotNull(conn);
            }

            var result = _healthService.CheckHealth(path);
            Assert.AreEqual(DatabaseHealthStatus.Healthy, result.Status);
            Assert.IsTrue(result.IsHealthy);
            Assert.AreEqual(SqliteDatabase.CurrentVersion, result.UserVersion);
        }
        finally
        {
            CleanupDatabaseFiles(path);
        }
    }

    [TestMethod]
    public void TruncatedFileIsDiagnosedAsCorrupted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"GuraFile.Truncated.{Guid.NewGuid():N}.db");
        try
        {
            // Write fewer than 100 bytes (e.g. 16 bytes of incomplete header or arbitrary bytes)
            File.WriteAllBytes(path, [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20]);

            var result = _healthService.CheckHealth(path);
            Assert.AreEqual(DatabaseHealthStatus.Corrupted, result.Status);
            Assert.IsFalse(result.IsHealthy);
            StringAssert.Contains(result.Message, "截断");
        }
        finally
        {
            CleanupDatabaseFiles(path);
        }
    }

    [TestMethod]
    public void InvalidMagicStringIsDiagnosedAsCorrupted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"GuraFile.InvalidMagic.{Guid.NewGuid():N}.db");
        try
        {
            var fakeHeader = new byte[1024];
            Random.Shared.NextBytes(fakeHeader);
            File.WriteAllBytes(path, fakeHeader);

            var result = _healthService.CheckHealth(path);
            Assert.AreEqual(DatabaseHealthStatus.Corrupted, result.Status);
            Assert.IsFalse(result.IsHealthy);
        }
        finally
        {
            CleanupDatabaseFiles(path);
        }
    }

    [TestMethod]
    public void CorruptedPagesFailQuickCheckAndAreDiagnosedAsCorrupted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"GuraFile.PageCorrupt.{Guid.NewGuid():N}.db");
        try
        {
            // Create a valid database with schema and data using DELETE journal
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode = DELETE; CREATE TABLE roots (id INTEGER PRIMARY KEY, path TEXT NOT NULL); INSERT INTO roots (id, path) VALUES (1, 'C:\\Data');";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Corrupt interior bytes (preserve first 100-byte SQLite header, corrupt btree page header at offset 100)
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Seek(100, SeekOrigin.Begin);
                var garbage = new byte[16];
                Array.Fill<byte>(garbage, 0xFF);
                fs.Write(garbage, 0, garbage.Length);
            }

            var result = _healthService.CheckHealth(path);
            Assert.AreEqual(DatabaseHealthStatus.Corrupted, result.Status);
            Assert.IsFalse(result.IsHealthy);
        }
        finally
        {
            CleanupDatabaseFiles(path);
        }
    }

    [TestMethod]
    public void ExclusiveFileStreamLockIsDiagnosedAsLockedAndNeverCorrupted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"GuraFile.Locked.{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = SqliteDatabase.Open(path))
            {
                Assert.IsNotNull(conn);
            }

            // Acquire exclusive lock with FileShare.None
            using var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = _healthService.CheckHealth(path);
            Assert.AreEqual(DatabaseHealthStatus.Locked, result.Status);
            Assert.AreNotEqual(DatabaseHealthStatus.Corrupted, result.Status);
            Assert.IsFalse(result.IsHealthy);
        }
        finally
        {
            CleanupDatabaseFiles(path);
        }
    }

    [TestMethod]
    [DataRow(11)]
    [DataRow(99)]
    public void UnsupportedFutureSchemaIsDiagnosedWithoutModifyingJournalMode(int futureVersion)
    {
        var path = Path.Combine(Path.GetTempPath(), $"GuraFile.FutureV{futureVersion}.{Guid.NewGuid():N}.db");
        try
        {
            // Create a valid SQLite db with user_version > CurrentVersion and delete-journal
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA journal_mode = DELETE; PRAGMA user_version = {futureVersion};";
                cmd.ExecuteNonQuery();
            }

            var result = _healthService.CheckHealth(path);
            Assert.AreEqual(DatabaseHealthStatus.UnsupportedFutureSchema, result.Status);
            Assert.AreEqual(futureVersion, result.UserVersion);
            Assert.IsFalse(result.IsHealthy);

            // Assert that journal_mode was NOT changed to WAL by health check
            using (var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode;";
                var journalMode = Convert.ToString(cmd.ExecuteScalar());
                Assert.AreEqual("delete", journalMode?.ToLowerInvariant());
            }
        }
        finally
        {
            CleanupDatabaseFiles(path);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void DatabaseUpgradedFromMigrationMatrixIsDiagnosedAsHealthyWithTagsIntact(int historicalVersion)
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(historicalVersion);

        // Migrate to current version
        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, Convert.ToInt32(
                new SqliteCommand("PRAGMA user_version;", connection).ExecuteScalar()));
        }

        // Check health
        var result = _healthService.CheckHealth(fixture.Path);
        Assert.AreEqual(DatabaseHealthStatus.Healthy, result.Status);
        Assert.IsTrue(result.IsHealthy);
        Assert.AreEqual(SqliteDatabase.CurrentVersion, result.UserVersion);

        // Verify tags are intact
        using var readConn = SqliteDatabase.Open(fixture.Path);
        using var tagCmd = readConn.CreateCommand();
        tagCmd.CommandText = "SELECT COUNT(*) FROM tags WHERE source = 'user';";
        var userTagCount = Convert.ToInt64(tagCmd.ExecuteScalar());
        Assert.IsGreaterThan(0L, userTagCount, $"Expected user tags in upgraded database v{historicalVersion}");
    }

    private static void CleanupDatabaseFiles(string databasePath)
    {
        foreach (var file in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                }
            }
        }
    }
}
