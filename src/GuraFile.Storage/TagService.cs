using System.Text;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public sealed record UserTag(long Id, string Name);
public sealed record AutomaticTag(long Id, string Name);

public sealed class TagService
{
    private readonly Func<string, FileTypeClassification> _classify;
    private readonly Func<string, FileIdentity> _readIdentity;

    public TagService(string databasePath) :
        this(databasePath, new FileTypeClassifier().Classify, FileIdentityReader.Read)
    {
    }

    internal TagService(string databasePath, Func<string, FileTypeClassification> classify) :
        this(databasePath, classify, FileIdentityReader.Read)
    {
    }

    internal TagService(
        string databasePath,
        Func<string, FileTypeClassification> classify,
        Func<string, FileIdentity> readIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(classify);
        ArgumentNullException.ThrowIfNull(readIdentity);
        DatabasePath = Path.GetFullPath(databasePath);
        _classify = classify;
        _readIdentity = readIdentity;
        using var _ = SqliteDatabase.Open(DatabasePath);
    }

    public string DatabasePath { get; }

    public IReadOnlyList<UserTag> ListTags()
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name FROM tags WHERE source = 'user' ORDER BY name COLLATE NOCASE, id;";
        using var reader = command.ExecuteReader();
        var tags = new List<UserTag>();
        while (reader.Read())
        {
            tags.Add(new(reader.GetInt64(0), reader.GetString(1)));
        }

