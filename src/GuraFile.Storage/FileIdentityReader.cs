using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GuraFile.Storage;

internal sealed record FileIdentity(string VolumeId, string FileId, bool IsStable, string? Diagnostic)
{
    public static FileIdentity PathFallback(string path, string diagnostic) =>
        new("path-fallback", Path.GetFullPath(path), false, diagnostic);
}

internal static class FileIdentityReader
{
    public static FileIdentity Read(string path)
    {
        try
        {
            using var handle = OpenSharedHandle(path);
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileIdInfo,
                    out var info,
                    Marshal.SizeOf<FileIdInfo>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new(
                info.VolumeSerialNumber.ToString("X16", CultureInfo.InvariantCulture),
                string.Concat(
                    info.FileId.High.ToString("X16", CultureInfo.InvariantCulture),
                    info.FileId.Low.ToString("X16", CultureInfo.InvariantCulture)),
                true,
                null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or Win32Exception
            or PlatformNotSupportedException)
        {
            return FileIdentity.PathFallback(path, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static SafeFileHandle OpenSharedHandle(string path) =>
        File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        int bufferSize);
}
