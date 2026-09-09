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
    TagMatchMode TagMatch = TagMatchMode.Any,
    int? Limit = null,
    int? Offset = null);

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
            var joinSearch = false;
            string? searchSubquery = null;
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var tokens = FtsQueryBuilder.ExtractTokens(query.Search);
                if (tokens.Count > 0)
                {
                    joinSearch = true;
                    var ftsQuery = FtsQueryBuilder.Build(query.Search)!;
                    command.Parameters.AddWithValue("$ftsQuery", ftsQuery);

                    var likeConditions = new List<string>(tokens.Count);
                    for (var i = 0; i < tokens.Count; i++)
                    {
                        var paramName = $"$likePattern{i}";
                        command.Parameters.AddWithValue(paramName, $"%{FtsQueryBuilder.EscapeLikePattern(tokens[i])}%");
                        likeConditions.Add($"(f_sub.name LIKE {paramName} ESCAPE '\\' OR f_sub.path LIKE {paramName} ESCAPE '\\')");
                    }

                    var likeClause = string.Join(" AND ", likeConditions);
                    searchSubquery =
                        $"""
                        (
                            SELECT rowid AS id FROM files_fts WHERE files_fts MATCH $ftsQuery
                            UNION
                            SELECT f_sub.id FROM files f_sub WHERE {likeClause}
                        )
                        """;
                }
                else
                {
                    filters.Add("0 = 1");
                }
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

            if (query.Limit is not null)
            {
                if (query.Limit < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(query), "Limit must be non-negative.");
                }

                var offset = query.Offset ?? 0;
                if (offset < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(query), "Offset must be non-negative.");
                }

                command.Parameters.AddWithValue("$limit", query.Limit.Value);
                command.Parameters.AddWithValue("$offset", offset);
            }
            else if (query.Offset is not null)
            {
                if (query.Offset < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(query), "Offset must be non-negative.");
                }
            }

            var limitClause = query.Limit is not null ? "\nLIMIT $limit OFFSET $offset" : "";
            var fromClause = joinSearch
                ? $"FROM files f JOIN {searchSubquery} matched ON matched.id = f.id"
                : "FROM files f";
            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
            command.CommandText =
                $"""
                SELECT f.id, f.name, f.path, f.extension, f.size, f.modified_utc, f.is_online, f.identity_diagnostic, f.identity_kind
                {fromClause}
                {where}
                ORDER BY {sortColumn} {direction}, f.id {direction}{limitClause};
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

    public Task RebuildSearchIndexAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = SqliteDatabase.Open(DatabasePath);
            SqliteDatabase.RebuildSearchIndex(connection);
        }, cancellationToken);

    private static DateTimeOffset ParseModifiedUtc(string raw)
    {
        if (DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
