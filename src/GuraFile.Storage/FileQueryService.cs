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

public enum TagMatchMode
{
    Any,
    All
}

public sealed record FileQuery(
    string? Search = null,
    FileSortColumn SortBy = FileSortColumn.Name,
    bool Descending = false,
    IReadOnlyList<long>? TagIds = null,
    TagMatchMode TagMatch = TagMatchMode.Any);

public sealed record IndexedFile(
    long Id,
    string Name,
    string Path,
    string Extension,
    long Size,
    DateTimeOffset Modified,
    bool IsOnline,
    string? Diagnostic,
    string IdentityKind = "stable")
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
            var filters = new List<string>();
            var sortColumn = query.SortBy switch
            {
                FileSortColumn.Name => "f.name COLLATE NOCASE",
                FileSortColumn.Path => "f.path COLLATE NOCASE",
                FileSortColumn.Extension => "f.extension COLLATE NOCASE",
                FileSortColumn.Size => "f.size",
                FileSortColumn.Modified => "f.modified_utc",
                _ => throw new ArgumentOutOfRangeException(nameof(query), query.SortBy, "Unsupported sort column.")
            };
            var direction = query.Descending ? "DESC" : "ASC";
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                filters.Add(
                    "(f.name LIKE $search ESCAPE '\\' COLLATE NOCASE " +
                    "OR f.path LIKE $search ESCAPE '\\' COLLATE NOCASE " +
                    "OR f.extension LIKE $search ESCAPE '\\' COLLATE NOCASE)");
                command.Parameters.AddWithValue("$search", $"%{EscapeLike(query.Search.Trim())}%");
            }

            var tagIds = query.TagIds?.Distinct().ToArray() ?? [];
            if (tagIds.Any(tagId => tagId <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(query), "Tag IDs must be positive.");
            }

            if (query.TagIds is not null && tagIds.Length == 0)
            {
                if (query.TagMatch == TagMatchMode.Any)
                {
                    filters.Add("0 = 1");
                }
                else if (query.TagMatch != TagMatchMode.All)
                {
                    throw new ArgumentOutOfRangeException(nameof(query), query.TagMatch, "Unsupported tag match mode.");
                }
            }
            else if (tagIds.Length > 0)
            {
                var placeholders = new string[tagIds.Length];
                for (var index = 0; index < tagIds.Length; index++)
                {
                    placeholders[index] = $"$tag{index}";
                    command.Parameters.AddWithValue(placeholders[index], tagIds[index]);
                }

                var tagList = string.Join(", ", placeholders);
                filters.Add(query.TagMatch switch
                {
                    TagMatchMode.Any =>
                        $"EXISTS (SELECT 1 FROM file_tags ft WHERE ft.file_id = f.id AND ft.tag_id IN ({tagList}))",
                    TagMatchMode.All =>
                        $"(SELECT COUNT(DISTINCT ft.tag_id) FROM file_tags ft WHERE ft.file_id = f.id AND ft.tag_id IN ({tagList})) = $tagCount",
                    _ => throw new ArgumentOutOfRangeException(nameof(query), query.TagMatch, "Unsupported tag match mode.")
                });
                if (query.TagMatch == TagMatchMode.All)
                {
                    command.Parameters.AddWithValue("$tagCount", tagIds.Length);
                }
            }

            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
            command.CommandText =
                $"""
                SELECT f.id, f.name, f.path, f.extension, f.size, f.modified_utc, f.is_online, f.identity_diagnostic, f.identity_kind
                FROM files f
                {where}
                ORDER BY {sortColumn} {direction}, f.id {direction};
                """;

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
                    ParseModifiedUtc(reader.GetString(5)),
                    reader.GetInt64(6) != 0,
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? "stable" : reader.GetString(8)));
            }

            return files;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static DateTimeOffset ParseModifiedUtc(string raw)
    {
        if (DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
