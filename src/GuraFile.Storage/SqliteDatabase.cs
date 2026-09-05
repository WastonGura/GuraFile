using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public static class SqliteDatabase
{
    public const int CurrentVersion = 10;

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE roots (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            normalized_path TEXT NOT NULL COLLATE NOCASE UNIQUE
        );

        CREATE TABLE files (
            id INTEGER PRIMARY KEY,
            root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
            volume_id TEXT NOT NULL,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            normalized_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
            name TEXT NOT NULL,
            extension TEXT NOT NULL,
            size INTEGER NOT NULL CHECK (size >= 0),
            modified_utc TEXT NOT NULL,
            UNIQUE (volume_id, file_id)
        );

        CREATE TABLE tags (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );

        CREATE TABLE file_tags (
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY (file_id, tag_id)
        );
        """,
        """
        CREATE TABLE file_tags_v2 (
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            source TEXT NOT NULL DEFAULT 'user' CHECK (source IN ('user', 'automatic')),
            PRIMARY KEY (file_id, tag_id, source)
        );

        INSERT INTO file_tags_v2 (file_id, tag_id, source)
        SELECT file_id, tag_id, 'user' FROM file_tags;

        DROP TABLE file_tags;
        ALTER TABLE file_tags_v2 RENAME TO file_tags;
        """,
        """
        CREATE TABLE files_v3 (
            id INTEGER PRIMARY KEY,
            root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
            volume_id TEXT NOT NULL,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            normalized_path TEXT NOT NULL COLLATE NOCASE,
            name TEXT NOT NULL,
            extension TEXT NOT NULL,
            size INTEGER NOT NULL CHECK (size >= 0),
            modified_utc TEXT NOT NULL,
            identity_kind TEXT NOT NULL DEFAULT 'path' CHECK (identity_kind IN ('stable', 'path')),
            identity_diagnostic TEXT,
            is_online INTEGER NOT NULL DEFAULT 1 CHECK (is_online IN (0, 1)),
            scan_token TEXT NOT NULL DEFAULT '',
            UNIQUE (volume_id, file_id)
        );

        INSERT INTO files_v3 (
            id, root_id, volume_id, file_id, path, normalized_path,
            name, extension, size, modified_utc, identity_kind)
        SELECT
            id, root_id, volume_id, file_id, path, normalized_path,
            name, extension, size, modified_utc,
            CASE WHEN volume_id = 'path-fallback' THEN 'path' ELSE 'stable' END
        FROM files;

        CREATE TABLE file_tags_v3 (
            file_id INTEGER NOT NULL REFERENCES files_v3(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            source TEXT NOT NULL DEFAULT 'user' CHECK (source IN ('user', 'automatic')),
            PRIMARY KEY (file_id, tag_id, source)
        );

        INSERT INTO file_tags_v3 (file_id, tag_id, source)
        SELECT file_id, tag_id, source FROM file_tags;

        DROP TABLE file_tags;
        DROP TABLE files;
        ALTER TABLE files_v3 RENAME TO files;
        ALTER TABLE file_tags_v3 RENAME TO file_tags;
        CREATE UNIQUE INDEX files_online_path
            ON files(normalized_path COLLATE NOCASE) WHERE is_online = 1;
        """,
        """
        CREATE TABLE tags_v4 (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL COLLATE NOCASE,
            source TEXT NOT NULL DEFAULT 'user' CHECK (source IN ('user', 'automatic')),
            UNIQUE (normalized_name, source),
            UNIQUE (id, source)
        );

        INSERT INTO tags_v4 (id, name, normalized_name, source)
        SELECT t.id, t.name, t.normalized_name, 'user'
        FROM tags t
        WHERE NOT EXISTS (
            SELECT 1 FROM file_tags ft WHERE ft.tag_id = t.id AND ft.source = 'automatic'
        ) OR EXISTS (
            SELECT 1 FROM file_tags ft WHERE ft.tag_id = t.id AND ft.source = 'user'
        );

        INSERT INTO tags_v4 (id, name, normalized_name, source)
        SELECT t.id, t.name, t.normalized_name, 'automatic'
        FROM tags t
        WHERE EXISTS (
            SELECT 1 FROM file_tags ft WHERE ft.tag_id = t.id AND ft.source = 'automatic'
        ) AND NOT EXISTS (
            SELECT 1 FROM file_tags ft WHERE ft.tag_id = t.id AND ft.source = 'user'
        );

        INSERT INTO tags_v4 (name, normalized_name, source)
        SELECT t.name, t.normalized_name, 'automatic'
        FROM tags t
        WHERE EXISTS (
            SELECT 1 FROM file_tags ft WHERE ft.tag_id = t.id AND ft.source = 'automatic'
        ) AND EXISTS (
            SELECT 1 FROM file_tags ft WHERE ft.tag_id = t.id AND ft.source = 'user'
        );

        CREATE TABLE file_tags_v4 (
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL,
            source TEXT NOT NULL DEFAULT 'user' CHECK (source IN ('user', 'automatic')),
            PRIMARY KEY (file_id, tag_id, source),
            FOREIGN KEY (tag_id, source) REFERENCES tags_v4(id, source) ON DELETE CASCADE
        );

        INSERT INTO file_tags_v4 (file_id, tag_id, source)
        SELECT ft.file_id, replacement.id, ft.source
        FROM file_tags ft
        JOIN tags original ON original.id = ft.tag_id
        JOIN tags_v4 replacement
          ON replacement.normalized_name = original.normalized_name COLLATE NOCASE
         AND replacement.source = ft.source;

        DROP TABLE file_tags;
        DROP TABLE tags;
        ALTER TABLE tags_v4 RENAME TO tags;
        ALTER TABLE file_tags_v4 RENAME TO file_tags;
        """,
        """
        ALTER TABLE roots ADD COLUMN status TEXT NOT NULL DEFAULT 'online'
            CHECK (status IN ('online', 'offline', 'recovering'));
        ALTER TABLE roots ADD COLUMN last_error TEXT;
        ALTER TABLE roots ADD COLUMN last_checked_utc TEXT;
        """,
        """
        CREATE TABLE scan_sessions (
            id INTEGER PRIMARY KEY,
            root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
            scan_token TEXT NOT NULL,
            scan_type TEXT NOT NULL DEFAULT 'full' CHECK (scan_type IN ('full', 'recovery', 'reconcile')),
            status TEXT NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'completed', 'interrupted')),
            started_utc TEXT NOT NULL,
            completed_utc TEXT
        );
        CREATE INDEX idx_scan_sessions_root_status ON scan_sessions(root_id, status);
        """,
        """
        CREATE TABLE file_operation_intents (
            id INTEGER PRIMARY KEY,
            correlation_id TEXT NOT NULL,
            operation_type TEXT NOT NULL CHECK (operation_type IN ('copy', 'move', 'rename', 'recycle_bin_delete')),
            collision_policy TEXT NOT NULL DEFAULT 'auto_rename',
            status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'shell_completed', 'committed', 'indeterminate')),
            created_utc TEXT NOT NULL,
            completed_utc TEXT
        );
        CREATE INDEX idx_file_op_intents_status ON file_operation_intents(status);

        CREATE TABLE file_operation_intent_items (
            id INTEGER PRIMARY KEY,
            intent_id INTEGER NOT NULL REFERENCES file_operation_intents(id) ON DELETE CASCADE,
            source_path TEXT NOT NULL,
            destination_directory TEXT,
            target_name TEXT,
            expected_target_path TEXT,
            actual_target_path TEXT,
            shell_status TEXT CHECK (shell_status IN ('completed', 'failed', 'skipped', 'canceled', 'unknown')),
            commit_status TEXT CHECK (commit_status IN ('pending', 'committed', 'failed', 'indeterminate')),
            error TEXT
        );
        CREATE INDEX idx_file_op_intent_items_intent_id ON file_operation_intent_items(intent_id);
        """,
        """
        CREATE INDEX idx_files_name_nocase ON files(name COLLATE NOCASE);
        """,
        """
        CREATE VIRTUAL TABLE files_fts USING fts5(
            name,
            path,
            content='files',
            content_rowid='id'
        );

        CREATE TRIGGER files_ai AFTER INSERT ON files BEGIN
            INSERT INTO files_fts(rowid, name, path) VALUES (new.id, new.name, new.path);
        END;

        CREATE TRIGGER files_ad AFTER DELETE ON files BEGIN
            INSERT INTO files_fts(files_fts, rowid, name, path) VALUES('delete', old.id, old.name, old.path);
        END;

        CREATE TRIGGER files_au AFTER UPDATE OF name, path ON files BEGIN
            INSERT INTO files_fts(files_fts, rowid, name, path) VALUES('delete', old.id, old.name, old.path);
            INSERT INTO files_fts(rowid, name, path) VALUES (new.id, new.name, new.path);
        END;

        INSERT INTO files_fts(rowid, name, path) SELECT id, name, path FROM files;
        """,
        """
        CREATE TABLE saved_filter_views (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            search_text TEXT,
            sort_column TEXT NOT NULL DEFAULT 'Name',
            sort_descending INTEGER NOT NULL DEFAULT 0,
            tag_match_mode TEXT NOT NULL DEFAULT 'Any',
            is_tag_filter_enabled INTEGER NOT NULL DEFAULT 0,
            sort_order INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE saved_filter_view_tags (
            view_id INTEGER NOT NULL REFERENCES saved_filter_views(id) ON DELETE CASCADE,
            tag_id INTEGER NOT NULL,
            PRIMARY KEY (view_id, tag_id)
        );
        """
    ];

    public static SqliteConnection Open(string databasePath) => Open(databasePath, CurrentVersion);

    internal static SqliteConnection Open(string databasePath, int targetVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetVersion, CurrentVersion);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        try
        {
            connection.Open();
            Execute(connection, "PRAGMA foreign_keys = ON;");
            var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
            if (version > targetVersion)
            {
                throw new InvalidOperationException($"Database schema v{version} is newer than supported v{targetVersion}.");
            }

            Execute(connection, "PRAGMA journal_mode = WAL;");
            Migrate(connection, targetVersion, version);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Migrate(SqliteConnection connection, int targetVersion, int version)
    {
        while (version < targetVersion)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, Migrations[version], transaction);
            Execute(connection, $"PRAGMA user_version = {++version};", transaction);
            transaction.Commit();
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public static void RebuildSearchIndex(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Execute(connection, "INSERT INTO files_fts(files_fts) VALUES('rebuild');", transaction);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}
