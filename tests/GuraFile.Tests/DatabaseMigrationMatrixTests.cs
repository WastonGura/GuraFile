using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

[TestClass]
public sealed class DatabaseMigrationMatrixTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    public void HistoricalFixtures_InitializeAtExpectedSchemaVersion(int version)
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(version);
        Assert.AreEqual((long)version, DatabaseMigrationFixtures.GetUserVersion(fixture.Path));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    public void Matrix_AnyHistoricalVersion_UpgradesToCurrentVersionCleanly(int version)
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(version);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            Assert.AreEqual("wal", DatabaseMigrationFixtures.Scalar<string>(connection, "PRAGMA journal_mode;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA foreign_keys;"));

            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            // Verify core tables exist
            var tables = DatabaseMigrationFixtures.GetTableNames(connection);
            CollectionAssert.IsSubsetOf(new[] { "roots", "files", "tags", "file_tags", "scan_sessions", "file_operation_intents", "file_operation_intent_items", "files_fts" }, tables);

            // Verify triggers on files exist for FTS5 sync
            var triggerCount = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('files_ai', 'files_ad', 'files_au');");
            Assert.AreEqual(3L, triggerCount);

            // Verify index on scan_sessions exists
            var indexCount = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_scan_sessions_root_status';");
            Assert.AreEqual(1L, indexCount);

            // Verify index on file_operation_intents exists
            var intentIndexCount = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_file_op_intents_status';");
            Assert.AreEqual(1L, intentIndexCount);

            // Verify index on file_operation_intent_items exists
            var itemIndexCount = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_file_op_intent_items_intent_id';");
            Assert.AreEqual(1L, itemIndexCount);

            // Verify index on files(name COLLATE NOCASE) exists
            var nameIndexCount = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_files_name_nocase';");
            Assert.AreEqual(1L, nameIndexCount);

            // Verify scan_sessions check constraints
            var invalidSessions = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM scan_sessions WHERE status NOT IN ('running', 'completed', 'interrupted') OR scan_type NOT IN ('full', 'recovery', 'reconcile');");
            Assert.AreEqual(0L, invalidSessions);

            // Verify file_operation_intents check constraints
            var invalidIntents = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_operation_intents WHERE status NOT IN ('pending', 'shell_completed', 'committed', 'indeterminate') OR operation_type NOT IN ('copy', 'move', 'rename', 'recycle_bin_delete');");
            Assert.AreEqual(0L, invalidIntents);

            // Verify file_operation_intent_items check constraints
            var invalidIntentItems = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_operation_intent_items WHERE (shell_status IS NOT NULL AND shell_status NOT IN ('completed', 'failed', 'skipped', 'canceled', 'unknown')) OR (commit_status IS NOT NULL AND commit_status NOT IN ('pending', 'committed', 'failed', 'indeterminate'));");
            Assert.AreEqual(0L, invalidIntentItems);

            // Verify no source cross-contamination
            var corruptedRelations = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.source <> t.source;");
            Assert.AreEqual(0L, corruptedRelations, "file_tags and tags sources must match without contamination.");

            var invalidTagSources = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM tags WHERE source NOT IN ('user', 'automatic');");
            Assert.AreEqual(0L, invalidTagSources);

            var invalidRelationSources = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags WHERE source NOT IN ('user', 'automatic');");
            Assert.AreEqual(0L, invalidRelationSources);

            // Verify roots have valid status
            var invalidRootStatus = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM roots WHERE status NOT IN ('online', 'offline', 'recovering');");
            Assert.AreEqual(0L, invalidRootStatus);

            // Verify files have valid identity_kind and is_online
            var invalidFiles = DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM files WHERE identity_kind NOT IN ('stable', 'path') OR is_online NOT IN (0, 1);");
            Assert.AreEqual(0L, invalidFiles);
        }
    }

    [TestMethod]
    public void Migrate_Version1_To_Current_PreservesAllData()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(1);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            // 1. Roots check
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(@"C:\FixtureRoot\Docs", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT path FROM roots WHERE id = 1;"));
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 1;"));
            Assert.IsNull(DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT last_error FROM roots WHERE id = 1;"));
            Assert.AreEqual(@"C:\FixtureRoot\Media", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT path FROM roots WHERE id = 2;"));
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 2;"));

            // 2. Files check
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));
            // File 1: stable
            Assert.AreEqual("stable", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 1;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 1;"));
            Assert.AreEqual("Report.pdf", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT name FROM files WHERE id = 1;"));
            Assert.AreEqual(1024L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT size FROM files WHERE id = 1;"));

            // File 2: path fallback (derived from volume_id = 'path-fallback')
            Assert.AreEqual("path", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 2;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 2;"));
            Assert.AreEqual("Fallback.txt", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT name FROM files WHERE id = 2;"));

            // File 3: stable
            Assert.AreEqual("stable", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 3;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 3;"));

            // 3. Tags check (v1 tags all become user tags)
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags;"));
            Assert.AreEqual("user", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT source FROM tags WHERE name = 'Urgent';"));
            Assert.AreEqual("user", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT source FROM tags WHERE name = 'Projects';"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE source = 'automatic';"));

            // 4. File Tags check
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags;"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE source = 'automatic';"));

            // File 1 has Urgent and Projects
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 1 AND t.name = 'Urgent' AND ft.source = 'user';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 1 AND t.name = 'Projects' AND ft.source = 'user';"));

            // File 2 has Projects
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 2 AND t.name = 'Projects' AND ft.source = 'user';"));
        }
    }

    [TestMethod]
    public void Migrate_Version2_To_Current_PreservesAllData()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(2);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            // 1. Roots check
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 1;"));
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 2;"));

            // 2. Files check
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual("stable", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 1;"));
            Assert.AreEqual("path", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 2;"));
            Assert.AreEqual("stable", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 3;"));
            Assert.AreEqual("stable", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 4;"));

            // 3. Tags check
            // 'UserOnly' -> user
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'UserOnly' AND source = 'user';"));
            // 'AutoOnly' -> automatic
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'AutoOnly' AND source = 'automatic';"));
            // 'SharedTag' was used as BOTH user and automatic in v2 -> must be split into two separate tags
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'SharedTag' AND source = 'user';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'SharedTag' AND source = 'automatic';"));
            // 'MultiFile' -> user
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'MultiFile' AND source = 'user';"));

            // Total tags = 5 (UserOnly, AutoOnly, SharedTag user, SharedTag auto, MultiFile)
            Assert.AreEqual(5L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags;"));

            // 4. File Tags check
            Assert.AreEqual(6L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags;"));

            // File 1 has UserOnly ('user')
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 1 AND t.name = 'UserOnly' AND ft.source = 'user';"));
            // File 2 has AutoOnly ('automatic')
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 2 AND t.name = 'AutoOnly' AND ft.source = 'automatic';"));
            // File 1 has SharedTag ('user')
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 1 AND t.name = 'SharedTag' AND ft.source = 'user';"));
            // File 3 has SharedTag ('automatic')
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 3 AND t.name = 'SharedTag' AND ft.source = 'automatic';"));
            // File 1 has MultiFile ('user')
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 1 AND t.name = 'MultiFile' AND ft.source = 'user';"));
            // File 4 has MultiFile ('user')
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 4 AND t.name = 'MultiFile' AND ft.source = 'user';"));
        }
    }

    [TestMethod]
    public void Migrate_Version3_To_Current_PreservesAllData()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(3);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            // 1. Roots check
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 1;"));
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 2;"));

            // 2. Files check: test online AND offline preservation, stable AND path preservation
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));

            // File 1: Online stable
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 1;"));
            Assert.AreEqual("stable", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 1;"));
            Assert.AreEqual("token-v3-1", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT scan_token FROM files WHERE id = 1;"));

            // File 2: Offline stable (MUST stay offline!)
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 2;"));
            Assert.AreEqual("stable", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 2;"));
            Assert.AreEqual("OldMain.cs", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT name FROM files WHERE id = 2;"));

            // File 3: Online path fallback
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 3;"));
            Assert.AreEqual("path", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 3;"));
            Assert.AreEqual("Volume not supporting file IDs", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_diagnostic FROM files WHERE id = 3;"));

            // File 4: Offline path fallback (MUST stay offline!)
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 4;"));
            Assert.AreEqual("path", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_kind FROM files WHERE id = 4;"));
            Assert.AreEqual("File ID query failed", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT identity_diagnostic FROM files WHERE id = 4;"));

            // 3. Tags check
            // Core -> user
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'Core' AND source = 'user';"));
            // AutoCode -> automatic
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'AutoCode' AND source = 'automatic';"));
            // Review -> split into user and automatic
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'Review' AND source = 'user';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE name = 'Review' AND source = 'automatic';"));

            // 4. File Tags check
            // Offline file 2 retains its user tag Core
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 2 AND t.name = 'Core' AND ft.source = 'user';"));
            // Offline path file 4 retains its automatic tag Review
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 4 AND t.name = 'Review' AND ft.source = 'automatic';"));
            // Online path file 3 retains its user tag Review
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM file_tags ft JOIN tags t ON t.id = ft.tag_id WHERE ft.file_id = 3 AND t.name = 'Review' AND ft.source = 'user';"));
        }
    }

    [TestMethod]
    public void Migrate_Version4_To_Current_PreservesAllData()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(4);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            // 1. Roots check: default status = 'online'
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 1;"));
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 2;"));

            // 2. Files check: 4 files
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 1;"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 2;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 3;"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT is_online FROM files WHERE id = 4;"));

            // 3. Tags check: same name with distinct sources preserved
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE id = 1 AND name = 'Archive' AND source = 'user';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE id = 2 AND name = 'Archive' AND source = 'automatic';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE id = 3 AND name = 'Starred' AND source = 'user';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags WHERE id = 4 AND name = 'AutoImage' AND source = 'automatic';"));

            // 4. File tags check
            Assert.AreEqual(6L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE file_id = 1 AND tag_id = 1 AND source = 'user';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE file_id = 1 AND tag_id = 4 AND source = 'automatic';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE file_id = 2 AND tag_id = 3 AND source = 'user';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE file_id = 3 AND tag_id = 2 AND source = 'automatic';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE file_id = 3 AND tag_id = 3 AND source = 'user';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags WHERE file_id = 4 AND tag_id = 1 AND source = 'user';"));
        }
    }

    [TestMethod]
    public void Migrate_Version5_To_Current_PreservesAllData()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(5);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            // scan_sessions table created and initially empty
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM scan_sessions;"));

            // Roots status and timestamps/errors MUST NOT be overwritten
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 1;"));
            Assert.IsNull(DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT last_error FROM roots WHERE id = 1;"));
            Assert.AreEqual("2026-09-03T10:00:00Z", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT last_checked_utc FROM roots WHERE id = 1;"));

            Assert.AreEqual("offline", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 2;"));
            Assert.AreEqual("Volume disconnected", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT last_error FROM roots WHERE id = 2;"));
            Assert.AreEqual("2026-09-03T09:00:00Z", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT last_checked_utc FROM roots WHERE id = 2;"));

            Assert.AreEqual("recovering", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 3;"));
            Assert.AreEqual("Sync in progress", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT last_error FROM roots WHERE id = 3;"));
            Assert.AreEqual("2026-09-03T09:30:00Z", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT last_checked_utc FROM roots WHERE id = 3;"));

            // Files and tags intact
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags;"));
        }
    }

    [TestMethod]
    public void Migrate_Version6_To_Current_PreservesAllData()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(6);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            // file_operation_intents and items created and initially empty
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_operation_intents;"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_operation_intent_items;"));

            // Roots status and timestamps/errors intact
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 1;"));
            Assert.AreEqual("offline", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 2;"));
            Assert.AreEqual("recovering", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 3;"));

            // Files and tags intact
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags;"));

            // Scan sessions intact
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM scan_sessions;"));
            Assert.AreEqual("completed", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM scan_sessions WHERE id = 1;"));
            Assert.AreEqual("full", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT scan_type FROM scan_sessions WHERE id = 1;"));
            Assert.AreEqual("interrupted", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM scan_sessions WHERE id = 2;"));
            Assert.AreEqual("running", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM scan_sessions WHERE id = 3;"));
        }
    }

    [TestMethod]
    public void Migrate_Version7_To_Current_IsIdempotent()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(7);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            // Roots status intact
            Assert.AreEqual("online", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 1;"));
            Assert.AreEqual("offline", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 2;"));
            Assert.AreEqual("recovering", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM roots WHERE id = 3;"));

            // Files and tags intact
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_tags;"));

            // Scan sessions intact
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM scan_sessions;"));

            // File operation intents intact
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_operation_intents;"));
            Assert.AreEqual("committed", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM file_operation_intents WHERE id = 1;"));
            Assert.AreEqual("copy", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT operation_type FROM file_operation_intents WHERE id = 1;"));
            Assert.AreEqual("indeterminate", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM file_operation_intents WHERE id = 2;"));
            Assert.AreEqual("move", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT operation_type FROM file_operation_intents WHERE id = 2;"));
            Assert.AreEqual("pending", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT status FROM file_operation_intents WHERE id = 3;"));
            Assert.AreEqual("recycle_bin_delete", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT operation_type FROM file_operation_intents WHERE id = 3;"));

            // File operation intent items intact
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM file_operation_intent_items;"));
            Assert.AreEqual("committed", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT commit_status FROM file_operation_intent_items WHERE id = 1;"));
            Assert.AreEqual("indeterminate", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT commit_status FROM file_operation_intent_items WHERE id = 2;"));
            Assert.AreEqual("pending", DatabaseMigrationFixtures.Scalar<string>(connection, "SELECT commit_status FROM file_operation_intent_items WHERE id = 3;"));
        }
    }

    [TestMethod]
    public void Migrate_Version8_To_Current_CreatesFtsTableAndSyncsFiles()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(8);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_files_name_nocase';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'files_fts';"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('files_ai', 'files_ad', 'files_au');"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files_fts;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM tags;"));
        }
    }

    [TestMethod]
    public void Migrate_Version9_To_Current_IsIdempotent()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(9);

        using (var connection = SqliteDatabase.Open(fixture.Path))
        {
            Assert.AreEqual(SqliteDatabase.CurrentVersion, DatabaseMigrationFixtures.Scalar<long>(connection, "PRAGMA user_version;"));
            DatabaseMigrationFixtures.AssertForeignKeys(connection);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(connection);

            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'files_fts';"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(connection, "SELECT COUNT(*) FROM files_fts;"));
        }
    }

    [TestMethod]
    public void Chained_Stepwise_Migration_From_Version1_To_Version9()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(1);

        // Step 1 -> 2
        using (var v2Conn = SqliteDatabase.Open(fixture.Path, 2))
        {
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(v2Conn, "PRAGMA user_version;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v2Conn,
                "SELECT COUNT(*) FROM pragma_table_info('file_tags') WHERE name = 'source';"));
            DatabaseMigrationFixtures.AssertForeignKeys(v2Conn);
        }

        // Step 2 -> 3
        using (var v3Conn = SqliteDatabase.Open(fixture.Path, 3))
        {
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(v3Conn, "PRAGMA user_version;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(v3Conn,
                "SELECT COUNT(*) FROM pragma_table_info('files') WHERE name IN ('identity_kind', 'is_online');"));
            DatabaseMigrationFixtures.AssertForeignKeys(v3Conn);
        }

        // Step 3 -> 4
        using (var v4Conn = SqliteDatabase.Open(fixture.Path, 4))
        {
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(v4Conn, "PRAGMA user_version;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v4Conn,
                "SELECT COUNT(*) FROM pragma_table_info('tags') WHERE name = 'source';"));
            DatabaseMigrationFixtures.AssertForeignKeys(v4Conn);
        }

        // Step 4 -> 5
        using (var v5Conn = SqliteDatabase.Open(fixture.Path, 5))
        {
            Assert.AreEqual(5L, DatabaseMigrationFixtures.Scalar<long>(v5Conn, "PRAGMA user_version;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(v5Conn,
                "SELECT COUNT(*) FROM pragma_table_info('roots') WHERE name IN ('status', 'last_error', 'last_checked_utc');"));
            DatabaseMigrationFixtures.AssertForeignKeys(v5Conn);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(v5Conn);

            // Intermediate data check: roots and files correctly retained
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(v5Conn, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(v5Conn, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual("stable", DatabaseMigrationFixtures.Scalar<string>(v5Conn, "SELECT identity_kind FROM files WHERE id = 1;"));
            Assert.AreEqual("path", DatabaseMigrationFixtures.Scalar<string>(v5Conn, "SELECT identity_kind FROM files WHERE id = 2;"));
        }

        // Step 5 -> 6
        using (var v6Conn = SqliteDatabase.Open(fixture.Path, 6))
        {
            Assert.AreEqual(6L, DatabaseMigrationFixtures.Scalar<long>(v6Conn, "PRAGMA user_version;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v6Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'scan_sessions';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v6Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_scan_sessions_root_status';"));
            DatabaseMigrationFixtures.AssertForeignKeys(v6Conn);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(v6Conn);
        }

        // Step 6 -> 7
        using (var v7Conn = SqliteDatabase.Open(fixture.Path, 7))
        {
            Assert.AreEqual(7L, DatabaseMigrationFixtures.Scalar<long>(v7Conn, "PRAGMA user_version;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v7Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'file_operation_intents';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v7Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'file_operation_intent_items';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v7Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_file_op_intents_status';"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v7Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_file_op_intent_items_intent_id';"));
            DatabaseMigrationFixtures.AssertForeignKeys(v7Conn);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(v7Conn);
        }

        // Step 7 -> 8
        using (var v8Conn = SqliteDatabase.Open(fixture.Path, 8))
        {
            Assert.AreEqual(8L, DatabaseMigrationFixtures.Scalar<long>(v8Conn, "PRAGMA user_version;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v8Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_files_name_nocase';"));
            DatabaseMigrationFixtures.AssertForeignKeys(v8Conn);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(v8Conn);
        }

        // Step 8 -> 9
        using (var v9Conn = SqliteDatabase.Open(fixture.Path, 9))
        {
            Assert.AreEqual(9L, DatabaseMigrationFixtures.Scalar<long>(v9Conn, "PRAGMA user_version;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(v9Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'files_fts';"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(v9Conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('files_ai', 'files_ad', 'files_au');"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(v9Conn, "SELECT COUNT(*) FROM files_fts;"));
            DatabaseMigrationFixtures.AssertForeignKeys(v9Conn);
            DatabaseMigrationFixtures.AssertNoTemporaryTables(v9Conn);
        }
    }

    [TestMethod]
    public void MigrationStep_V1_To_V2_RollsBack_OnFailure()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(1);

        // Inject foreign key violating dirty data into v1
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            DatabaseMigrationFixtures.Execute(raw, "PRAGMA foreign_keys = OFF;");
            DatabaseMigrationFixtures.Execute(raw, "INSERT INTO file_tags (file_id, tag_id) VALUES (9999, 9999);");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(fixture.Path, 2);
        });

        // Verify transaction completely rolled back
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(raw, "PRAGMA user_version;"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'file_tags_v2';"));
            // Original file_tags still has original structure without source column
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM pragma_table_info('file_tags') WHERE name = 'source';"));
            // The injected row is still there in the untouched v1 table
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM file_tags WHERE file_id = 9999 AND tag_id = 9999;"));
            // Roots, files, tags all intact
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM files;"));
        }
    }

    [TestMethod]
    public void MigrationStep_V2_To_V3_RollsBack_OnFailure()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(2);

        // Inject orphaned relation pointing to nonexistent file
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            DatabaseMigrationFixtures.Execute(raw, "PRAGMA foreign_keys = OFF;");
            DatabaseMigrationFixtures.Execute(raw, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (8888, 1, 'user');");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(fixture.Path, 3);
        });

        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "PRAGMA user_version;"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('files_v3', 'file_tags_v3');"));
            // files table does not have identity_kind yet
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM pragma_table_info('files') WHERE name = 'identity_kind';"));
            // Original data preserved
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM tags;"));
        }
    }

    [TestMethod]
    public void MigrationStep_V3_To_V4_RollsBack_OnFailure()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(3);

        // In v3, inject orphaned relation with nonexistent file_id and valid tag_id = 1
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            DatabaseMigrationFixtures.Execute(raw, "PRAGMA foreign_keys = OFF;");
            DatabaseMigrationFixtures.Execute(raw, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (7777, 1, 'user');");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(fixture.Path, 4);
        });

        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "PRAGMA user_version;"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('tags_v4', 'file_tags_v4');"));
            // tags does not have source column
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM pragma_table_info('tags') WHERE name = 'source';"));
            // Original data preserved
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM tags;"));
        }
    }

    [TestMethod]
    public void MigrationStep_V4_To_V5_RollsBack_OnFailure()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(4);

        // Inject conflicting column status on roots
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            DatabaseMigrationFixtures.Execute(raw, "ALTER TABLE roots ADD COLUMN status TEXT;");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(fixture.Path, 5);
        });

        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(raw, "PRAGMA user_version;"));
            // status is the injected column; last_checked_utc must NOT exist because migration rolled back
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM pragma_table_info('roots') WHERE name = 'last_checked_utc';"));
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM pragma_table_info('roots') WHERE name = 'last_error';"));
            // Original roots data preserved
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(4L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM tags;"));
        }
    }

    [TestMethod]
    public void MigrationStep_V5_To_V6_RollsBack_OnFailure()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(5);

        // Inject conflicting scan_sessions table with bad schema in v5
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            DatabaseMigrationFixtures.Execute(raw, "CREATE TABLE scan_sessions (invalid_column TEXT);");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(fixture.Path, 6);
        });

        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            Assert.AreEqual(5L, DatabaseMigrationFixtures.Scalar<long>(raw, "PRAGMA user_version;"));
            // idx_scan_sessions_root_status must NOT exist because migration rolled back
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_scan_sessions_root_status';"));
            // Original roots, files, tags data preserved
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM tags;"));
        }
    }

    [TestMethod]
    public void MigrationStep_V6_To_V7_RollsBack_OnFailure()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(6);

        // Inject conflicting file_operation_intents table with bad schema in v6
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            DatabaseMigrationFixtures.Execute(raw, "CREATE TABLE file_operation_intents (invalid_column TEXT);");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(fixture.Path, 7);
        });

        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            Assert.AreEqual(6L, DatabaseMigrationFixtures.Scalar<long>(raw, "PRAGMA user_version;"));
            // idx_file_op_intents_status must NOT exist because migration rolled back
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_file_op_intents_status';"));
            // file_operation_intent_items must NOT exist
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'file_operation_intent_items';"));
            // Original roots, files, tags, scan_sessions data preserved
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM tags;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM scan_sessions;"));
        }
    }

    [TestMethod]
    public void MigrationStep_V7_To_V8_RollsBack_OnFailure()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(7);

        // Inject conflicting idx_files_name_nocase as a table in v7 so index creation fails
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            DatabaseMigrationFixtures.Execute(raw, "CREATE TABLE idx_files_name_nocase (dummy TEXT);");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(fixture.Path, 8);
        });

        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            Assert.AreEqual(7L, DatabaseMigrationFixtures.Scalar<long>(raw, "PRAGMA user_version;"));
            // Index idx_files_name_nocase must NOT exist as an index
            Assert.AreEqual(0L, DatabaseMigrationFixtures.Scalar<long>(raw,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_files_name_nocase';"));
            // Original roots, files, tags, scan_sessions, file_operation_intents data preserved
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM tags;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM scan_sessions;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM file_operation_intents;"));
        }
    }

    [TestMethod]
    public void MigrationStep_V8_To_V9_RollsBack_OnFailure()
    {
        using var fixture = DatabaseMigrationFixtures.CreateTempDatabase(8);

        // Inject conflicting files_fts as a regular table in v8 so virtual table creation fails
        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            DatabaseMigrationFixtures.Execute(raw, "CREATE TABLE files_fts (dummy TEXT);");
        }

        Assert.ThrowsExactly<SqliteException>(() =>
        {
            using var _ = SqliteDatabase.Open(fixture.Path, 9);
        });

        using (var raw = DatabaseMigrationFixtures.OpenRaw(fixture.Path))
        {
            Assert.AreEqual(8L, DatabaseMigrationFixtures.Scalar<long>(raw, "PRAGMA user_version;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM roots;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM files;"));
            Assert.AreEqual(2L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM tags;"));
            Assert.AreEqual(1L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM scan_sessions;"));
            Assert.AreEqual(3L, DatabaseMigrationFixtures.Scalar<long>(raw, "SELECT COUNT(*) FROM file_operation_intents;"));
        }
    }

    [TestMethod]
    public void FutureSchema_V10_Rejected_WithoutModifyingJournalMode()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"GuraFile.FutureV10.{Guid.NewGuid():N}.db");
        try
        {
            using (var raw = DatabaseMigrationFixtures.OpenRaw(dbPath))
            {
                DatabaseMigrationFixtures.Execute(raw, "PRAGMA user_version = 10;");
                DatabaseMigrationFixtures.Execute(raw, "PRAGMA journal_mode = DELETE;");
            }

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                using var _ = SqliteDatabase.Open(dbPath);
            });

            StringAssert.Contains(ex.Message, "v10 is newer than supported v9");

            // Verify journal_mode was untouched before exception
            using (var raw = DatabaseMigrationFixtures.OpenRaw(dbPath))
            {
                Assert.AreEqual("delete", DatabaseMigrationFixtures.Scalar<string>(raw, "PRAGMA journal_mode;"));
            }
        }
        finally
        {
            foreach (var file in new[] { dbPath, $"{dbPath}-shm", $"{dbPath}-wal" })
            {
                if (File.Exists(file))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
    }

    [TestMethod]
    public void FutureSchema_V99_Rejected_WithoutModifyingJournalMode()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"GuraFile.FutureV99.{Guid.NewGuid():N}.db");
        try
        {
            using (var raw = DatabaseMigrationFixtures.OpenRaw(dbPath))
            {
                DatabaseMigrationFixtures.Execute(raw, "PRAGMA user_version = 99;");
                DatabaseMigrationFixtures.Execute(raw, "PRAGMA journal_mode = DELETE;");
            }

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                using var _ = SqliteDatabase.Open(dbPath);
            });

            StringAssert.Contains(ex.Message, "v99 is newer than supported v9");

            using (var raw = DatabaseMigrationFixtures.OpenRaw(dbPath))
            {
                Assert.AreEqual("delete", DatabaseMigrationFixtures.Scalar<string>(raw, "PRAGMA journal_mode;"));
            }
        }
        finally
        {
            foreach (var file in new[] { dbPath, $"{dbPath}-shm", $"{dbPath}-wal" })
            {
                if (File.Exists(file))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
    }

    [TestMethod]
    public void FutureSchema_TargetVersion_BoundsEnforced()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"GuraFile.Bounds.{Guid.NewGuid():N}.db");
        try
        {
            using (var fixture = DatabaseMigrationFixtures.CreateTempDatabase(4))
            {
                // Requesting targetVersion 3 on a v4 database should be rejected
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                {
                    using var _ = SqliteDatabase.Open(fixture.Path, 3);
                });
                StringAssert.Contains(ex.Message, "v4 is newer than supported v3");
            }

            // Target version out of range (> CurrentVersion) throws ArgumentOutOfRangeException
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                using var _ = SqliteDatabase.Open(dbPath, 10);
            });

            // Target version out of range (< 0) throws ArgumentOutOfRangeException
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                using var _ = SqliteDatabase.Open(dbPath, -1);
            });
        }
        finally
        {
            foreach (var file in new[] { dbPath, $"{dbPath}-shm", $"{dbPath}-wal" })
            {
                if (File.Exists(file))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
    }
}
