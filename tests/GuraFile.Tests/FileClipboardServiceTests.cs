using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
[DoNotParallelize]
[SupportedOSPlatform("windows")]
public sealed class FileClipboardServiceTests
{
    private static readonly object s_lock = new();

    [TestMethod]
    public void SetAndGetContent_WithCopyEffect_ReturnsFilesAndCopyEffect()
    {
        lock (s_lock)
        {
            using var env = TestEnvironment.Create();
            var file1 = env.CreateFile("file1.txt", "content 1");
            var file2 = env.CreateFile("file2.txt", "content 2");

            var clipboard = new FileClipboardService();
            clipboard.SetContent([file1, file2], FileClipboardEffect.Copy);

            var content = AssertClipboardHasFiles(clipboard);
            Assert.AreEqual(FileClipboardEffect.Copy, content.Effect);
            Assert.HasCount(2, content.Files);
            CollectionAssert.AreEquivalent(new[] { Path.GetFullPath(file1), Path.GetFullPath(file2) }, content.Files.ToArray());
        }
    }

    [TestMethod]
    public void SetAndGetContent_WithMoveEffect_ReturnsFilesAndMoveEffect()
    {
        lock (s_lock)
        {
            using var env = TestEnvironment.Create();
            var file1 = env.CreateFile("cut_file.txt", "cut content");

            var clipboard = new FileClipboardService();
            clipboard.SetContent([file1], FileClipboardEffect.Move);

            var content = AssertClipboardHasFiles(clipboard);
            Assert.AreEqual(FileClipboardEffect.Move, content.Effect);
            Assert.HasCount(1, content.Files);
            Assert.AreEqual(Path.GetFullPath(file1), content.Files[0]);
        }
    }

    [TestMethod]
    public void Clear_RemovesClipboardFiles()
    {
        lock (s_lock)
        {
            using var env = TestEnvironment.Create();
            var file1 = env.CreateFile("temp.txt", "temp content");

            var clipboard = new FileClipboardService();
            clipboard.SetContent([file1], FileClipboardEffect.Copy);
            AssertClipboardHasFiles(clipboard);

            clipboard.Clear();
            AssertClipboardEmpty(clipboard);
        }
    }

    [TestMethod]
    public void SetContent_WithEmptyList_ClearsClipboard()
    {
        lock (s_lock)
        {
            using var env = TestEnvironment.Create();
            var file1 = env.CreateFile("temp.txt", "temp content");

            var clipboard = new FileClipboardService();
            clipboard.SetContent([file1], FileClipboardEffect.Copy);
            AssertClipboardHasFiles(clipboard);

            clipboard.SetContent([], FileClipboardEffect.Copy);
            AssertClipboardEmpty(clipboard);
        }
    }

    [TestMethod]
    public void FileClipboardService_RetryParametersAreBounded()
    {
        Assert.IsLessThanOrEqualTo(5, FileClipboardService.DefaultRetries);
        Assert.IsLessThanOrEqualTo(20, FileClipboardService.DefaultDelayMs);
        Assert.IsLessThanOrEqualTo(150, FileClipboardService.DefaultRetries * FileClipboardService.DefaultDelayMs);
        Assert.IsLessThanOrEqualTo(3, FileClipboardService.MaxClearAttempts);
    }

    [TestCleanup]
    public void Cleanup()
    {
        lock (s_lock)
        {
            new FileClipboardService().Clear();
        }
    }

    private static FileClipboardContent AssertClipboardHasFiles(IFileClipboardService clipboard, int timeoutMs = 2000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (clipboard.HasFiles())
            {
                var content = clipboard.GetContent();
                if (content is not null)
                {
                    return content;
                }
            }

            Thread.Sleep(30);
        }

        Assert.IsTrue(clipboard.HasFiles());
        var finalContent = clipboard.GetContent();
        Assert.IsNotNull(finalContent);
        return finalContent;
    }

    private static void AssertClipboardEmpty(IFileClipboardService clipboard, int timeoutMs = 2000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (!clipboard.HasFiles() && clipboard.GetContent() == null)
            {
                return;
            }

            clipboard.Clear();
            Thread.Sleep(50);
        }

        Assert.IsFalse(clipboard.HasFiles());
        Assert.IsNull(clipboard.GetContent());
    }

    private sealed class TestEnvironment : IDisposable
    {
        public string RootPath { get; }

        private TestEnvironment(string rootPath)
        {
            RootPath = rootPath;
        }

        public static TestEnvironment Create()
        {
            var path = Path.Combine(Path.GetTempPath(), $"GuraFile_ClipTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
        }

        public string CreateFile(string relativeName, string content)
        {
            var filePath = Path.Combine(RootPath, relativeName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
            return filePath;
        }

        public void Dispose()
        {
            try
            {
                RecycleBinTestHelper.CleanupRecycleBinItemsForDirectory(RootPath);
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
