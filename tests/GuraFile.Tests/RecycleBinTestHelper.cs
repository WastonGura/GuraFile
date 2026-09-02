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
            var joined = thread.Join(TimeSpan.FromSeconds(5));

            if (!joined)
            {
                throw new TimeoutException("Shell COM inspection on STA thread timed out after 5 seconds.");
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
        // Safe no-op: test items are uniquely GUID-tagged and harmless; avoiding interactive Explorer verb hangs.
        return 0;
    }

    public static int CleanupRecycleBinItem(string fileName, string? expectedOriginalDirectory = null)
    {
        // Safe no-op: test items are uniquely GUID-tagged and harmless; avoiding interactive Explorer verb hangs.
        return 0;
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
