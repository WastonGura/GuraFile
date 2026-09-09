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

        Assert.AreEqual("0.5.2", project.Descendants("Version").Single().Value);
        Assert.AreEqual("0.5.2.0", project.Descendants("AssemblyVersion").Single().Value);
        Assert.AreEqual("0.5.2.0", project.Descendants("FileVersion").Single().Value);
        Assert.IsTrue(File.Exists(Path.Combine(root, "CHANGELOG.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "THIRD_PARTY_NOTICES.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "scripts", "PackageRelease.ps1")));

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        StringAssert.Contains(readme, "正常负载目标为 2 秒内更新");
        StringAssert.Contains(readme, "本版不使用 NTFS USN Journal");
        StringAssert.Contains(readme, "删除操作仅支持删除到 Windows 回收站");
        StringAssert.Contains(readme, "图谱");
        StringAssert.Contains(readme, "300");
        StringAssert.Contains(readme, "GuraFile-v0.5.2-win-x64.zip");
        StringAssert.Contains(readme, @".\scripts\PackageRelease.ps1 -Version 0.5.2");

        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        StringAssert.Contains(changelog, "## 0.5.2");
        StringAssert.Contains(changelog, "#104");
        StringAssert.Contains(changelog, "#105");
        StringAssert.Contains(changelog, "#106");
        StringAssert.Contains(changelog, "#107");
        StringAssert.Contains(changelog, "## 0.5.1");
        StringAssert.Contains(changelog, "#96");
        StringAssert.Contains(changelog, "#97");
        StringAssert.Contains(changelog, "#98");
        StringAssert.Contains(changelog, "#99");
        StringAssert.Contains(changelog, "## 0.5.0");
        StringAssert.Contains(changelog, "#71");
        StringAssert.Contains(changelog, "#72");
        StringAssert.Contains(changelog, "#73");
        StringAssert.Contains(changelog, "#74");
        StringAssert.Contains(changelog, "#75");
        StringAssert.Contains(changelog, "#76");
        StringAssert.Contains(changelog, "#77");
        StringAssert.Contains(changelog, "#78");
        StringAssert.Contains(changelog, "#79");
        StringAssert.Contains(changelog, "#80");
        StringAssert.Contains(changelog, "#81");
        StringAssert.Contains(changelog, "#82");

        var notices = File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md"));
        StringAssert.Contains(notices, "GuraFile v0.5.2");
        StringAssert.Contains(notices, "Cytoscape.js");
        StringAssert.Contains(notices, "3.30.2");
        StringAssert.Contains(notices, "MIT");

        var packageScript = File.ReadAllText(Path.Combine(root, "scripts", "PackageRelease.ps1"));
        StringAssert.Contains(packageScript, "[string]$Version = '0.5.2'");
        StringAssert.Contains(packageScript, "cytoscape.min.js");
        StringAssert.Contains(packageScript, "index.html");
        StringAssert.Contains(packageScript, "graph.css");
        StringAssert.Contains(packageScript, "graph.js");

        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "docs", "RELEASE_CHECKLIST.md")),
            "# v0.5.2 发布验收");
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
