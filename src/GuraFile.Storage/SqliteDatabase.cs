using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public static class SqliteDatabase
{
    public const int CurrentVersion = 3;

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
        """
    ];

    public static SqliteConnection Open(string databasePath) => Open(databasePath, CurrentVersion);

    internal static SqliteConnection Open(string databasePath, int targetVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetVersion, CurrentVersion);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
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

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}
