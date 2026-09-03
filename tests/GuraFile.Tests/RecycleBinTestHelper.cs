using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GuraFile.Tests;

[SupportedOSPlatform("windows")]
internal static class RecycleBinTestHelper
{
    private const int ssfBITBUCKET = 10;
    private static readonly object _syncLock = new();

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern void OleUninitialize();

    private static T RunInSta<T>(Func<T> action)
    {
        lock (_syncLock)
        {
            T result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                var oleInit = OleInitialize(IntPtr.Zero);
                try
                {
                    result = action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    if (oleInit == 0 || oleInit == 1)
                    {
                        OleUninitialize();
                    }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            var joined = thread.Join(TimeSpan.FromSeconds(15));

            if (!joined)
            {
                throw new TimeoutException("Shell COM operation on STA thread timed out after 15 seconds.");
            }

            if (error != null)
            {
                throw new InvalidOperationException($"Error executing Shell COM on STA thread: {error.Message}", error);
            }

            return result;
        }
    }

    public static bool ExistsInRecycleBin(string fileName, string? expectedOriginalDirectory = null, int maxRetries = 10, int delayMs = 100)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (RunInSta(() => FindRecycleBinItem(fileName, expectedOriginalDirectory) != null))
                {
                    return true;
                }
            }
            catch
            {
            }

            if (i < maxRetries - 1)
            {
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }

    public static int CleanupRecycleBinItemsForDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return 0;
        }

        var normalizedExpectedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));

        return RunInSta(() =>
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null)
            {
                return 0;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic recycleBin = shell.NameSpace(ssfBITBUCKET);
            if (recycleBin == null)
            {
                return 0;
            }

            var matchingItems = new List<dynamic>();

            foreach (dynamic item in recycleBin.Items())
            {
                try
                {
                    string origDir = recycleBin.GetDetailsOf(item, 1);
                    if (string.IsNullOrWhiteSpace(origDir))
                    {
                        continue;
                    }

                    var normalizedOrig = Path.TrimEndingDirectorySeparator(Path.GetFullPath(origDir));
                    if (string.Equals(normalizedOrig, normalizedExpectedDir, StringComparison.OrdinalIgnoreCase) ||
                        normalizedOrig.StartsWith(normalizedExpectedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingItems.Add(item);
                    }
                }
                catch
                {
                }
            }

            int count = 0;
            foreach (var item in matchingItems)
            {
                if (RestoreAndPurge(item, recycleBin, normalizedExpectedDir))
                {
                    count++;
                }
            }

            return count;
        });
    }

    public static int CleanupRecycleBinItem(string fileName, string? expectedOriginalDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return 0;
        }

        string? normalizedExpectedDir = expectedOriginalDirectory != null
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedOriginalDirectory))
            : null;

        return RunInSta(() =>
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null)
            {
                return 0;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic recycleBin = shell.NameSpace(ssfBITBUCKET);
            if (recycleBin == null)
            {
                return 0;
            }

            var matchingItems = new List<dynamic>();
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            foreach (dynamic item in recycleBin.Items())
            {
                try
                {
                    string name = item.Name;
                    bool nameMatches = string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(name, nameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                                       (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) && name.Length >= (nameWithoutExt?.Length ?? 0));
                    if (!nameMatches)
                    {
                        continue;
                    }

                    if (normalizedExpectedDir != null)
                    {
                        string origDir = recycleBin.GetDetailsOf(item, 1);
                        if (!string.IsNullOrWhiteSpace(origDir))
                        {
                            var normalizedOrig = Path.TrimEndingDirectorySeparator(Path.GetFullPath(origDir));
                            if (string.Equals(normalizedOrig, normalizedExpectedDir, StringComparison.OrdinalIgnoreCase) ||
                                normalizedOrig.StartsWith(normalizedExpectedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                normalizedExpectedDir.StartsWith(normalizedOrig + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            {
                                matchingItems.Add(item);
                            }
                        }
                        else
                        {
                            matchingItems.Add(item);
                        }
                    }
                    else
                    {
                        matchingItems.Add(item);
                    }
                }
                catch
                {
                }
            }

            int count = 0;
            foreach (var item in matchingItems)
            {
                if (RestoreAndPurge(item, recycleBin, normalizedExpectedDir))
                {
                    count++;
                }
            }

            return count;
        });
    }

    private static bool RestoreAndPurge(dynamic item, dynamic recycleBin, string? fallbackDir)
    {
        try
        {
            // 1. Direct Recycle Bin payload purge: delete the underlying $R<id> file/folder and $I<id> metadata entry.
            // This is instantaneous, thread-safe, and avoids any Explorer verb / DDE hangs on headless Windows Server runners.
            try
            {
                string itemPath = (string)item.Path;
                if (!string.IsNullOrWhiteSpace(itemPath))
                {
                    var dir = Path.GetDirectoryName(itemPath);
                    var leaf = Path.GetFileName(itemPath);
                    if (leaf.StartsWith("$R", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dir))
                    {
                        var iLeaf = "$I" + leaf.Substring(2);
                        var iPath = Path.Combine(dir, iLeaf);

                        if (File.Exists(itemPath))
                        {
                            try { File.Delete(itemPath); } catch { }
                        }
                        else if (Directory.Exists(itemPath))
                        {
                            try { Directory.Delete(itemPath, recursive: true); } catch { }
                        }

                        if (File.Exists(iPath))
                        {
                            try { File.Delete(iPath); } catch { }
                        }

                        if (!File.Exists(itemPath) && !Directory.Exists(itemPath) && !File.Exists(iPath))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            // 2. Fallback: Shell verb undelete followed by file removal
            string name = (string)item.Name;
            string origDir = (string)recycleBin.GetDetailsOf(item, 1);
            if (string.IsNullOrWhiteSpace(origDir))
            {
                origDir = fallbackDir ?? "";
            }

            string? targetPath = !string.IsNullOrWhiteSpace(origDir) ? Path.Combine(origDir, name) : null;
            if (targetPath != null)
            {
                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { }
                }
                else if (Directory.Exists(targetPath))
                {
                    try { Directory.Delete(targetPath, recursive: true); } catch { }
                }
            }

            bool invoked = false;
            try
            {
                item.InvokeVerb("undelete");
                invoked = true;
            }
            catch
            {
            }

            if (targetPath != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (File.Exists(targetPath) || Directory.Exists(targetPath))
                    {
                        break;
                    }
                    Thread.Sleep(50);
                }
            }

            if (targetPath != null && !File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                try
                {
                    dynamic verbs = item.Verbs();
                    if (verbs != null)
                    {
                        for (int v = 0; v < verbs.Count; v++)
                        {
                            dynamic verb = verbs.Item(v);
                            string vName = verb.Name?.ToString() ?? "";
                            var clean = vName.Replace("&", "").Trim();
                            if (string.Equals(clean, "Restore", StringComparison.OrdinalIgnoreCase) ||
                                clean.Contains("还原", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(vName, "&E", StringComparison.OrdinalIgnoreCase))
                            {
                                verb.DoIt();
                                invoked = true;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            if (!invoked)
            {
                return false;
            }

            if (targetPath != null)
            {
                for (int i = 0; i < 20; i++)
                {
                    if (File.Exists(targetPath))
                    {
                        try { File.Delete(targetPath); } catch { }
                        if (!File.Exists(targetPath))
                        {
                            return true;
                        }
                    }
                    else if (Directory.Exists(targetPath))
                    {
                        try { Directory.Delete(targetPath, recursive: true); } catch { }
                        if (!Directory.Exists(targetPath))
                        {
                            return true;
                        }
                    }
                    Thread.Sleep(50);
                }

            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static dynamic? FindRecycleBinItem(string fileName, string? expectedOriginalDirectory = null)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic recycleBin = shell.NameSpace(ssfBITBUCKET);
            if (recycleBin == null)
            {
                return null;
            }

            string? normalizedExpectedDir = expectedOriginalDirectory != null
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedOriginalDirectory))
                : null;

            foreach (dynamic item in recycleBin.Items())
            {
                try
                {
                    string name = item.Name;
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    bool nameMatches = string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(name, nameWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                                       (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) && name.Length >= (nameWithoutExt?.Length ?? 0));
                    if (nameMatches)
                    {
                        if (normalizedExpectedDir == null)
                        {
                            return item;
                        }

                        string origDir = recycleBin.GetDetailsOf(item, 1);
                        if (!string.IsNullOrWhiteSpace(origDir))
                        {
                            var normalizedOrig = Path.TrimEndingDirectorySeparator(Path.GetFullPath(origDir));
                            if (string.Equals(normalizedOrig, normalizedExpectedDir, StringComparison.OrdinalIgnoreCase) ||
                                normalizedOrig.StartsWith(normalizedExpectedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                normalizedExpectedDir.StartsWith(normalizedOrig + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            {
                                return item;
                            }
                        }
                        else
                        {
                            // In environments where Original Location column is empty, GUID-based name match is conclusive.
                            return item;
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return null;
    }
}
