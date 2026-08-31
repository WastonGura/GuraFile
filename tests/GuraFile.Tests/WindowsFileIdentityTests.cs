using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class WindowsFileIdentityTests
{
    [TestMethod]
    public void RenameKeepsStableIdentity()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "identity.txt");
        File.WriteAllText(filePath, "identity");
        Assert.AreEqual("NTFS", new DriveInfo(Path.GetPathRoot(filePath)!).DriveFormat, ignoreCase: true);

        var renamedPath = Path.Combine(temp.Path, "renamed.txt");
        var first = FileIdentityReader.Read(filePath);
        File.Move(filePath, renamedPath);
        var second = FileIdentityReader.Read(renamedPath);

        Assert.IsTrue(first.IsStable, first.Diagnostic);
        Assert.AreEqual(first, second);
        Assert.AreEqual(16, first.VolumeId.Length);
        Assert.AreEqual(32, first.FileId.Length);
    }

    [TestMethod]
    public void IdentityHandleAllowsRenameAndDelete()
    {
        using var temp = TempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "shared.txt");
        var renamedPath = Path.Combine(temp.Path, "renamed.txt");
        File.WriteAllText(filePath, "shared");

        using var handle = FileIdentityReader.OpenSharedHandle(filePath);
        File.Move(filePath, renamedPath);
        File.Delete(renamedPath);

        Assert.IsFalse(File.Exists(renamedPath));
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.Identity.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
