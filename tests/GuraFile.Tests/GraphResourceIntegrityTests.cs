using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GuraFile.Tests;

[TestClass]
public sealed class GraphResourceIntegrityTests
{
    [TestMethod]
    public void GraphAssetsExistAndAreOffline()
    {
        var root = RepositoryRoot();
        var graphDir = Path.Combine(root, "src", "GuraFile", "Assets", "graph");

        Assert.IsTrue(Directory.Exists(graphDir), $"Graph directory missing at: {graphDir}");

        var cytoscapeJs = Path.Combine(graphDir, "cytoscape.min.js");
        var indexHtml = Path.Combine(graphDir, "index.html");
        var graphCss = Path.Combine(graphDir, "graph.css");
        var graphJs = Path.Combine(graphDir, "graph.js");

        Assert.IsTrue(File.Exists(cytoscapeJs), "cytoscape.min.js missing");
        Assert.IsTrue(File.Exists(indexHtml), "index.html missing");
        Assert.IsTrue(File.Exists(graphCss), "graph.css missing");
        Assert.IsTrue(File.Exists(graphJs), "graph.js missing");

        // Verify cytoscape.min.js is non-empty and contains cytoscape banner
        var cytoscapeContent = File.ReadAllText(cytoscapeJs);
        Assert.IsGreaterThan(100_000, cytoscapeContent.Length, "cytoscape.min.js is unexpectedly small");
        StringAssert.Contains(cytoscapeContent, "Cytoscape");

        // Verify index.html contains strict CSP
        var htmlContent = File.ReadAllText(indexHtml);
        const string expectedCsp = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none';";
        StringAssert.Contains(htmlContent, expectedCsp);

        // Verify index.html does NOT reference external URLs
        var remoteRefMatch = Regex.Match(htmlContent, @"(?:src|href)\s*=\s*[""']https?://", RegexOptions.IgnoreCase);
        Assert.IsFalse(remoteRefMatch.Success, $"index.html contains remote URL reference: {remoteRefMatch.Value}");
    }

    [TestMethod]
    public void ProjectConfiguresGraphAssetsForOutputCopy()
    {
        var root = RepositoryRoot();
        var projPath = Path.Combine(root, "src", "GuraFile", "GuraFile.csproj");
        var projXml = XDocument.Load(projPath);

        var copyElements = projXml.Descendants()
            .Where(e => e.Name.LocalName is "Content" or "None")
            .Where(e => e.Attribute("Include")?.Value.Contains("Assets/graph", StringComparison.OrdinalIgnoreCase) == true
                     || e.Attribute("Include")?.Value.Contains(@"Assets\graph", StringComparison.OrdinalIgnoreCase) == true
                     || e.Attribute("Update")?.Value.Contains("Assets/graph", StringComparison.OrdinalIgnoreCase) == true
                     || e.Attribute("Update")?.Value.Contains(@"Assets\graph", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.IsNotEmpty(copyElements, "GuraFile.csproj must configure Assets/graph to copy to output directory");
        Assert.IsTrue(copyElements.Any(e => e.Element("CopyToOutputDirectory")?.Value is "PreserveNewest" or "Always"),
            "CopyToOutputDirectory must be PreserveNewest or Always for graph assets");
    }

    [TestMethod]
    public void ThirdPartyNoticesIncludesCytoscapeNotice()
    {
        var root = RepositoryRoot();
        var noticesPath = Path.Combine(root, "THIRD_PARTY_NOTICES.md");
        Assert.IsTrue(File.Exists(noticesPath));

        var noticesContent = File.ReadAllText(noticesPath);
        StringAssert.Contains(noticesContent, "Cytoscape.js");
        StringAssert.Contains(noticesContent, "3.30.2");
        StringAssert.Contains(noticesContent, "MIT");
    }

    private static string RepositoryRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
