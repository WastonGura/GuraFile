using System.Xml.Linq;

namespace GuraFile.Tests;

[TestClass]
public sealed class ReleaseMetadataTests
{
    [TestMethod]
    public void VersionDocumentationLicensesAndPackagingStayAligned()
    {
        var root = RepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "GuraFile", "GuraFile.csproj"));

        Assert.AreEqual("0.1.0", project.Descendants("Version").Single().Value);
        Assert.IsTrue(File.Exists(Path.Combine(root, "CHANGELOG.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "THIRD_PARTY_NOTICES.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "scripts", "PackageRelease.ps1")));

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        StringAssert.Contains(readme, "没有实时监听");
        StringAssert.Contains(readme, "不提供复制、移动、重命名或删除");
        StringAssert.Contains(readme, "尚未提供图谱");
    }

    private static string RepositoryRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