        return tags;
    }

    public IReadOnlyList<UserTag> ListTagsForFile(long fileId)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.id, t.name
            FROM tags t
            JOIN file_tags ft ON ft.tag_id = t.id
            WHERE ft.file_id = $fileId AND ft.source = 'user' AND t.source = 'user'
            ORDER BY t.name COLLATE NOCASE, t.id;
            """;
        command.Parameters.AddWithValue("$fileId", fileId);
        using var reader = command.ExecuteReader();
        var tags = new List<UserTag>();
        while (reader.Read())
        {
            tags.Add(new(reader.GetInt64(0), reader.GetString(1)));
        }

        return tags;
    }

    public IReadOnlyList<AutomaticTag> ListAutomaticTags()
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.id, t.name
            FROM tags t
            WHERE t.source = 'automatic'
              AND EXISTS (SELECT 1 FROM file_tags ft WHERE ft.tag_id = t.id AND ft.source = 'automatic')
            ORDER BY t.name COLLATE NOCASE, t.id;
            """;
        using var reader = command.ExecuteReader();
        var tags = new List<AutomaticTag>();
        while (reader.Read())
        {
            tags.Add(new(reader.GetInt64(0), reader.GetString(1)));
        }

        return tags;
    }

    public IReadOnlyList<AutomaticTag> ListAutomaticTagsForFile(long fileId)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.id, t.name
            FROM tags t
            JOIN file_tags ft ON ft.tag_id = t.id
            WHERE ft.file_id = $fileId AND ft.source = 'automatic' AND t.source = 'automatic'
            ORDER BY t.name COLLATE NOCASE, t.id;
            """;
        command.Parameters.AddWithValue("$fileId", fileId);
        using var reader = command.ExecuteReader();
        var tags = new List<AutomaticTag>();
        while (reader.Read())
        {
            tags.Add(new(reader.GetInt64(0), reader.GetString(1)));
        }

        return tags;
    }

    public FileTypeClassification ReclassifyFile(long fileId)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        FileNode node;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path, volume_id, file_id, is_online FROM files WHERE id = $fileId;";
            command.Parameters.AddWithValue("$fileId", fileId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new ArgumentException($"文件节点 {fileId} 不存在。", nameof(fileId));
            }

            node = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0);
        }

        if (!node.IsOnline)
        {
            throw new InvalidOperationException("离线文件无法重新识别；请先扫描其根目录。");
        }

        if (!MatchesIdentity(node, _readIdentity(node.Path)))
        {
            throw new InvalidOperationException("文件身份已变化；请先重新扫描其根目录。");
        }

        var classification = _classify(node.Path);
        using var transaction = connection.BeginTransaction();
        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText =
                "SELECT EXISTS(SELECT 1 FROM files WHERE id = $fileId AND path = $path AND volume_id = $volumeId AND file_id = $identity AND is_online = 1);";
            verify.Parameters.AddWithValue("$fileId", fileId);
            verify.Parameters.AddWithValue("$path", node.Path);
            verify.Parameters.AddWithValue("$volumeId", node.VolumeId);
            verify.Parameters.AddWithValue("$identity", node.FileId);
            if ((long)verify.ExecuteScalar()! == 0)
            {
                throw new InvalidOperationException("文件节点已变化；请重试。");
            }
        }

        if (!MatchesIdentity(node, _readIdentity(node.Path)))
        {
            throw new InvalidOperationException("文件身份在识别期间发生变化；请先重新扫描其根目录。");
        }

        ReplaceAutomaticTags(connection, transaction, fileId, classification.AutomaticTags);
        transaction.Commit();
        return classification;
    }

    public UserTag CreateTag(string name)
    {
        var (displayName, normalizedName) = NormalizeName(name);
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO tags (name, normalized_name, source) VALUES ($name, $normalizedName, 'user') RETURNING id;";
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$normalizedName", normalizedName);
        try
        {
            var tag = new UserTag((long)command.ExecuteScalar()!, displayName);
            transaction.Commit();
            return tag;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"标签“{displayName}”已存在。", exception);
        }
    }

    public UserTag RenameTag(long tagId, string name)
    {
        var (displayName, normalizedName) = NormalizeName(name);
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE tags SET name = $name, normalized_name = $normalizedName WHERE id = $tagId AND source = 'user';";
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$normalizedName", normalizedName);
        command.Parameters.AddWithValue("$tagId", tagId);
        try
        {
            if (command.ExecuteNonQuery() == 0)
            {
                throw new ArgumentException("标签不存在。", nameof(tagId));
            }

            transaction.Commit();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"标签“{displayName}”已存在。", exception);
        }

        return new(tagId, displayName);
    }

    public bool DeleteTag(long tagId)
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM tags WHERE id = $tagId AND source = 'user';";
        command.Parameters.AddWithValue("$tagId", tagId);
        var deleted = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return deleted;
    }

    public void AddTagToFiles(long tagId, IReadOnlyCollection<long> fileIds) =>
        ChangeRelations(tagId, fileIds, add: true);

    public void RemoveTagFromFiles(long tagId, IReadOnlyCollection<long> fileIds) =>
        ChangeRelations(tagId, fileIds, add: false);

    private void ChangeRelations(long tagId, IReadOnlyCollection<long> fileIds, bool add)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var distinctFileIds = fileIds.Distinct().ToArray();
        if (distinctFileIds.Length == 0)
        {
            return;
        }

        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();
        EnsureUserTagExists(connection, transaction, tagId);
        foreach (var fileId in distinctFileIds)
        {
            EnsureExists(connection, transaction, "files", fileId, $"文件节点 {fileId} 不存在。");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = add
                ? "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fileId, $tagId, 'user') ON CONFLICT DO NOTHING;"
                : "DELETE FROM file_tags WHERE file_id = $fileId AND tag_id = $tagId AND source = 'user';";
            command.Parameters.AddWithValue("$fileId", fileId);
            command.Parameters.AddWithValue("$tagId", tagId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void EnsureExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        long id,
        string message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE id = $id);";
        command.Parameters.AddWithValue("$id", id);
        if ((long)command.ExecuteScalar()! == 0)
        {
            throw new ArgumentException(message);
        }
    }

    private static void EnsureUserTagExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long tagId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM tags WHERE id = $id AND source = 'user');";
        command.Parameters.AddWithValue("$id", tagId);
        if ((long)command.ExecuteScalar()! == 0)
        {
            throw new ArgumentException("用户标签不存在。", nameof(tagId));
        }
    }

    internal static void ReplaceAutomaticTags(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        IReadOnlyList<string> tagNames)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM file_tags WHERE file_id = $fileId AND source = 'automatic';";
            delete.Parameters.AddWithValue("$fileId", fileId);
            delete.ExecuteNonQuery();
        }

        foreach (var name in tagNames.Distinct(StringComparer.Ordinal))
        {
            var (displayName, normalizedName) = NormalizeName(name);
            long tagId;
            using (var tag = connection.CreateCommand())
            {
                tag.Transaction = transaction;
                tag.CommandText =
                    """
                    INSERT INTO tags (name, normalized_name, source)
                    VALUES ($name, $normalizedName, 'automatic')
                    ON CONFLICT(normalized_name, source) DO UPDATE SET name = excluded.name
                    RETURNING id;
                    """;
                tag.Parameters.AddWithValue("$name", displayName);
                tag.Parameters.AddWithValue("$normalizedName", normalizedName);
                tagId = (long)tag.ExecuteScalar()!;
            }

            using var relation = connection.CreateCommand();
            relation.Transaction = transaction;
            relation.CommandText =
                "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fileId, $tagId, 'automatic');";
            relation.Parameters.AddWithValue("$fileId", fileId);
            relation.Parameters.AddWithValue("$tagId", tagId);
            relation.ExecuteNonQuery();
        }
    }

    internal static (string DisplayName, string NormalizedName) NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var displayName = name.Trim().Normalize(NormalizationForm.FormC);
        if (displayName.Length == 0)
        {
            throw new ArgumentException("标签名称不能为空。", nameof(name));
        }

        if (displayName.Length > 100)
        {
            throw new ArgumentException("标签名称不能超过 100 个字符。", nameof(name));
        }

        var normalizedName = displayName.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        return (displayName, normalizedName);
    }

    private static bool MatchesIdentity(FileNode node, FileIdentity identity) =>
        string.Equals(identity.VolumeId, node.VolumeId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(identity.FileId, node.FileId, StringComparison.OrdinalIgnoreCase);

    private sealed record FileNode(string Path, string VolumeId, string FileId, bool IsOnline);
}
