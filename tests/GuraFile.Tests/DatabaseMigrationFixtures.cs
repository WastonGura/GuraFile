using GuraFile.Storage;
using Microsoft.Data.Sqlite;

namespace GuraFile.Tests;

public static class DatabaseMigrationFixtures
{
    public const int MinHistoricalVersion = 1;
    public const int MaxHistoricalVersion = 7;

    public sealed class TempFixtureDatabase : IDisposable
    {
        public string Path { get; }
        public int Version { get; }

        public TempFixtureDatabase(string path, int version)
        {
            Path = path;
            Version = version;
        }

        public void Dispose()
        {
            foreach (var file in new[] { Path, $"{Path}-shm", $"{Path}-wal" })
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

    public static TempFixtureDatabase CreateTempDatabase(int version)
    {
        var path = Path.Combine(Path.GetTempPath(), $"GuraFile.Fixture.v{version}.{Guid.NewGuid():N}.db");
        CreateDatabase(version, path);
        return new TempFixtureDatabase(path, version);
    }

    public static void CreateDatabase(int version, string path)
    {
        switch (version)
        {
            case 1:
                CreateVersion1Database(path);
                break;
            case 2:
                CreateVersion2Database(path);
                break;
            case 3:
                CreateVersion3Database(path);
                break;
            case 4:
                CreateVersion4Database(path);
                break;
            case 5:
                CreateVersion5Database(path);
                break;
            case 6:
                CreateVersion6Database(path);
                break;
            case 7:
                CreateVersion7Database(path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(version), version, $"Unsupported fixture version {version}. Supported: 1..7");
        }
    }

    public static string CreateVersion1Database(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"GuraFile.Fixture.v1.{Guid.NewGuid():N}.db");

        using (var connection = SqliteDatabase.Open(path, 1))
        using (var transaction = connection.BeginTransaction())
        {
            // Roots: 2 roots
            Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\FixtureRoot\\Docs', 'c:\\fixtureroot\\docs');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (2, 'C:\\FixtureRoot\\Media', 'c:\\fixtureroot\\media');", transaction);

            // Files: 3 files (stable, path-fallback, and media stable)
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (1, 1, 'vol-v1-stable', 'file-v1-001', 'C:\\FixtureRoot\\Docs\\Report.pdf', 'c:\\fixtureroot\\docs\\report.pdf', 'Report.pdf', '.pdf', 1024, '2026-08-30T10:00:00Z');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (2, 1, 'path-fallback', 'path-v1-002', 'C:\\FixtureRoot\\Docs\\Fallback.txt', 'c:\\fixtureroot\\docs\\fallback.txt', 'Fallback.txt', '.txt', 512, '2026-08-30T10:05:00Z');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (3, 2, 'vol-v1-media', 'file-v1-003', 'C:\\FixtureRoot\\Media\\Photo.jpg', 'c:\\fixtureroot\\media\\photo.jpg', 'Photo.jpg', '.jpg', 204800, '2026-08-30T10:10:00Z');",
                transaction);

            // Tags (v1 has no source column)
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (1, 'Urgent', 'urgent');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (2, 'Projects', 'projects');", transaction);

            // File Tags (v1 has no source column; file 1 has multi-tags, tag 2 has multi-files)
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id) VALUES (1, 1);", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id) VALUES (1, 2);", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id) VALUES (2, 2);", transaction);

