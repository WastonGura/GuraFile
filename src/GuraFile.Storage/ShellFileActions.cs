using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GuraFile.Storage;

public sealed class ShellFileActions
{
    private readonly Action<string> _open;
    private readonly Action<string> _reveal;

    public ShellFileActions()
        : this(OpenWithDefaultApplication, RevealWithExplorer)
    {
    }

    public ShellFileActions(Action<string> open, Action<string> reveal)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _reveal = reveal ?? throw new ArgumentNullException(nameof(reveal));
    }

    public void Open(string path) => Execute(path, _open, "打开");

    public void RevealInExplorer(string path) => Execute(path, _reveal, "定位");

    private static void Execute(string path, Action<string> action, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"文件不存在或无法访问：{fullPath}", fullPath);
        }

        try
        {
            action(fullPath);
        }
        catch (Exception exception) when (
            exception is Win32Exception or COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException($"无法{operation}文件“{Path.GetFileName(fullPath)}”：{exception.Message}", exception);
        }
    }

    private static void OpenWithDefaultApplication(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        if (process is null)
        {
            throw new InvalidOperationException("Windows Shell 未启动关联应用。");
        }
    }

    private static void RevealWithExplorer(string path)
    {
        var result = SHParseDisplayName(path, IntPtr.Zero, out var itemIdList, 0, out _);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(itemIdList, 0, IntPtr.Zero, 0));
        }
        finally
        {
            Marshal.FreeCoTaskMem(itemIdList);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributes,
        out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr itemIdList,
        uint itemCount,
        IntPtr childItemIdLists,
        uint flags);
}
