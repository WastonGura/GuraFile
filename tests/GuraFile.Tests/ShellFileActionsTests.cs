using System.ComponentModel;
using System.Runtime.InteropServices;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class ShellFileActionsTests
{
    [TestMethod]
    public void OpenPassesTheExactFullPathToTheShell()
    {
        using var file = TempFile.Create("空 格 ' file.txt");
        string? opened = null;
        var actions = new ShellFileActions(path => opened = path, _ => { });

        actions.Open(file.Path);

        Assert.AreEqual(Path.GetFullPath(file.Path), opened);
    }

    [TestMethod]
    public void RevealPassesTheExactFullPathToTheShell()
    {
        using var file = TempFile.Create("image 空 格.png");
        string? revealed = null;
        var actions = new ShellFileActions(_ => { }, path => revealed = path);

        actions.RevealInExplorer(file.Path);

        Assert.AreEqual(Path.GetFullPath(file.Path), revealed);
    }

    [TestMethod]
    public void MissingOrInvalidFileNeverReachesTheShell()
    {
        var calls = 0;
        var actions = new ShellFileActions(_ => calls++, _ => calls++);
        var missing = Path.Combine(Path.GetTempPath(), "missing \" & file.txt");

        Assert.ThrowsExactly<FileNotFoundException>(() => actions.Open(missing));
        Assert.ThrowsExactly<FileNotFoundException>(() => actions.RevealInExplorer(missing));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void ShellFailuresAreWrappedWithActionableContext()
    {
        using var file = TempFile.Create("failure.txt");
        Exception[] failures =
        [
            new Win32Exception(1155, "No association"),
            new UnauthorizedAccessException("Access denied"),
            new COMException("Shell failure")
        ];

        foreach (var failure in failures)
        {
            var actions = new ShellFileActions(_ => throw failure, _ => throw failure);

            var open = Assert.ThrowsExactly<InvalidOperationException>(() => actions.Open(file.Path));
            var reveal = Assert.ThrowsExactly<InvalidOperationException>(() => actions.RevealInExplorer(file.Path));

            StringAssert.Contains(open.Message, "无法打开文件");
            StringAssert.Contains(reveal.Message, "无法定位文件");
            Assert.AreSame(failure, open.InnerException);
            Assert.AreSame(failure, reveal.InnerException);
        }
    }

    private sealed class TempFile : IDisposable
    {
        private TempFile(string path) => Path = path;

        public string Path { get; }

        public static TempFile Create(string name)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, name);
            File.WriteAllText(path, "test");
            return new(path);
        }

        public void Dispose() => Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
    }
}
