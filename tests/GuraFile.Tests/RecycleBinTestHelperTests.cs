using System.Reflection;

namespace GuraFile.Tests;

[TestClass]
public sealed class RecycleBinTestHelperTests
{
    [TestMethod]
    public void RestoreAndPurge_WhenMetadataDeletionFails_DoesNotReportSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"GuraFile-RecycleHelper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var suffix = Guid.NewGuid().ToString("N");
        var payloadPath = Path.Combine(root, $"$R{suffix}");
        var metadataPath = Path.Combine(root, $"$I{suffix}");
        File.WriteAllText(payloadPath, "payload");
        File.WriteAllText(metadataPath, "metadata");

        try
        {
            using (File.Open(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var method = typeof(RecycleBinTestHelper).GetMethod(
                    "RestoreAndPurge",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var cleaned = (bool)method!.Invoke(
                    null,
                    [new RecycleBinItemStub(payloadPath), new RecycleBinStub(), null])!;

                Assert.IsFalse(cleaned, "Partial direct cleanup must not be reported as successful.");
                Assert.IsFalse(File.Exists(payloadPath));
                Assert.IsTrue(File.Exists(metadataPath));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RestoreAndPurge_WhenRestoredTargetDeletionFails_DoesNotReportSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"GuraFile-RecycleHelper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var targetPath = Path.Combine(root, "restored.txt");
        FileStream? targetLock = null;

        try
        {
            var item = new RecycleBinItemStub(
                Path.Combine(root, "shell-item"),
                Path.GetFileName(targetPath),
                _ =>
                {
                    File.WriteAllText(targetPath, "restored");
                    targetLock = File.Open(targetPath, FileMode.Open, FileAccess.Read, FileShare.None);
                });
            var method = typeof(RecycleBinTestHelper).GetMethod(
                "RestoreAndPurge",
                BindingFlags.NonPublic | BindingFlags.Static);

            var cleaned = (bool)method!.Invoke(
                null,
                [item, new RecycleBinStub(root), null])!;

            Assert.IsFalse(cleaned, "A restored target that could not be deleted must not be reported as cleaned.");
            Assert.IsTrue(File.Exists(targetPath));
        }
        finally
        {
            targetLock?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RestoreAndPurge_WhenRestoreTargetNeverAppears_DoesNotReportSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"GuraFile-RecycleHelper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var item = new RecycleBinItemStub(
                Path.Combine(root, "shell-item"),
                "missing.txt",
                _ => { });
            var method = typeof(RecycleBinTestHelper).GetMethod(
                "RestoreAndPurge",
                BindingFlags.NonPublic | BindingFlags.Static);

            var cleaned = (bool)method!.Invoke(
                null,
                [item, new RecycleBinStub(root), null])!;

            Assert.IsFalse(cleaned, "A restore with no observable target must not be reported as cleaned.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RestoreAndPurge_WhenRestoreTargetNeverAppears_PreservesExistingSamePrefixFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"GuraFile-RecycleHelper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var existingPath = Path.Combine(root, "missing.txt.user-data");
        const string existingContent = "keep me";
        File.WriteAllText(existingPath, existingContent);

        try
        {
            var item = new RecycleBinItemStub(
                Path.Combine(root, "shell-item"),
                "missing.txt",
                _ => { });
            var method = typeof(RecycleBinTestHelper).GetMethod(
                "RestoreAndPurge",
                BindingFlags.NonPublic | BindingFlags.Static);

            var cleaned = (bool)method!.Invoke(
                null,
                [item, new RecycleBinStub(root), null])!;

            Assert.IsFalse(cleaned, "A restore with no observable exact target must not be reported as cleaned.");
            Assert.IsTrue(File.Exists(existingPath), "An existing same-prefix file must not be deleted.");
            Assert.AreEqual(existingContent, File.ReadAllText(existingPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RestoreAndPurge_WhenExactRestoreTargetIsDeleted_ReportsSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"GuraFile-RecycleHelper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var targetPath = Path.Combine(root, "restored.txt");

        try
        {
            var item = new RecycleBinItemStub(
                Path.Combine(root, "shell-item"),
                Path.GetFileName(targetPath),
                _ => File.WriteAllText(targetPath, "restored"));
            var method = typeof(RecycleBinTestHelper).GetMethod(
                "RestoreAndPurge",
                BindingFlags.NonPublic | BindingFlags.Static);

            var cleaned = (bool)method!.Invoke(
                null,
                [item, new RecycleBinStub(root), null])!;

            Assert.IsTrue(cleaned, "An observed exact restore target deleted successfully must be reported as cleaned.");
            Assert.IsFalse(File.Exists(targetPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class RecycleBinItemStub
{
    private readonly Action<string>? _invokeVerb;

    public RecycleBinItemStub(string path, string? name = null, Action<string>? invokeVerb = null)
    {
        Path = path;
        Name = name ?? System.IO.Path.GetFileName(path);
        _invokeVerb = invokeVerb;
    }

    public string Path { get; }

    public string Name { get; }

    public void InvokeVerb(string verb)
    {
        if (_invokeVerb == null)
        {
            throw new InvalidOperationException(verb);
        }

        _invokeVerb(verb);
    }
}

internal sealed class RecycleBinStub
{
    private readonly string _originalDirectory;

    public RecycleBinStub(string originalDirectory = "")
    {
        _originalDirectory = originalDirectory;
    }

    public string GetDetailsOf(object item, int column) => _originalDirectory;
}
