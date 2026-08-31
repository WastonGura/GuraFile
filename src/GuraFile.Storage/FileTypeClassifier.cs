using System.Security;
using System.Buffers.Binary;

namespace GuraFile.Storage;

public sealed record FileTypeClassification(
    string TypeTag,
    string FormatTag,
    string? DetectedFormatTag,
    bool HasConflict,
    string? Diagnostic)
{
    public IReadOnlyList<string> AutomaticTags => HasConflict
        ? [TypeTag, FormatTag, "状态/类型冲突"]
        : [TypeTag, FormatTag];
}

public sealed class FileTypeClassifier
{
    internal const int HeaderByteLimit = 32;

    private static readonly IReadOnlyDictionary<string, FileType> ExtensionTypes =
        new Dictionary<string, FileType>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = Type("图片", "PNG"),
            [".jpg"] = Type("图片", "JPEG"),
            [".jpeg"] = Type("图片", "JPEG"),
            [".jpe"] = Type("图片", "JPEG"),
            [".gif"] = Type("图片", "GIF"),
            [".bmp"] = Type("图片", "BMP"),
            [".webp"] = Type("图片", "WEBP"),
            [".tif"] = Type("图片", "TIFF"),
            [".tiff"] = Type("图片", "TIFF"),
            [".svg"] = Type("图片", "SVG"),
            [".heic"] = Type("图片", "HEIC"),
            [".avif"] = Type("图片", "AVIF"),
            [".ico"] = Type("图片", "ICO"),
            [".raw"] = Type("图片", "RAW"),

            [".mp4"] = Type("视频", "MP4"),
            [".mkv"] = Type("视频", "MKV"),
            [".avi"] = Type("视频", "AVI"),
            [".mov"] = Type("视频", "MOV"),
            [".wmv"] = Type("视频", "WMV"),
            [".webm"] = Type("视频", "WEBM"),
            [".m4v"] = Type("视频", "M4V"),
            [".flv"] = Type("视频", "FLV"),
            [".3gp"] = Type("视频", "3GP"),
            [".3g2"] = Type("视频", "3G2"),

            [".mp3"] = Type("音频", "MP3"),
            [".wav"] = Type("音频", "WAV"),
            [".flac"] = Type("音频", "FLAC"),
            [".aac"] = Type("音频", "AAC"),
            [".ogg"] = Type("音频", "OGG"),
            [".m4a"] = Type("音频", "M4A"),
            [".wma"] = Type("音频", "WMA"),

            [".txt"] = Type("文档", "TXT"),
            [".md"] = Type("文档", "Markdown"),
            [".pdf"] = Type("文档", "PDF"),
            [".doc"] = Type("文档", "DOC"),
            [".docx"] = Type("文档", "DOCX"),
            [".rtf"] = Type("文档", "RTF"),
            [".odt"] = Type("文档", "ODT"),
            [".epub"] = Type("文档", "EPUB"),

            [".xls"] = Type("表格", "XLS"),
            [".xlsx"] = Type("表格", "XLSX"),
            [".csv"] = Type("表格", "CSV"),
            [".ods"] = Type("表格", "ODS"),

            [".ppt"] = Type("演示文稿", "PPT"),
            [".pptx"] = Type("演示文稿", "PPTX"),
            [".odp"] = Type("演示文稿", "ODP"),

            [".zip"] = Type("压缩包", "ZIP"),
            [".7z"] = Type("压缩包", "7Z"),
            [".rar"] = Type("压缩包", "RAR"),
            [".tar"] = Type("压缩包", "TAR"),
            [".gz"] = Type("压缩包", "GZ"),
            [".bz2"] = Type("压缩包", "BZ2"),
            [".xz"] = Type("压缩包", "XZ"),

