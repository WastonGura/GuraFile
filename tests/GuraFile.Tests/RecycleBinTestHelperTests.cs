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
}

internal sealed class RecycleBinItemStub
{
    public RecycleBinItemStub(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public void InvokeVerb(string verb) => throw new InvalidOperationException(verb);
}

internal sealed class RecycleBinStub
{
    public string GetDetailsOf(object item, int column) => string.Empty;
}
