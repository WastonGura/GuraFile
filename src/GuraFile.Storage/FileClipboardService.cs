using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GuraFile.Storage;

public enum FileClipboardEffect
{
    Copy = 1,
    Move = 2
}

public sealed record FileClipboardContent(
    IReadOnlyList<string> Files,
    FileClipboardEffect Effect);

public interface IFileClipboardService
{
    bool HasFiles();
    FileClipboardContent? GetContent();
    void SetContent(IReadOnlyList<string> filePaths, FileClipboardEffect effect);
    void Clear();
}

[SupportedOSPlatform("windows")]
public sealed class FileClipboardService : IFileClipboardService
{
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const string PreferredDropEffectFormatName = "Preferred DropEffect";

    private static readonly Lazy<uint> PreferredDropEffectFormat = new(
        () => RegisterClipboardFormatW(PreferredDropEffectFormatName));

    [StructLayout(LayoutKind.Sequential)]
    private struct DROPFILES
    {
        public uint pFiles;
        public int ptX;
        public int ptY;
        public int fNC;
        public int fWide;
    }

    public bool HasFiles()
    {
        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            return IsClipboardFormatAvailable(CF_HDROP);
        }
        finally
        {
            CloseClipboard();
        }
    }

    public FileClipboardContent? GetContent()
    {
        if (!TryOpenClipboard())
        {
            return null;
        }

        try
        {
            if (!IsClipboardFormatAvailable(CF_HDROP))
            {
                return null;
            }

            var hDrop = GetClipboardData(CF_HDROP);
            if (hDrop == IntPtr.Zero)
            {
                return null;
            }

            var count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0)
            {
                return null;
            }

            var files = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFileW(hDrop, i, null, 0);
                if (length == 0)
                {
                    continue;
                }

                var buffer = new char[length + 1];
                DragQueryFileW(hDrop, i, buffer, (uint)buffer.Length);
                var path = new string(buffer, 0, (int)length);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    files.Add(Path.GetFullPath(path));
                }
            }

            if (files.Count == 0)
            {
                return null;
            }

            var effect = FileClipboardEffect.Copy;
            var formatId = PreferredDropEffectFormat.Value;
            if (formatId != 0 && IsClipboardFormatAvailable(formatId))
            {
                var hEffect = GetClipboardData(formatId);
                if (hEffect != IntPtr.Zero)
                {
                    var ptr = GlobalLock(hEffect);
                    if (ptr != IntPtr.Zero)
                    {
                        try
                        {
                            var effectValue = Marshal.ReadInt32(ptr);
                            if ((effectValue & (int)FileClipboardEffect.Move) != 0)
                            {
                                effect = FileClipboardEffect.Move;
                            }
                        }
                        finally
                        {
                            GlobalUnlock(hEffect);
                        }
                    }
                }
            }

            return new FileClipboardContent(files, effect);
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void SetContent(IReadOnlyList<string> filePaths, FileClipboardEffect effect)
    {
        if (filePaths == null || filePaths.Count == 0)
        {
            Clear();
            return;
        }

        var normalizedPaths = filePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            Clear();
            return;
        }

        var dropFilesHandle = CreateDropFilesHandle(normalizedPaths);
        var dropEffectHandle = CreateDropEffectHandle(effect);

        OleSetClipboard(IntPtr.Zero);

        if (!TryOpenClipboard())
        {
            GlobalFree(dropFilesHandle);
            GlobalFree(dropEffectHandle);
            throw new InvalidOperationException("无法打开 Windows 剪贴板。");
        }

        try
        {
            EmptyClipboard();

            var resDrop = SetClipboardData(CF_HDROP, dropFilesHandle);
            if (resDrop == IntPtr.Zero)
            {
                GlobalFree(dropFilesHandle);
            }

            var formatId = PreferredDropEffectFormat.Value;
            if (formatId != 0)
            {
                var resEffect = SetClipboardData(formatId, dropEffectHandle);
                if (resEffect == IntPtr.Zero)
                {
                    GlobalFree(dropEffectHandle);
                }
            }
            else
            {
                GlobalFree(dropEffectHandle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Clear()
    {
        OleSetClipboard(IntPtr.Zero);

        if (!TryOpenClipboard())
        {
            return;
        }

        try
        {
            EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static IntPtr CreateDropFilesHandle(IReadOnlyList<string> paths)
    {
        var dropFilesHeaderSize = Marshal.SizeOf<DROPFILES>();
        var totalChars = 0;
        foreach (var path in paths)
        {
            totalChars += path.Length + 1; // null terminator for each string
        }
        totalChars += 1; // final double null terminator

        var totalBytes = dropFilesHeaderSize + (totalChars * sizeof(char));
        var hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)totalBytes);
        if (hGlobal == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法分配全局剪贴板内存。");
        }

        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法锁定全局剪贴板内存。");
        }

        try
        {
            var dropFiles = new DROPFILES
            {
                pFiles = (uint)dropFilesHeaderSize,
                ptX = 0,
                ptY = 0,
                fNC = 0,
                fWide = 1
            };

            Marshal.StructureToPtr(dropFiles, ptr, false);

            var charPointer = IntPtr.Add(ptr, dropFilesHeaderSize);
            var offset = 0;
            foreach (var path in paths)
            {
                var span = path.AsSpan();
                for (int i = 0; i < span.Length; i++)
                {
                    Marshal.WriteInt16(charPointer, offset * sizeof(char), span[i]);
                    offset++;
                }
                Marshal.WriteInt16(charPointer, offset * sizeof(char), 0);
                offset++;
            }
            Marshal.WriteInt16(charPointer, offset * sizeof(char), 0);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        return hGlobal;
    }

    private static IntPtr CreateDropEffectHandle(FileClipboardEffect effect)
    {
        var hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)sizeof(uint));
        if (hGlobal == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法分配全局剪贴板内存。");
        }

        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法锁定全局剪贴板内存。");
        }

        try
        {
            Marshal.WriteInt32(ptr, (int)effect);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        return hGlobal;
    }

    private static bool TryOpenClipboard(int retries = 5, int delayMs = 20)
    {
        for (int i = 0; i < retries; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            if (i < retries - 1)
            {
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleSetClipboard(IntPtr pDataObj);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint uFormat);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, [Out] char[]? lpszFile, uint cch);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