            [".cs"] = Type("代码", "CS"),
            [".fs"] = Type("代码", "FS"),
            [".vb"] = Type("代码", "VB"),
            [".c"] = Type("代码", "C"),
            [".cpp"] = Type("代码", "CPP"),
            [".h"] = Type("代码", "H"),
            [".hpp"] = Type("代码", "HPP"),
            [".java"] = Type("代码", "JAVA"),
            [".kt"] = Type("代码", "KOTLIN"),
            [".py"] = Type("代码", "PYTHON"),
            [".js"] = Type("代码", "JAVASCRIPT"),
            [".ts"] = Type("代码", "TYPESCRIPT"),
            [".jsx"] = Type("代码", "JSX"),
            [".tsx"] = Type("代码", "TSX"),
            [".html"] = Type("代码", "HTML"),
            [".css"] = Type("代码", "CSS"),
            [".xml"] = Type("代码", "XML"),
            [".json"] = Type("代码", "JSON"),
            [".yaml"] = Type("代码", "YAML"),
            [".yml"] = Type("代码", "YAML"),
            [".toml"] = Type("代码", "TOML"),
            [".sql"] = Type("代码", "SQL"),
            [".sh"] = Type("代码", "SHELL"),
            [".ps1"] = Type("代码", "POWERSHELL"),
            [".go"] = Type("代码", "GO"),
            [".rs"] = Type("代码", "RUST"),
            [".swift"] = Type("代码", "SWIFT"),

            [".exe"] = Type("可执行文件", "EXE"),
            [".msi"] = Type("可执行文件", "MSI"),
            [".dll"] = Type("可执行文件", "DLL"),
            [".bat"] = Type("可执行文件", "BAT"),
            [".cmd"] = Type("可执行文件", "CMD"),
            [".com"] = Type("可执行文件", "COM"),
            [".appx"] = Type("可执行文件", "APPX"),
            [".msix"] = Type("可执行文件", "MSIX"),

