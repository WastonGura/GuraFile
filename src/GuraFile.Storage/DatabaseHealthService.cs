using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public enum DatabaseHealthStatus
{
    Healthy,
    Locked,
    UnsupportedFutureSchema,
    Corrupted,
    IoError
}

public sealed record DatabaseHealthResult(
    DatabaseHealthStatus Status,
    string? Message = null,
    int? UserVersion = null,
    Exception? Exception = null)
{
    public bool IsHealthy => Status == DatabaseHealthStatus.Healthy;
}

public sealed class DatabaseHealthService
{
    public static readonly byte[] SqliteHeaderMagic =
    [
        0x53, 0x51, 0x4c, 0x69, 0x74, 0x65, 0x20, 0x66,
        0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x33, 0x00
    ];

    public DatabaseHealthResult CheckHealth(string databasePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            return new(DatabaseHealthStatus.Healthy, "数据库文件尚不存在。");
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(2);

        // 1. Probing file stream: detect lock / sharing violations, minimum size, and magic header
        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length < 100)
            {
                return new(
                    DatabaseHealthStatus.Corrupted,
                    $"数据库文件过小或已截断（大小为 {fileInfo.Length} 字节，小于 SQLite 最小文件头 100 字节）。");
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[16];
            int read = stream.Read(header, 0, 16);
            if (read < 16 || !header.SequenceEqual(SqliteHeaderMagic))
            {
                return new(DatabaseHealthStatus.Corrupted, "数据库文件头无效，非合法 SQLite 3 格式数据库。");
            }
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return new(DatabaseHealthStatus.Locked, "数据库文件正被其他进程独占使用。", Exception: ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(DatabaseHealthStatus.IoError, $"无权限访问数据库文件：{ex.Message}", Exception: ex);
        }
        catch (IOException ex)
        {
            return new(DatabaseHealthStatus.IoError, $"读取数据库文件时发生 I/O 错误：{ex.Message}", Exception: ex);
        }

        // 2. Open SQLite connection with bounded busy timeout
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)effectiveTimeout.TotalSeconds)
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            // 3. Inspect user_version first to strictly avoid mutating journal_mode or schema for future schemas
            int version;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                cmd.CommandTimeout = Math.Max(1, (int)effectiveTimeout.TotalSeconds);
                var obj = cmd.ExecuteScalar();
                version = Convert.ToInt32(obj, CultureInfo.InvariantCulture);
            }

            if (version > SqliteDatabase.CurrentVersion)
            {
                return new(
                    DatabaseHealthStatus.UnsupportedFutureSchema,
                    $"数据库架构版本 v{version} 高于当前支持的版本 v{SqliteDatabase.CurrentVersion}，禁止写入或修改日志模式。",
                    UserVersion: version);
            }

            // 4. Run PRAGMA quick_check
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA quick_check;";
                cmd.CommandTimeout = Math.Max(1, (int)effectiveTimeout.TotalSeconds);
                using var reader = cmd.ExecuteReader();
                var errors = new List<string>();
                while (reader.Read())
                {
                    var text = reader.GetString(0);
                    if (!string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(text);
                    }
                }

                if (errors.Count > 0)
                {
                    return new(
                        DatabaseHealthStatus.Corrupted,
                        $"数据库完整性快速检查失败：{string.Join("; ", errors)}",
                        UserVersion: version);
                }
            }

            return new(
                DatabaseHealthStatus.Healthy,
                "数据库正常。",
                UserVersion: version);
        }
        catch (SqliteException ex)
        {
            return ClassifySqliteException(ex);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return new(DatabaseHealthStatus.Locked, "数据库文件正被其他进程使用。", Exception: ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(DatabaseHealthStatus.IoError, $"无权限访问数据库：{ex.Message}", Exception: ex);
        }
        catch (Exception ex)
        {
            return new(DatabaseHealthStatus.IoError, $"访问数据库时发生错误：{ex.Message}", Exception: ex);
        }
    }

    public static bool IsSharingViolation(IOException ex)
    {
        var win32Error = ex.HResult & 0xFFFF;
        return win32Error is 32 or 33;
    }

    private static DatabaseHealthResult ClassifySqliteException(SqliteException ex)
    {
        if (ex.InnerException is IOException ioEx && IsSharingViolation(ioEx))
        {
            return new(DatabaseHealthStatus.Locked, $"数据库文件正被其他进程独占：{ioEx.Message}", Exception: ex);
        }

        // SQLite error codes:
        // 5 = SQLITE_BUSY, 6 = SQLITE_LOCKED
        if (ex.SqliteErrorCode is 5 or 6)
        {
            return new(DatabaseHealthStatus.Locked, $"数据库正忙或已被锁定（SQLite 错误代码 {ex.SqliteErrorCode}）：{ex.Message}", Exception: ex);
        }

        // 11 = SQLITE_CORRUPT, 26 = SQLITE_NOTADB
        if (ex.SqliteErrorCode is 11 or 26)
        {
            return new(DatabaseHealthStatus.Corrupted, $"数据库损坏或非合法数据库（SQLite 错误代码 {ex.SqliteErrorCode}）：{ex.Message}", Exception: ex);
        }

        // Extended error codes: 3850 = SQLITE_IOERR_LOCK, 2826 = SQLITE_IOERR_BLOCKED
        if (ex.SqliteExtendedErrorCode is 3850 or 2826)
        {
            return new(DatabaseHealthStatus.Locked, $"数据库锁定冲突（SQLite 错误代码 {ex.SqliteExtendedErrorCode}）：{ex.Message}", Exception: ex);
        }

        // 10 = SQLITE_IOERR
        if (ex.SqliteErrorCode == 10)
        {
            return new(DatabaseHealthStatus.IoError, $"数据库 I/O 错误（SQLite 错误代码 {ex.SqliteErrorCode}）：{ex.Message}", Exception: ex);
        }

        return new(DatabaseHealthStatus.IoError, $"数据库发生错误（SQLite 错误代码 {ex.SqliteErrorCode}）：{ex.Message}", Exception: ex);
    }
}
