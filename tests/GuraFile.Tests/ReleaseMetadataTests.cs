using System.Runtime.Versioning;
using System.Xml.Linq;

namespace GuraFile.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class ReleaseMetadataTests
{
    [TestMethod]
    public void VersionDocumentationLicensesAndPackagingStayAligned()
    {
        var root = RepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "GuraFile", "GuraFile.csproj"));

        Assert.AreEqual("0.3.2", project.Descendants("Version").Single().Value);
        Assert.AreEqual("0.3.2.0", project.Descendants("AssemblyVersion").Single().Value);
        Assert.AreEqual("0.3.2.0", project.Descendants("FileVersion").Single().Value);
        Assert.IsTrue(File.Exists(Path.Combine(root, "CHANGELOG.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "THIRD_PARTY_NOTICES.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "scripts", "PackageRelease.ps1")));

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        StringAssert.Contains(readme, "正常负载目标为 2 秒内更新");
        StringAssert.Contains(readme, "本版不使用 NTFS USN Journal");
        StringAssert.Contains(readme, "删除操作仅支持删除到 Windows 回收站");
        StringAssert.Contains(readme, "尚未提供图谱");
        StringAssert.Contains(readme, "GuraFile-v0.3.2-win-x64.zip");
        StringAssert.Contains(readme, @".\scripts\PackageRelease.ps1 -Version 0.3.2");

        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")), "## 0.3.2");
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md")), "GuraFile v0.3.2");
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "scripts", "PackageRelease.ps1")),
            "[string]$Version = '0.3.2'");
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "docs", "RELEASE_CHECKLIST.md")),
            "# v0.3.2 发布验收");
    }

    [TestMethod]
    public void ReleaseChecklistHasNoUncheckedRequiredItems()
    {
        var root = RepositoryRoot();
        var checklistPath = Path.Combine(root, "docs", "RELEASE_CHECKLIST.md");
        Assert.IsTrue(File.Exists(checklistPath), $"Checklist missing at {checklistPath}");

        var lines = File.ReadAllLines(checklistPath);
        var uncheckedItems = lines
            .Where(line => line.TrimStart().StartsWith("- [ ]", StringComparison.Ordinal))
            .ToList();

        Assert.IsEmpty(uncheckedItems,
            $"Found unchecked required items in RELEASE_CHECKLIST.md:\n{string.Join(Environment.NewLine, uncheckedItems)}");

        var checkedItems = lines
            .Where(line => line.TrimStart().StartsWith("- [x]", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.IsGreaterThanOrEqualTo(15, checkedItems.Count, "Checklist should contain all verified items.");
    }

    private static string RepositoryRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