            [".ttf"] = Type("字体", "TTF"),
            [".otf"] = Type("字体", "OTF"),
            [".woff"] = Type("字体", "WOFF"),
            [".woff2"] = Type("字体", "WOFF2")
        };

    private static readonly HashSet<string> ZipContainerFormats =
        ["ZIP", "DOCX", "XLSX", "PPTX", "ODT", "ODS", "ODP", "EPUB", "JAR", "APK"];

    private static readonly HashSet<string> Mp4ContainerFormats =
        ["MP4", "M4V", "M4A", "MOV", "HEIC"];

    private readonly Func<string, int, byte[]> _readHeader;

    public FileTypeClassifier() : this(ReadHeader)
    {
    }

    internal FileTypeClassifier(Func<string, int, byte[]> readHeader) =>
        _readHeader = readHeader ?? throw new ArgumentNullException(nameof(readHeader));

    public FileTypeClassification Classify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);
        var extensionType = FromExtension(extension);
        FileType? detectedType;

        try
        {
            var header = _readHeader(path, HeaderByteLimit);
            detectedType = DetectHeader(header.AsSpan(0, Math.Min(header.Length, HeaderByteLimit)));
        }
        catch (Exception exception) when (IsFileReadFailure(exception))
        {
            return Result(extensionType, null, false, $"无法读取文件头：{exception.Message}");
        }

        if (string.IsNullOrEmpty(extension) && detectedType is not null)
        {
            return Result(detectedType, detectedType, false, null);
        }

        var conflict = detectedType is not null && !IsCompatible(extensionType, detectedType);
        var diagnostic = conflict
            ? $"扩展名格式/{extensionType.Format} 与文件头格式/{detectedType!.Format} 不一致。"
            : null;
        return Result(extensionType, detectedType, conflict, diagnostic);
    }

    private static FileType FromExtension(string extension)
    {
        if (ExtensionTypes.TryGetValue(extension, out var known))
        {
            return known;
        }

        var format = extension.Length > 1 ? extension[1..].ToUpperInvariant() : "未知";
        return Type("其他", format);
    }

    private static FileType? DetectHeader(ReadOnlySpan<byte> header)
    {
        if (StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Type("图片", "PNG");
        }

        if (StartsWith(header, [0xFF, 0xD8, 0xFF]))
        {
            return Type("图片", "JPEG");
        }

        if (StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8))
        {
            return Type("图片", "GIF");
        }

        if (StartsWith(header, "%PDF-"u8))
        {
            return Type("文档", "PDF");
        }

        if (StartsWith(header, [0x50, 0x4B, 0x03, 0x04]) ||
            StartsWith(header, [0x50, 0x4B, 0x05, 0x06]) ||
            StartsWith(header, [0x50, 0x4B, 0x07, 0x08]))
        {
            return Type("压缩包", "ZIP");
        }

        if (StartsWith(header, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
        {
            return Type("压缩包", "7Z");
        }

        if (IsId3Header(header) || IsMpegAudioFrame(header))
        {
            return Type("音频", "MP3");
        }

        return DetectIsoBaseMedia(header);
    }

    private static bool IsId3Header(ReadOnlySpan<byte> header) =>
        header.Length >= 10 &&
        StartsWith(header, "ID3"u8) &&
        header[3] != 0xFF &&
        header[4] != 0xFF &&
        (header[5] & 0x0F) == 0 &&
        (header[6] | header[7] | header[8] | header[9]) < 0x80;

    private static bool IsMpegAudioFrame(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        var version = (header[1] >> 3) & 0x03;
        var layer = (header[1] >> 1) & 0x03;
        var bitrate = header[2] >> 4;
        var sampleRate = (header[2] >> 2) & 0x03;
        return version != 0x01 && layer == 0x01 && bitrate is > 0 and < 0x0F && sampleRate != 0x03;
    }

    private static FileType? DetectIsoBaseMedia(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header[4..8].SequenceEqual("ftyp"u8))
        {
            return null;
        }

        var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (boxSize is 1 or > 1024 * 1024 or > 0 and < 16 ||
            boxSize <= HeaderByteLimit && header.Length < boxSize)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) switch
        {
            0x69736F6D or 0x69736F32 or 0x6D703431 or 0x6D703432 or 0x61766331 or 0x64617368 or 0x4D345620
                => Type("视频", "MP4"),
            0x4D344120 => Type("音频", "M4A"),
            0x71742020 => Type("视频", "MOV"),
            0x68656963 or 0x68656978 or 0x6D696631 or 0x6D736631 => Type("图片", "HEIC"),
            0x61766966 or 0x61766973 => Type("图片", "AVIF"),
            0x33677034 or 0x33677035 or 0x33677036 or 0x33676536 or 0x33676736 => Type("视频", "3GP"),
            0x33673261 or 0x33673262 => Type("视频", "3G2"),
            _ => null
        };
    }

    private static bool IsCompatible(FileType extensionType, FileType detectedType) =>
        extensionType.Format.Equals(detectedType.Format, StringComparison.OrdinalIgnoreCase) ||
        detectedType.Format == "ZIP" && ZipContainerFormats.Contains(extensionType.Format) ||
        detectedType.Format == "MP4" && Mp4ContainerFormats.Contains(extensionType.Format);

    private static FileTypeClassification Result(
        FileType extensionType,
        FileType? detectedType,
        bool conflict,
        string? diagnostic) =>
        new(extensionType.TypeTag, extensionType.FormatTag, detectedType?.FormatTag, conflict, diagnostic);

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static bool IsFileReadFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;

    private static byte[] ReadHeader(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: maxBytes,
            FileOptions.SequentialScan);
        var header = new byte[maxBytes];
        var length = 0;
        while (length < header.Length)
        {
            var read = stream.Read(header, length, header.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return header[..length];
    }

    private static FileType Type(string category, string format) => new(category, format);

    private sealed record FileType(string Category, string Format)
    {
        public string TypeTag => $"类型/{Category}";
        public string FormatTag => $"格式/{Format}";
    }
}
