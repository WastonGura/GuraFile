namespace GuraFile.Storage;

public static class GraphCategoryResolver
{
    public const string Image = "图片";
    public const string Audio = "音频";
    public const string Video = "视频";
    public const string Document = "文档";
    public const string Archive = "压缩包";
    public const string Code = "代码";
    public const string Other = "其他";

    public static string Resolve(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return Other;
        }

        var ext = extension.StartsWith('.') ? extension : $".{extension}";
        return ext.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".jpe" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff"
                or ".svg" or ".heic" or ".avif" or ".ico" or ".raw" => Image,

            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" or ".wma" => Audio,

            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v" or ".flv"
                or ".3gp" or ".3g2" => Video,

            ".txt" or ".md" or ".pdf" or ".doc" or ".docx" or ".rtf" or ".odt" or ".epub"
                or ".xls" or ".xlsx" or ".csv" or ".ods" or ".ppt" or ".pptx" or ".odp" => Document,

            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" => Archive,

            ".cs" or ".fs" or ".vb" or ".c" or ".cpp" or ".h" or ".hpp" or ".java" or ".kt"
                or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".html" or ".css" or ".xml"
                or ".json" or ".yaml" or ".yml" or ".toml" or ".sql" or ".sh" or ".ps1"
                or ".go" or ".rs" or ".swift" => Code,

            _ => Other
        };
    }
}
