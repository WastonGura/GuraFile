using System.IO;

namespace GuraFile.Storage;

public enum StorageMediumKind
{
    Fixed,
    Network,
    Removable,
    Unknown
}

public sealed record StorageDriveSnapshot(
    string Name,
    DriveType DriveType,
    string? DriveFormat,
    bool IsReady = true);

public sealed record StorageCapability(
    StorageMediumKind MediumKind,
    string FileSystemName,
    bool SupportsStableFileId,
    bool IsReparsePoint,
    string UserSummary);

public class StorageCapabilityService
{
    private static StorageCapabilityService? s_default;
    private static readonly object s_defaultLock = new();

    private readonly Func<string, StorageDriveSnapshot?> _getDriveSnapshot;
    private readonly Func<string, FileAttributes> _getAttributes;

    public static StorageCapabilityService Default
    {
        get
        {
            if (s_default is null)
            {
                lock (s_defaultLock)
                {
                    s_default ??= new StorageCapabilityService();
                }
            }
            return s_default;
        }
        set
        {
            lock (s_defaultLock)
            {
                s_default = value;
            }
        }
    }

    public StorageCapabilityService(
        Func<string, StorageDriveSnapshot?>? getDriveSnapshot = null,
        Func<string, FileAttributes>? getAttributes = null)
    {
        _getDriveSnapshot = getDriveSnapshot ?? DefaultGetDriveSnapshot;
        _getAttributes = getAttributes ?? File.GetAttributes;
    }

    public StorageCapabilityService(
        Func<string, DriveInfo?> getDriveInfo,
        Func<string, FileAttributes>? getAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(getDriveInfo);
        _getDriveSnapshot = path =>
        {
            try
            {
                var drive = getDriveInfo(path);
                if (drive is null)
                {
                    return null;
                }

                return new StorageDriveSnapshot(
                    drive.Name,
                    drive.DriveType,
                    drive.IsReady ? drive.DriveFormat : null,
                    drive.IsReady);
            }
            catch
            {
                return null;
            }
        };
        _getAttributes = getAttributes ?? File.GetAttributes;
    }

    public StorageCapability Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new StorageCapability(
                StorageMediumKind.Unknown,
                "Unknown",
                SupportsStableFileId: false,
                IsReparsePoint: false,
                UserSummary: "未知介质 - 身份跟踪受限");
        }

        var isReparsePoint = false;
        try
        {
            var attributes = _getAttributes(path);
            isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // Path might not exist yet or be inaccessible; default to not reparse point
        }

        if (IsUncPath(path))
        {
            var summary = isReparsePoint
                ? "网络共享 (SMB) [重解析点] - 身份跟踪受限（路径降级）"
                : "网络共享 (SMB) - 身份跟踪受限（路径降级）";

            return new StorageCapability(
                StorageMediumKind.Network,
                "SMB",
                SupportsStableFileId: false,
                isReparsePoint,
                summary);
        }

        StorageDriveSnapshot? snapshot = null;
        try
        {
            snapshot = _getDriveSnapshot(path);
        }
        catch
        {
            // Failed to query drive info
        }

        if (snapshot is null)
        {
            var summary = isReparsePoint
                ? "未知介质 [重解析点] - 身份跟踪受限"
                : "未知介质 - 身份跟踪受限";

            return new StorageCapability(
                StorageMediumKind.Unknown,
                "Unknown",
                SupportsStableFileId: false,
                isReparsePoint,
                summary);
        }

        if (!snapshot.IsReady)
        {
            var medium = ToMediumKind(snapshot.DriveType);
            var fs = snapshot.DriveFormat ?? "Unknown";
            var summary = $"介质未就绪或已断开 ({fs}) - 身份跟踪受限";

            return new StorageCapability(
                medium,
                fs,
                SupportsStableFileId: false,
                isReparsePoint,
                summary);
        }

        switch (snapshot.DriveType)
        {
            case DriveType.Network:
            {
                var fs = string.IsNullOrWhiteSpace(snapshot.DriveFormat) ? "SMB" : snapshot.DriveFormat;
                var summary = isReparsePoint
                    ? $"网络共享 ({fs}) [重解析点] - 身份跟踪受限（路径降级）"
                    : $"网络共享 ({fs}) - 身份跟踪受限（路径降级）";

                return new StorageCapability(
                    StorageMediumKind.Network,
                    fs,
                    SupportsStableFileId: false,
                    isReparsePoint,
                    summary);
            }

            case DriveType.Removable:
            {
                var fs = string.IsNullOrWhiteSpace(snapshot.DriveFormat) ? "Unknown" : snapshot.DriveFormat;
                var summary = isReparsePoint
                    ? $"可移动介质 ({fs}) [重解析点] - 身份跟踪受限"
                    : $"可移动介质 ({fs}) - 身份跟踪受限";

                return new StorageCapability(
                    StorageMediumKind.Removable,
                    fs,
                    SupportsStableFileId: false,
                    isReparsePoint,
                    summary);
            }

            case DriveType.Fixed:
            {
                var fs = string.IsNullOrWhiteSpace(snapshot.DriveFormat) ? "Unknown" : snapshot.DriveFormat;
                var isStableFs = string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fs, "ReFS", StringComparison.OrdinalIgnoreCase);

                var summary = isStableFs
                    ? (isReparsePoint ? $"本地固定盘 ({fs}) [重解析点] - 支持稳定身份跟踪" : $"本地固定盘 ({fs}) - 支持稳定身份跟踪")
                    : (isReparsePoint ? $"本地固定盘 ({fs}) [重解析点] - 身份跟踪受限" : $"本地固定盘 ({fs}) - 身份跟踪受限");

                return new StorageCapability(
                    StorageMediumKind.Fixed,
                    fs,
                    SupportsStableFileId: isStableFs,
                    isReparsePoint,
                    summary);
            }

            default:
            {
                var fs = string.IsNullOrWhiteSpace(snapshot.DriveFormat) ? "Unknown" : snapshot.DriveFormat;
                var summary = isReparsePoint
                    ? $"未知介质 ({fs}) [重解析点] - 身份跟踪受限"
                    : $"未知介质 ({fs}) - 身份跟踪受限";

                return new StorageCapability(
                    StorageMediumKind.Unknown,
                    fs,
                    SupportsStableFileId: false,
                    isReparsePoint,
                    summary);
            }
        }
    }

    private static StorageMediumKind ToMediumKind(DriveType driveType) => driveType switch
    {
        DriveType.Fixed => StorageMediumKind.Fixed,
        DriveType.Network => StorageMediumKind.Network,
        DriveType.Removable => StorageMediumKind.Removable,
        _ => StorageMediumKind.Unknown
    };

    private static bool IsUncPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc;
    }

    private static StorageDriveSnapshot? DefaultGetDriveSnapshot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root) || IsUncPath(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return new StorageDriveSnapshot(
                drive.Name,
                drive.DriveType,
                drive.IsReady ? drive.DriveFormat : null,
                drive.IsReady);
        }
        catch
        {
            return null;
        }
    }
}
