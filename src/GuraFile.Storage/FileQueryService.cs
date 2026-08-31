using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public enum FileSortColumn
{
    Name,
    Path,
    Extension,
    Size,
    Modified
}

public sealed record FileQuery(
    string? Search = null,
    FileSortColumn SortBy = FileSortColumn.Name,
    bool Descending = false);

public sealed record IndexedFile(
    long Id,
    string Name,
    string Path,
    string Extension,
    long Size,
    DateTimeOffset Modified,
    bool IsOnline,
    string? Diagnostic)
{
    public string Status => IsOnline ? "在线" : "离线";
}

public sealed class FileQueryService
{
    public FileQueryService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = System.IO.Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public Task<IReadOnlyList<IndexedFile>> QueryAsync(
        FileQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => Query(query, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<IndexedFile> Query(FileQuery query, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = SqliteDatabase.Open(DatabasePath);
            using var command = connection.CreateCommand();
            var sortColumn = query.SortBy switch
            {
                FileSortColumn.Name => "name COLLATE NOCASE",
                FileSortColumn.Path => "path COLLATE NOCASE",
                FileSortColumn.Extension => "extension COLLATE NOCASE",
                FileSortColumn.Size => "size",
                FileSortColumn.Modified => "modified_utc",
                _ => throw new ArgumentOutOfRangeException(nameof(query), query.SortBy, "Unsupported sort column.")
            };
            var direction = query.Descending ? "DESC" : "ASC";
            var where = string.IsNullOrWhiteSpace(query.Search)
                ? ""
                : "WHERE name LIKE $search ESCAPE '\\' COLLATE NOCASE OR path LIKE $search ESCAPE '\\' COLLATE NOCASE OR extension LIKE $search ESCAPE '\\' COLLATE NOCASE";
            command.CommandText =
                $"""
                SELECT id, name, path, extension, size, modified_utc, is_online, identity_diagnostic
                FROM files
                {where}
                ORDER BY {sortColumn} {direction}, id {direction};
                """;
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                command.Parameters.AddWithValue("$search", $"%{EscapeLike(query.Search.Trim())}%");
            }

            using var registration = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            var files = new List<IndexedFile>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetInt64(6) != 0,
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return files;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
