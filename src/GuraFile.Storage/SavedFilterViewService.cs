using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public sealed record SavedFilterView(
    long Id,
    string Name,
    string? SearchText,
    FileSortColumn SortColumn,
    bool SortDescending,
    TagMatchMode TagMatchMode,
    bool IsTagFilterEnabled,
    int SortOrder,
    IReadOnlyList<long> TagIds,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    bool HasInvalidTags = false,
    IReadOnlyList<long>? MissingTagIds = null)
{
    public string DisplayName => HasInvalidTags ? $"{Name} [失效]" : Name;
}

public sealed class SavedFilterViewService
{
    public SavedFilterViewService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        using var _ = SqliteDatabase.Open(DatabasePath);
    }

    public string DatabasePath { get; }

    public SavedFilterView CreateView(
        string name,
        string? searchText,
        FileSortColumn sortColumn,
        bool sortDescending,
        TagMatchMode tagMatchMode,
        bool isTagFilterEnabled,
        IReadOnlyCollection<long>? tagIds = null)
    {
        var (displayName, normalizedName) = NormalizeName(name);
        var distinctTagIds = tagIds?.Distinct().ToArray() ?? [];
        if (distinctTagIds.Any(id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tagIds), "标签 ID 必须为正整数。");
        }

        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();

        if (distinctTagIds.Length > 0)
        {
            EnsureTagsExist(connection, transaction, distinctTagIds);
        }

        long nextOrder;
        using (var orderCmd = connection.CreateCommand())
        {
            orderCmd.Transaction = transaction;
            orderCmd.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM saved_filter_views;";
            nextOrder = Convert.ToInt64(orderCmd.ExecuteScalar());
        }

        var now = DateTimeOffset.UtcNow;
        var nowUtc = now.ToString("O");
        long viewId;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO saved_filter_views (
                    name, normalized_name, search_text, sort_column, sort_descending,
                    tag_match_mode, is_tag_filter_enabled, sort_order, created_utc, updated_utc
                ) VALUES (
                    $name, $normalizedName, $searchText, $sortColumn, $sortDescending,
                    $tagMatchMode, $isTagFilterEnabled, $sortOrder, $createdUtc, $updatedUtc
                ) RETURNING id;
                """;
            command.Parameters.AddWithValue("$name", displayName);
            command.Parameters.AddWithValue("$normalizedName", normalizedName);
            command.Parameters.AddWithValue("$searchText", (object?)searchText ?? DBNull.Value);
            command.Parameters.AddWithValue("$sortColumn", sortColumn.ToString());
            command.Parameters.AddWithValue("$sortDescending", sortDescending ? 1 : 0);
            command.Parameters.AddWithValue("$tagMatchMode", tagMatchMode.ToString());
            command.Parameters.AddWithValue("$isTagFilterEnabled", isTagFilterEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$sortOrder", nextOrder);
            command.Parameters.AddWithValue("$createdUtc", nowUtc);
            command.Parameters.AddWithValue("$updatedUtc", nowUtc);

            try
            {
                viewId = Convert.ToInt64(command.ExecuteScalar());
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException($"已保存视图“{displayName}”已存在。", ex);
            }
        }

        if (distinctTagIds.Length > 0)
        {
            using var tagInsertCmd = connection.CreateCommand();
            tagInsertCmd.Transaction = transaction;
            tagInsertCmd.CommandText = "INSERT INTO saved_filter_view_tags (view_id, tag_id) VALUES ($viewId, $tagId);";
            var pViewId = tagInsertCmd.Parameters.Add("$viewId", SqliteType.Integer);
            var pTagId = tagInsertCmd.Parameters.Add("$tagId", SqliteType.Integer);
            pViewId.Value = viewId;

            foreach (var tagId in distinctTagIds)
            {
                pTagId.Value = tagId;
                tagInsertCmd.ExecuteNonQuery();
            }
        }

        transaction.Commit();

        return new SavedFilterView(
            viewId,
            displayName,
            searchText,
            sortColumn,
            sortDescending,
            tagMatchMode,
            isTagFilterEnabled,
            (int)nextOrder,
            distinctTagIds,
            now,
            now,
            HasInvalidTags: false,
            MissingTagIds: null);
    }

    public IReadOnlyList<SavedFilterView> ListViews()
    {
        using var connection = SqliteDatabase.Open(DatabasePath);

        var existingTagIds = new HashSet<long>();
        using (var tagCmd = connection.CreateCommand())
        {
            tagCmd.CommandText = "SELECT id FROM tags;";
            using var tagReader = tagCmd.ExecuteReader();
            while (tagReader.Read())
            {
                existingTagIds.Add(tagReader.GetInt64(0));
            }
        }

        var viewTags = new Dictionary<long, List<long>>();
        using (var vtCmd = connection.CreateCommand())
        {
            vtCmd.CommandText = "SELECT view_id, tag_id FROM saved_filter_view_tags ORDER BY view_id, tag_id;";
            using var vtReader = vtCmd.ExecuteReader();
            while (vtReader.Read())
            {
                var viewId = vtReader.GetInt64(0);
                var tagId = vtReader.GetInt64(1);
                if (!viewTags.TryGetValue(viewId, out var list))
                {
                    list = [];
                    viewTags[viewId] = list;
                }
                list.Add(tagId);
            }
        }

        var views = new List<SavedFilterView>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, name, search_text, sort_column, sort_descending, tag_match_mode, is_tag_filter_enabled, sort_order, created_utc, updated_utc
                FROM saved_filter_views
                ORDER BY sort_order ASC, id ASC;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var name = reader.GetString(1);
                var searchText = reader.IsDBNull(2) ? null : reader.GetString(2);
                var sortColumn = Enum.TryParse<FileSortColumn>(reader.GetString(3), out var sc) ? sc : FileSortColumn.Name;
                var sortDescending = reader.GetInt64(4) != 0;
                var tagMatchMode = Enum.TryParse<TagMatchMode>(reader.GetString(5), out var tmm) ? tmm : TagMatchMode.Any;
                var isTagFilterEnabled = reader.GetInt64(6) != 0;
                var sortOrder = reader.GetInt32(7);
                var createdUtc = ParseUtc(reader.GetString(8));
                var updatedUtc = ParseUtc(reader.GetString(9));

                var tagIds = viewTags.TryGetValue(id, out var tags) ? (IReadOnlyList<long>)tags : Array.Empty<long>();
                var missing = tagIds.Where(tid => !existingTagIds.Contains(tid)).ToArray();
                var hasInvalidTags = missing.Length > 0;

                views.Add(new SavedFilterView(
                    id,
                    name,
                    searchText,
                    sortColumn,
                    sortDescending,
                    tagMatchMode,
                    isTagFilterEnabled,
                    sortOrder,
                    tagIds,
                    createdUtc,
                    updatedUtc,
                    hasInvalidTags,
                    hasInvalidTags ? missing : null));
            }
        }

        return views;
    }

    public SavedFilterView? GetViewById(long id)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);

        var existingTagIds = new HashSet<long>();
        using (var tagCmd = connection.CreateCommand())
        {
            tagCmd.CommandText = "SELECT id FROM tags;";
            using var tagReader = tagCmd.ExecuteReader();
            while (tagReader.Read())
            {
                existingTagIds.Add(tagReader.GetInt64(0));
            }
        }

        var tagIds = new List<long>();
        using (var vtCmd = connection.CreateCommand())
        {
            vtCmd.CommandText = "SELECT tag_id FROM saved_filter_view_tags WHERE view_id = $viewId ORDER BY tag_id;";
            vtCmd.Parameters.AddWithValue("$viewId", id);
            using var vtReader = vtCmd.ExecuteReader();
            while (vtReader.Read())
            {
                tagIds.Add(vtReader.GetInt64(0));
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, search_text, sort_column, sort_descending, tag_match_mode, is_tag_filter_enabled, sort_order, created_utc, updated_utc
            FROM saved_filter_views
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var name = reader.GetString(1);
        var searchText = reader.IsDBNull(2) ? null : reader.GetString(2);
        var sortColumn = Enum.TryParse<FileSortColumn>(reader.GetString(3), out var sc) ? sc : FileSortColumn.Name;
        var sortDescending = reader.GetInt64(4) != 0;
        var tagMatchMode = Enum.TryParse<TagMatchMode>(reader.GetString(5), out var tmm) ? tmm : TagMatchMode.Any;
        var isTagFilterEnabled = reader.GetInt64(6) != 0;
        var sortOrder = reader.GetInt32(7);
        var createdUtc = ParseUtc(reader.GetString(8));
        var updatedUtc = ParseUtc(reader.GetString(9));

        var missing = tagIds.Where(tid => !existingTagIds.Contains(tid)).ToArray();
        var hasInvalidTags = missing.Length > 0;

        return new SavedFilterView(
            id,
            name,
            searchText,
            sortColumn,
            sortDescending,
            tagMatchMode,
            isTagFilterEnabled,
            sortOrder,
            tagIds,
            createdUtc,
            updatedUtc,
            hasInvalidTags,
            hasInvalidTags ? missing : null);
    }

    public SavedFilterView RenameView(long id, string newName)
    {
        var (displayName, normalizedName) = NormalizeName(newName);
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();
        var nowUtc = DateTimeOffset.UtcNow.ToString("O");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE saved_filter_views
            SET name = $name, normalized_name = $normalizedName, updated_utc = $updatedUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$normalizedName", normalizedName);
        command.Parameters.AddWithValue("$updatedUtc", nowUtc);
        command.Parameters.AddWithValue("$id", id);

        try
        {
            if (command.ExecuteNonQuery() == 0)
            {
                throw new ArgumentException("视图不存在。", nameof(id));
            }

            transaction.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"已保存视图“{displayName}”已存在。", ex);
        }

        return GetViewById(id)!;
    }

    public SavedFilterView UpdateViewFilter(
        long id,
        string? searchText,
        FileSortColumn sortColumn,
        bool sortDescending,
        TagMatchMode tagMatchMode,
        bool isTagFilterEnabled,
        IReadOnlyCollection<long>? tagIds = null)
    {
        var distinctTagIds = tagIds?.Distinct().ToArray() ?? [];
        if (distinctTagIds.Any(tid => tid <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tagIds), "标签 ID 必须为正整数。");
        }

        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();

        if (distinctTagIds.Length > 0)
        {
            EnsureTagsExist(connection, transaction, distinctTagIds);
        }

        var nowUtc = DateTimeOffset.UtcNow.ToString("O");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE saved_filter_views
                SET search_text = $searchText, sort_column = $sortColumn, sort_descending = $sortDescending,
                    tag_match_mode = $tagMatchMode, is_tag_filter_enabled = $isTagFilterEnabled, updated_utc = $updatedUtc
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$searchText", (object?)searchText ?? DBNull.Value);
            command.Parameters.AddWithValue("$sortColumn", sortColumn.ToString());
            command.Parameters.AddWithValue("$sortDescending", sortDescending ? 1 : 0);
            command.Parameters.AddWithValue("$tagMatchMode", tagMatchMode.ToString());
            command.Parameters.AddWithValue("$isTagFilterEnabled", isTagFilterEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$updatedUtc", nowUtc);
            command.Parameters.AddWithValue("$id", id);

            if (command.ExecuteNonQuery() == 0)
            {
                throw new ArgumentException("视图不存在。", nameof(id));
            }
        }

        using (var deleteTagsCmd = connection.CreateCommand())
        {
            deleteTagsCmd.Transaction = transaction;
            deleteTagsCmd.CommandText = "DELETE FROM saved_filter_view_tags WHERE view_id = $id;";
            deleteTagsCmd.Parameters.AddWithValue("$id", id);
            deleteTagsCmd.ExecuteNonQuery();
        }

        if (distinctTagIds.Length > 0)
        {
            using var insertTagsCmd = connection.CreateCommand();
            insertTagsCmd.Transaction = transaction;
            insertTagsCmd.CommandText = "INSERT INTO saved_filter_view_tags (view_id, tag_id) VALUES ($viewId, $tagId);";
            var pViewId = insertTagsCmd.Parameters.Add("$viewId", SqliteType.Integer);
            var pTagId = insertTagsCmd.Parameters.Add("$tagId", SqliteType.Integer);
            pViewId.Value = id;

            foreach (var tagId in distinctTagIds)
            {
                pTagId.Value = tagId;
                insertTagsCmd.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return GetViewById(id)!;
    }

    public bool DeleteView(long id)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM saved_filter_views WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public void ReorderViews(IReadOnlyList<long> viewIds)
    {
        ArgumentNullException.ThrowIfNull(viewIds);
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();
        var nowUtc = DateTimeOffset.UtcNow.ToString("O");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE saved_filter_views SET sort_order = $sortOrder, updated_utc = $updatedUtc WHERE id = $id;";
        var pOrder = command.Parameters.Add("$sortOrder", SqliteType.Integer);
        var pUpdated = command.Parameters.Add("$updatedUtc", SqliteType.Text);
        var pId = command.Parameters.Add("$id", SqliteType.Integer);
        pUpdated.Value = nowUtc;

        for (var i = 0; i < viewIds.Count; i++)
        {
            pOrder.Value = i;
            pId.Value = viewIds[i];
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public FileQuery ToFileQuery(SavedFilterView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.IsTagFilterEnabled)
        {
            if (view.HasInvalidTags)
            {
                // When an invalid view (containing deleted tags) is executed,
                // we must not silently discard missing tags and broaden results.
                // Match 0 files by setting empty TagIds and TagMatchMode.Any (which produces 0 = 1 in FileQueryService).
                return new FileQuery(
                    Search: view.SearchText,
                    SortBy: view.SortColumn,
                    Descending: view.SortDescending,
                    TagIds: [],
                    TagMatch: TagMatchMode.Any);
            }

            return new FileQuery(
                Search: view.SearchText,
                SortBy: view.SortColumn,
                Descending: view.SortDescending,
                TagIds: view.TagIds,
                TagMatch: view.TagMatchMode);
        }

        return new FileQuery(
            Search: view.SearchText,
            SortBy: view.SortColumn,
            Descending: view.SortDescending,
            TagIds: null,
            TagMatch: view.TagMatchMode);
    }

    internal static (string DisplayName, string NormalizedName) NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var displayName = name.Trim().Normalize(NormalizationForm.FormC);
        if (displayName.Length == 0)
        {
            throw new ArgumentException("视图名称不能为空。", nameof(name));
        }

        if (displayName.Length > 100)
        {
            throw new ArgumentException("视图名称不能超过 100 个字符。", nameof(name));
        }

        var normalizedName = displayName.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        return (displayName, normalizedName);
    }

    private static void EnsureTagsExist(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> tagIds)
    {
        var placeholders = new string[tagIds.Count];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        for (var i = 0; i < tagIds.Count; i++)
        {
            placeholders[i] = $"$tag{i}";
            command.Parameters.AddWithValue(placeholders[i], tagIds[i]);
        }

        command.CommandText = $"SELECT id FROM tags WHERE id IN ({string.Join(", ", placeholders)});";
        var existing = new HashSet<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            existing.Add(reader.GetInt64(0));
        }

        foreach (var tagId in tagIds)
        {
            if (!existing.Contains(tagId))
            {
                throw new ArgumentException($"标签不存在：{tagId}。", nameof(tagIds));
            }
        }
    }

    private static DateTimeOffset ParseUtc(string raw)
    {
        if (DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