            transaction.Commit();
        }

        return path;
    }

    public static string CreateVersion2Database(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"GuraFile.Fixture.v2.{Guid.NewGuid():N}.db");

        using (var connection = SqliteDatabase.Open(path, 2))
        using (var transaction = connection.BeginTransaction())
        {
            // Roots: 2 roots
            Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\FixtureRoot\\Docs', 'c:\\fixtureroot\\docs');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (2, 'C:\\FixtureRoot\\Archive', 'c:\\fixtureroot\\archive');", transaction);

            // Files: 4 files
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (1, 1, 'vol-v2-main', 'file-v2-001', 'C:\\FixtureRoot\\Docs\\Spec.pdf', 'c:\\fixtureroot\\docs\\spec.pdf', 'Spec.pdf', '.pdf', 4096, '2026-08-31T09:00:00Z');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (2, 1, 'path-fallback', 'path-v2-002', 'C:\\FixtureRoot\\Docs\\Legacy.txt', 'c:\\fixtureroot\\docs\\legacy.txt', 'Legacy.txt', '.txt', 128, '2026-08-31T09:15:00Z');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (3, 2, 'vol-v2-media', 'file-v2-003', 'C:\\FixtureRoot\\Archive\\Photo.png', 'c:\\fixtureroot\\archive\\photo.png', 'Photo.png', '.png', 1048576, '2026-08-31T09:30:00Z');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc) " +
                "VALUES (4, 2, 'vol-v2-media', 'file-v2-004', 'C:\\FixtureRoot\\Archive\\Data.csv', 'c:\\fixtureroot\\archive\\data.csv', 'Data.csv', '.csv', 2048, '2026-08-31T09:45:00Z');",
                transaction);

            // Tags (v2 tags still has no source column)
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (1, 'UserOnly', 'useronly');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (2, 'AutoOnly', 'autoonly');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (3, 'SharedTag', 'sharedtag');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (4, 'MultiFile', 'multifile');", transaction);

            // File Tags (v2 has source column in file_tags)
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (2, 2, 'automatic');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 3, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (3, 3, 'automatic');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 4, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (4, 4, 'user');", transaction);

            transaction.Commit();
        }

        return path;
    }

    public static string CreateVersion3Database(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"GuraFile.Fixture.v3.{Guid.NewGuid():N}.db");

        using (var connection = SqliteDatabase.Open(path, 3))
        using (var transaction = connection.BeginTransaction())
        {
            // Roots: 2 roots
            Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\FixtureRoot\\Projects', 'c:\\fixtureroot\\projects');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (2, 'C:\\FixtureRoot\\External', 'c:\\fixtureroot\\external');", transaction);

            // Files: 4 files (online stable, offline stable, online path-fallback, offline path-fallback)
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (1, 1, 'vol-v3-01', 'file-v3-001', 'C:\\FixtureRoot\\Projects\\Main.cs', 'c:\\fixtureroot\\projects\\main.cs', 'Main.cs', '.cs', 8192, '2026-09-01T12:00:00Z', 'stable', NULL, 1, 'token-v3-1');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (2, 1, 'vol-v3-01', 'file-v3-002', 'C:\\FixtureRoot\\Projects\\OldMain.cs', 'c:\\fixtureroot\\projects\\oldmain.cs', 'OldMain.cs', '.cs', 4096, '2026-09-01T11:00:00Z', 'stable', NULL, 0, 'token-v3-1');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (3, 2, 'path-fallback', 'path-v3-003', 'C:\\FixtureRoot\\External\\Readme.md', 'c:\\fixtureroot\\external\\readme.md', 'Readme.md', '.md', 1024, '2026-09-01T12:30:00Z', 'path', 'Volume not supporting file IDs', 1, 'token-v3-2');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (4, 2, 'path-fallback', 'path-v3-004', 'C:\\FixtureRoot\\External\\Missing.md', 'c:\\fixtureroot\\external\\missing.md', 'Missing.md', '.md', 512, '2026-09-01T10:00:00Z', 'path', 'File ID query failed', 0, 'token-v3-2');",
                transaction);

            // Tags: 3 tags
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (1, 'Core', 'core');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (2, 'AutoCode', 'autocode');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name) VALUES (3, 'Review', 'review');", transaction);

            // File Tags:
            // File 1 (online stable) has Core ('user') and AutoCode ('automatic')
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 2, 'automatic');", transaction);
            // File 2 (offline stable) has Core ('user')
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (2, 1, 'user');", transaction);
            // File 3 (online path) has Review ('user')
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (3, 3, 'user');", transaction);
            // File 4 (offline path) has Review ('automatic')
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (4, 3, 'automatic');", transaction);

            transaction.Commit();
        }

        return path;
    }

    public static string CreateVersion4Database(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"GuraFile.Fixture.v4.{Guid.NewGuid():N}.db");

        using (var connection = SqliteDatabase.Open(path, 4))
        using (var transaction = connection.BeginTransaction())
        {
            // Roots: 2 roots
            Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (1, 'C:\\FixtureRoot\\Design', 'c:\\fixtureroot\\design');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path) VALUES (2, 'C:\\FixtureRoot\\Assets', 'c:\\fixtureroot\\assets');", transaction);

            // Files: 4 files (online stable, offline stable, online path-fallback, offline path-fallback)
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (1, 1, 'vol-v4-01', 'file-v4-001', 'C:\\FixtureRoot\\Design\\App.xaml', 'c:\\fixtureroot\\design\\app.xaml', 'App.xaml', '.xaml', 16384, '2026-09-02T14:00:00Z', 'stable', NULL, 1, 'tok-v4-1');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (2, 1, 'vol-v4-01', 'file-v4-002', 'C:\\FixtureRoot\\Design\\OldApp.xaml', 'c:\\fixtureroot\\design\\oldapp.xaml', 'OldApp.xaml', '.xaml', 8192, '2026-09-02T13:00:00Z', 'stable', NULL, 0, 'tok-v4-1');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (3, 2, 'path-fallback', 'path-v4-003', 'C:\\FixtureRoot\\Assets\\Logo.png', 'c:\\fixtureroot\\assets\\logo.png', 'Logo.png', '.png', 32768, '2026-09-02T14:30:00Z', 'path', 'Diagnostic v4', 1, 'tok-v4-2');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (4, 2, 'path-fallback', 'path-v4-004', 'C:\\FixtureRoot\\Assets\\OldLogo.png', 'c:\\fixtureroot\\assets\\oldlogo.png', 'OldLogo.png', '.png', 16384, '2026-09-02T10:00:00Z', 'path', 'Missing v4', 0, 'tok-v4-2');",
                transaction);

            // Tags (v4 has source in tags!):
            // Include same name with different source!
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (1, 'Archive', 'archive', 'user');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (2, 'Archive', 'archive', 'automatic');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (3, 'Starred', 'starred', 'user');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (4, 'AutoImage', 'autoimage', 'automatic');", transaction);

            // File Tags:
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 4, 'automatic');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (2, 3, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (3, 2, 'automatic');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (3, 3, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (4, 1, 'user');", transaction);

            transaction.Commit();
        }

        return path;
    }

    public static string CreateVersion5Database(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"GuraFile.Fixture.v5.{Guid.NewGuid():N}.db");

        using (var connection = SqliteDatabase.Open(path, 5))
        using (var transaction = connection.BeginTransaction())
        {
            // Roots: 3 roots covering online, offline, recovering status
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (1, 'C:\\FixtureRoot\\Active', 'c:\\fixtureroot\\active', 'online', NULL, '2026-09-03T10:00:00Z');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (2, 'C:\\FixtureRoot\\Unplugged', 'c:\\fixtureroot\\unplugged', 'offline', 'Volume disconnected', '2026-09-03T09:00:00Z');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (3, 'C:\\FixtureRoot\\Recovering', 'c:\\fixtureroot\\recovering', 'recovering', 'Sync in progress', '2026-09-03T09:30:00Z');", transaction);

            // Files: 3 files
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (1, 1, 'vol-v5-01', 'file-v5-001', 'C:\\FixtureRoot\\Active\\Doc1.txt', 'c:\\fixtureroot\\active\\doc1.txt', 'Doc1.txt', '.txt', 100, '2026-09-03T10:00:00Z', 'stable', NULL, 1, 'tok-5-1');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (2, 2, 'vol-v5-02', 'file-v5-002', 'C:\\FixtureRoot\\Unplugged\\Doc2.txt', 'c:\\fixtureroot\\unplugged\\doc2.txt', 'Doc2.txt', '.txt', 200, '2026-09-03T09:00:00Z', 'stable', NULL, 0, 'tok-5-2');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (3, 3, 'path-fallback', 'path-v5-003', 'C:\\FixtureRoot\\Recovering\\Doc3.txt', 'c:\\fixtureroot\\recovering\\doc3.txt', 'Doc3.txt', '.txt', 300, '2026-09-03T09:30:00Z', 'path', 'Diagnostic v5', 1, 'tok-5-3');",
                transaction);

            // Tags:
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (1, 'TagV5', 'tagv5', 'user');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (2, 'TagV5', 'tagv5', 'automatic');", transaction);

            // File Tags:
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 2, 'automatic');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (2, 1, 'user');", transaction);

            transaction.Commit();
        }

        return path;
    }

    public static string CreateVersion6Database(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"GuraFile.Fixture.v6.{Guid.NewGuid():N}.db");

        using (var connection = SqliteDatabase.Open(path, 6))
        using (var transaction = connection.BeginTransaction())
        {
            // Roots: 3 roots covering online, offline, recovering status
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (1, 'C:\\FixtureRoot\\Primary', 'c:\\fixtureroot\\primary', 'online', NULL, '2026-09-04T10:00:00Z');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (2, 'C:\\FixtureRoot\\Secondary', 'c:\\fixtureroot\\secondary', 'offline', 'Device unplugged', '2026-09-04T09:00:00Z');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (3, 'C:\\FixtureRoot\\Recover', 'c:\\fixtureroot\\recover', 'recovering', 'Crash recovery', '2026-09-04T09:30:00Z');", transaction);

            // Files: 3 files
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (1, 1, 'vol-v6-01', 'file-v6-001', 'C:\\FixtureRoot\\Primary\\Doc1.txt', 'c:\\fixtureroot\\primary\\doc1.txt', 'Doc1.txt', '.txt', 100, '2026-09-04T10:00:00Z', 'stable', NULL, 1, 'tok-6-1');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (2, 2, 'vol-v6-02', 'file-v6-002', 'C:\\FixtureRoot\\Secondary\\Doc2.txt', 'c:\\fixtureroot\\secondary\\doc2.txt', 'Doc2.txt', '.txt', 200, '2026-09-04T09:00:00Z', 'stable', NULL, 0, 'tok-6-2');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (3, 3, 'path-fallback', 'path-v6-003', 'C:\\FixtureRoot\\Recover\\Doc3.txt', 'c:\\fixtureroot\\recover\\doc3.txt', 'Doc3.txt', '.txt', 300, '2026-09-04T09:30:00Z', 'path', 'Diagnostic v6', 1, 'tok-6-3');",
                transaction);

            // Tags:
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (1, 'TagV6', 'tagv6', 'user');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (2, 'TagV6', 'tagv6', 'automatic');", transaction);

            // File Tags:
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 2, 'automatic');", transaction);

            // Scan Sessions:
            // 1 completed session for root 1
            Execute(connection,
                "INSERT INTO scan_sessions (id, root_id, scan_token, scan_type, status, started_utc, completed_utc) " +
                "VALUES (1, 1, 'tok-6-1', 'full', 'completed', '2026-09-04T09:59:00Z', '2026-09-04T10:00:00Z');",
                transaction);
            // 1 interrupted session for root 2
            Execute(connection,
                "INSERT INTO scan_sessions (id, root_id, scan_token, scan_type, status, started_utc, completed_utc) " +
                "VALUES (2, 2, 'tok-6-2', 'recovery', 'interrupted', '2026-09-04T08:50:00Z', '2026-09-04T08:55:00Z');",
                transaction);
            // 1 running session for root 3 (simulating uncompleted scan session)
            Execute(connection,
                "INSERT INTO scan_sessions (id, root_id, scan_token, scan_type, status, started_utc, completed_utc) " +
                "VALUES (3, 3, 'tok-6-3', 'recovery', 'running', '2026-09-04T09:30:00Z', NULL);",
                transaction);

            transaction.Commit();
        }

        return path;
    }

    public static string CreateVersion7Database(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"GuraFile.Fixture.v7.{Guid.NewGuid():N}.db");

        using (var connection = SqliteDatabase.Open(path, 7))
        using (var transaction = connection.BeginTransaction())
        {
            // Roots: 3 roots covering online, offline, recovering status
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (1, 'C:\\FixtureRoot\\Primary', 'c:\\fixtureroot\\primary', 'online', NULL, '2026-09-04T10:00:00Z');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (2, 'C:\\FixtureRoot\\Secondary', 'c:\\fixtureroot\\secondary', 'offline', 'Device unplugged', '2026-09-04T09:00:00Z');", transaction);
            Execute(connection, "INSERT INTO roots (id, path, normalized_path, status, last_error, last_checked_utc) VALUES (3, 'C:\\FixtureRoot\\Recover', 'c:\\fixtureroot\\recover', 'recovering', 'Crash recovery', '2026-09-04T09:30:00Z');", transaction);

            // Files: 3 files
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (1, 1, 'vol-v7-01', 'file-v7-001', 'C:\\FixtureRoot\\Primary\\Doc1.txt', 'c:\\fixtureroot\\primary\\doc1.txt', 'Doc1.txt', '.txt', 100, '2026-09-04T10:00:00Z', 'stable', NULL, 1, 'tok-7-1');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (2, 2, 'vol-v7-02', 'file-v7-002', 'C:\\FixtureRoot\\Secondary\\Doc2.txt', 'c:\\fixtureroot\\secondary\\doc2.txt', 'Doc2.txt', '.txt', 200, '2026-09-04T09:00:00Z', 'stable', NULL, 0, 'tok-7-2');",
                transaction);
            Execute(connection,
                "INSERT INTO files (id, root_id, volume_id, file_id, path, normalized_path, name, extension, size, modified_utc, identity_kind, identity_diagnostic, is_online, scan_token) " +
                "VALUES (3, 3, 'path-fallback', 'path-v7-003', 'C:\\FixtureRoot\\Recover\\Doc3.txt', 'c:\\fixtureroot\\recover\\doc3.txt', 'Doc3.txt', '.txt', 300, '2026-09-04T09:30:00Z', 'path', 'Diagnostic v7', 1, 'tok-7-3');",
                transaction);

            // Tags:
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (1, 'TagV7', 'tagv7', 'user');", transaction);
            Execute(connection, "INSERT INTO tags (id, name, normalized_name, source) VALUES (2, 'TagV7', 'tagv7', 'automatic');", transaction);

            // File Tags:
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 1, 'user');", transaction);
            Execute(connection, "INSERT INTO file_tags (file_id, tag_id, source) VALUES (1, 2, 'automatic');", transaction);

            // Scan Sessions:
            Execute(connection,
                "INSERT INTO scan_sessions (id, root_id, scan_token, scan_type, status, started_utc, completed_utc) " +
                "VALUES (1, 1, 'tok-7-1', 'full', 'completed', '2026-09-04T09:59:00Z', '2026-09-04T10:00:00Z');",
                transaction);

            // File Operation Intents:
            Execute(connection,
                "INSERT INTO file_operation_intents (id, correlation_id, operation_type, collision_policy, status, created_utc, completed_utc) " +
                "VALUES (1, 'corr-v7-1', 'copy', 'auto_rename', 'committed', '2026-09-05T01:00:00Z', '2026-09-05T01:00:02Z');",
                transaction);
            Execute(connection,
                "INSERT INTO file_operation_intents (id, correlation_id, operation_type, collision_policy, status, created_utc, completed_utc) " +
                "VALUES (2, 'corr-v7-2', 'move', 'auto_rename', 'indeterminate', '2026-09-05T01:05:00Z', '2026-09-05T01:05:05Z');",
                transaction);
            Execute(connection,
                "INSERT INTO file_operation_intents (id, correlation_id, operation_type, collision_policy, status, created_utc, completed_utc) " +
                "VALUES (3, 'corr-v7-3', 'recycle_bin_delete', 'auto_rename', 'pending', '2026-09-05T01:10:00Z', NULL);",
                transaction);

            // File Operation Intent Items:
            Execute(connection,
                "INSERT INTO file_operation_intent_items (id, intent_id, source_path, destination_directory, target_name, expected_target_path, actual_target_path, shell_status, commit_status, error) " +
                "VALUES (1, 1, 'C:\\FixtureRoot\\Primary\\Doc1.txt', 'C:\\FixtureRoot\\Primary\\Backup', 'Doc1.txt', 'C:\\FixtureRoot\\Primary\\Backup\\Doc1.txt', 'C:\\FixtureRoot\\Primary\\Backup\\Doc1.txt', 'completed', 'committed', NULL);",
                transaction);
            Execute(connection,
                "INSERT INTO file_operation_intent_items (id, intent_id, source_path, destination_directory, target_name, expected_target_path, actual_target_path, shell_status, commit_status, error) " +
                "VALUES (2, 2, 'C:\\FixtureRoot\\Primary\\Old.txt', 'C:\\FixtureRoot\\Primary\\New', 'Old.txt', 'C:\\FixtureRoot\\Primary\\New\\Old.txt', NULL, 'unknown', 'indeterminate', 'Ambiguous crash');",
                transaction);
            Execute(connection,
                "INSERT INTO file_operation_intent_items (id, intent_id, source_path, destination_directory, target_name, expected_target_path, actual_target_path, shell_status, commit_status, error) " +
                "VALUES (3, 3, 'C:\\FixtureRoot\\Secondary\\Doc2.txt', NULL, NULL, NULL, NULL, NULL, 'pending', NULL);",
                transaction);

            transaction.Commit();
        }

        return path;
    }

    public static SqliteConnection OpenRaw(string path)
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

    public static long GetUserVersion(string path)
    {
        using var connection = OpenRaw(path);
        return Scalar<long>(connection, "PRAGMA user_version;");
    }

    public static string GetJournalMode(string path)
    {
        using var connection = OpenRaw(path);
        return Scalar<string>(connection, "PRAGMA journal_mode;");
    }

    public static List<string> GetTableNames(SqliteConnection connection)
    {
        var list = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public static void AssertForeignKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        var violations = new List<string>();
        while (reader.Read())
        {
            violations.Add($"Table={reader.GetString(0)}, RowId={reader.GetInt64(1)}, Parent={reader.GetString(2)}, FkId={reader.GetInt32(3)}");
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException($"Foreign key integrity violated: {string.Join("; ", violations)}");
        }
    }

    public static void AssertNoTemporaryTables(SqliteConnection connection)
    {
        var temporaryNames = new[] { "file_tags_v2", "files_v3", "file_tags_v3", "tags_v4", "file_tags_v4" };
        var tables = GetTableNames(connection);
        var leftovers = tables.Intersect(temporaryNames).ToList();
        if (leftovers.Count > 0)
        {
            throw new InvalidOperationException($"Found leftover temporary migration tables: {string.Join(", ", leftovers)}");
        }
    }

    public static T Scalar<T>(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var result = command.ExecuteScalar();
        if (result is DBNull || result is null)
        {
            return default!;
        }
        return (T)Convert.ChangeType(result, typeof(T))!;
    }

    public static void Execute(
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
}
