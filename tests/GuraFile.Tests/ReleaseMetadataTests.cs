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

        Assert.AreEqual("0.2.0", project.Descendants("Version").Single().Value);
        Assert.AreEqual("0.2.0.0", project.Descendants("AssemblyVersion").Single().Value);
        Assert.AreEqual("0.2.0.0", project.Descendants("FileVersion").Single().Value);
        Assert.IsTrue(File.Exists(Path.Combine(root, "CHANGELOG.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "THIRD_PARTY_NOTICES.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "scripts", "PackageRelease.ps1")));

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        StringAssert.Contains(readme, "正常负载目标为 2 秒内更新");
        StringAssert.Contains(readme, "本版不使用 NTFS USN Journal");
        StringAssert.Contains(readme, "不提供复制、移动、重命名或删除");
        StringAssert.Contains(readme, "尚未提供图谱");
        StringAssert.Contains(readme, "GuraFile-v0.2.0-win-x64.zip");

        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")), "## 0.2.0 — Alpha");
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md")), "GuraFile v0.2.0");
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "scripts", "PackageRelease.ps1")),
            "[string]$Version = '0.2.0'");
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "docs", "RELEASE_CHECKLIST.md")),
            "# v0.2.0 发布验收");
    }

    private static string RepositoryRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
