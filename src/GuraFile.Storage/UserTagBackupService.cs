using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GuraFile.Storage;

public sealed record TagImportConflict(string ImportedName, string ExistingName);

public sealed record MissingBackupFile(string Path, string Reason);

public sealed record TagImportResult(
    int CreatedTags,
    int ReusedTags,
    int RestoredRelations,
    IReadOnlyList<TagImportConflict> Conflicts,
    IReadOnlyList<MissingBackupFile> MissingFiles);

public sealed class UserTagBackupService
{
    private const string FormatName = "GuraFile.UserTags";
    private const int FormatVersion = 1;
    public const int MaximumBackupBytes = 64 * 1024 * 1024;
    private const int MaximumTags = 10_000;
    private const int MaximumFiles = 100_000;
    private const int MaximumTagsPerFile = 1_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 16
    };

    public UserTagBackupService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        using var _ = SqliteDatabase.Open(DatabasePath);
    }

    public string DatabasePath { get; }

    public string Export()
    {
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();
        var document = new BackupDocument
        {
            Format = FormatName,
            Version = FormatVersion,
            Tags = ReadTags(connection, transaction),
            Files = ReadFiles(connection, transaction)
        };
        transaction.Commit();
        _ = Validate(document);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBackupBytes)
        {
            throw new InvalidOperationException("用户标签备份超过 64 MB，无法导出为单个文件。");
        }

        return json;
    }

    public static bool TryValidate(string? json, [NotNullWhen(false)] out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            errorMessage = "备份内容为空。";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumBackupBytes)
        {
            errorMessage = "备份文件过大。";
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize<BackupDocument>(json, JsonOptions);
            if (document is null)
            {
                errorMessage = "备份内容为空。";
                return false;
            }

            _ = Validate(document);
            errorMessage = null;
            return true;
        }
        catch (JsonException exception)
        {
            errorMessage = $"备份 JSON 无效：{exception.Message}";
            return false;
        }
        catch (InvalidDataException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            errorMessage = $"备份校验失败：{exception.Message}";
            return false;
        }
    }

    public TagImportResult Import(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBackupBytes)
        {
            throw new InvalidDataException("备份文件过大。");
        }

        BackupDocument document;
        try
        {
            document = JsonSerializer.Deserialize<BackupDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("备份内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("备份 JSON 无效。", exception);
        }

        var validated = Validate(document);
        using var connection = SqliteDatabase.Open(DatabasePath);
        using var transaction = connection.BeginTransaction();
        var files = ReadIndexedFiles(connection, transaction);
        var missingFiles = new List<MissingBackupFile>();
        var matchedFiles = new List<(long FileId, ValidatedFile Backup)>();
        foreach (var file in validated.Files)
        {
            long? fileId = file.IdentityKind == "stable"
                ? files.Stable.GetValueOrDefault(StableKey(file.VolumeId!, file.FileId!))
                : files.Paths.GetValueOrDefault(NormalizePath(file.Path));
            if (fileId is null or 0)
            {
                missingFiles.Add(new(file.Path, file.IdentityKind == "stable"
                    ? "未找到相同的稳定文件身份。"
                    : "未找到相同的当前路径。"));
            }
            else
            {
                matchedFiles.Add((fileId.Value, file));
            }
        }

        var existingTags = ReadExistingTags(connection, transaction);
        var tagIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var conflicts = new List<TagImportConflict>();
        var createdTags = 0;
        var reusedTags = 0;
        foreach (var tag in validated.Tags)
        {
            if (existingTags.TryGetValue(tag.NormalizedName, out var existing))
            {
                tagIds.Add(tag.NormalizedName, existing.Id);
                reusedTags++;
                if (!string.Equals(existing.Name, tag.DisplayName, StringComparison.Ordinal))
                {
                    conflicts.Add(new(tag.DisplayName, existing.Name));
                }

                continue;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO tags (name, normalized_name, source) VALUES ($name, $normalizedName, 'user') RETURNING id;";
            insert.Parameters.AddWithValue("$name", tag.DisplayName);
            insert.Parameters.AddWithValue("$normalizedName", tag.NormalizedName);
            var tagId = (long)insert.ExecuteScalar()!;
            tagIds.Add(tag.NormalizedName, tagId);
            createdTags++;
        }

        var restoredRelations = 0;
        foreach (var (fileId, backup) in matchedFiles)
        {
            foreach (var normalizedTag in backup.NormalizedTags)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO file_tags (file_id, tag_id, source) VALUES ($fileId, $tagId, 'user') ON CONFLICT DO NOTHING;";
                insert.Parameters.AddWithValue("$fileId", fileId);
                insert.Parameters.AddWithValue("$tagId", tagIds[normalizedTag]);
                restoredRelations += insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return new(createdTags, reusedTags, restoredRelations, conflicts, missingFiles);
    }

    private static List<BackupTag> ReadTags(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM tags WHERE source = 'user' ORDER BY normalized_name;";
        using var reader = command.ExecuteReader();
        var tags = new List<BackupTag>();
        while (reader.Read())
        {
            tags.Add(new() { Name = reader.GetString(0) });
        }

        return tags;
    }

    private static List<BackupFile> ReadFiles(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT f.id, f.volume_id, f.file_id, f.identity_kind, f.path, t.name
            FROM file_tags ft
            JOIN files f ON f.id = ft.file_id
            JOIN tags t ON t.id = ft.tag_id AND t.source = 'user'
            WHERE ft.source = 'user'
            ORDER BY f.id, t.normalized_name;
            """;
        using var reader = command.ExecuteReader();
        var files = new List<BackupFile>();
        BackupFile? current = null;
        long currentId = -1;
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (id != currentId)
            {
                var identityKind = reader.GetString(3);
                current = new()
                {
                    IdentityKind = identityKind,
                    VolumeId = identityKind == "stable" ? reader.GetString(1) : null,
                    FileId = identityKind == "stable" ? reader.GetString(2) : null,
                    Path = reader.GetString(4),
                    Tags = []
                };
                files.Add(current);
                currentId = id;
            }

            current!.Tags!.Add(reader.GetString(5));
        }

        return files;
    }

    private static ValidatedDocument Validate(BackupDocument document)
    {
        if (document.Format != FormatName || document.Version != FormatVersion)
        {
            throw new InvalidDataException("备份格式或版本不受支持。");
        }

        if (document.Tags is null || document.Files is null
            || document.Tags.Count > MaximumTags || document.Files.Count > MaximumFiles)
        {
            throw new InvalidDataException("备份缺少必需集合或项目过多。");
        }

        var tags = new List<ValidatedTag>(document.Tags.Count);
        var knownTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in document.Tags)
        {
            if (tag is null)
            {
                throw new InvalidDataException("备份包含空标签记录。");
            }

            var normalized = NormalizeTag(tag.Name);
            if (!knownTags.Add(normalized.NormalizedName))
            {
                throw new InvalidDataException($"备份包含重复标签“{normalized.DisplayName}”。");
            }

            tags.Add(normalized);
        }

        var files = new List<ValidatedFile>(document.Files.Count);
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in document.Files)
        {
            if (file is null || file.Path is null || file.Path.Length > 32_767 || !Path.IsPathFullyQualified(file.Path)
                || file.Tags is null || file.Tags.Count > MaximumTagsPerFile
                || file.IdentityKind is not ("stable" or "path"))
            {
                throw new InvalidDataException("备份包含无效的文件记录。");
            }

            string key;
            if (file.IdentityKind == "stable")
            {
                if (string.IsNullOrWhiteSpace(file.VolumeId) || file.VolumeId.Length > 256
                    || string.IsNullOrWhiteSpace(file.FileId) || file.FileId.Length > 256)
                {
                    throw new InvalidDataException("稳定文件记录缺少有效身份。");
                }

                key = StableKey(file.VolumeId, file.FileId);
            }
            else
            {
                key = NormalizePath(file.Path);
            }

            if (!knownFiles.Add($"{file.IdentityKind}:{key}"))
            {
                throw new InvalidDataException("备份包含重复文件记录。");
            }

            var normalizedTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tagName in file.Tags)
            {
                var normalizedTag = NormalizeTag(tagName).NormalizedName;
                if (!knownTags.Contains(normalizedTag))
                {
                    throw new InvalidDataException("文件关系引用了未声明的标签。");
                }

                normalizedTags.Add(normalizedTag);
            }

            files.Add(new(file.IdentityKind, file.VolumeId, file.FileId, file.Path, normalizedTags.ToArray()));
        }

        return new(tags, files);
    }

    private static ValidatedTag NormalizeTag(string? name)
    {
        try
        {
            var (displayName, normalizedName) = TagService.NormalizeName(name!);
            return new(displayName, normalizedName);
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("备份包含无效标签名称。", exception);
        }
    }

    private static ExistingFiles ReadIndexedFiles(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, volume_id, file_id, normalized_path, identity_kind, is_online FROM files;";
        using var reader = command.ExecuteReader();
        var stable = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (reader.GetString(4) == "stable")
            {
                stable[StableKey(reader.GetString(1), reader.GetString(2))] = id;
            }

            if (reader.GetString(4) == "path" && reader.GetInt64(5) != 0)
            {
                paths[NormalizePath(reader.GetString(3))] = id;
            }
        }

        return new(stable, paths);
    }

    private static Dictionary<string, ExistingTag> ReadExistingTags(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, normalized_name FROM tags WHERE source = 'user';";
        using var reader = command.ExecuteReader();
        var tags = new Dictionary<string, ExistingTag>(StringComparer.Ordinal);
        while (reader.Read())
        {
            tags.Add(reader.GetString(2), new(reader.GetInt64(0), reader.GetString(1)));
        }

        return tags;
    }

    private static string StableKey(string volumeId, string fileId) => $"{volumeId}\0{fileId}";

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("备份包含无效路径。", exception);
        }
    }

    private sealed class BackupDocument
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public List<BackupTag>? Tags { get; set; }
        public List<BackupFile>? Files { get; set; }
    }

    private sealed class BackupTag
    {
        public string? Name { get; set; }
    }

    private sealed class BackupFile
    {
        public string? IdentityKind { get; set; }
        public string? VolumeId { get; set; }
        public string? FileId { get; set; }
        public string? Path { get; set; }
        public List<string?>? Tags { get; set; }
    }

    private sealed record ValidatedTag(string DisplayName, string NormalizedName);
    private sealed record ValidatedFile(
        string IdentityKind,
        string? VolumeId,
        string? FileId,
        string Path,
        IReadOnlyList<string> NormalizedTags);
    private sealed record ValidatedDocument(
        IReadOnlyList<ValidatedTag> Tags,
        IReadOnlyList<ValidatedFile> Files);
    private sealed record ExistingTag(long Id, string Name);
    private sealed record ExistingFiles(
        IReadOnlyDictionary<string, long> Stable,
        IReadOnlyDictionary<string, long> Paths);
}
