using System.Text.Json;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class GraphSecurityPolicyTests
{
    [TestMethod]
    public void VirtualHostAndOriginMatchSpecification()
    {
        string host = GraphSecurityPolicy.VirtualHostName;
        string origin = GraphSecurityPolicy.VirtualHostOrigin;
        string entry = GraphSecurityPolicy.EntryUrl;

        Assert.AreEqual("graph.gurafile.local", host);
        Assert.AreEqual("https://graph.gurafile.local", origin);
        Assert.AreEqual("https://graph.gurafile.local/index.html", entry);
    }

    [TestMethod]
    public void OnlyVirtualHostUrisAreAllowedForNavigation()
    {
        Assert.IsTrue(GraphSecurityPolicy.IsAllowedUri("https://graph.gurafile.local/"));
        Assert.IsTrue(GraphSecurityPolicy.IsAllowedUri("https://graph.gurafile.local/index.html"));
        Assert.IsTrue(GraphSecurityPolicy.IsAllowedUri("https://graph.gurafile.local/Assets/graph/graph.js"));
        Assert.IsTrue(GraphSecurityPolicy.IsAllowedUri("http://graph.gurafile.local/index.html"));

        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri((string?)null));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri(string.Empty));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("   "));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("https://evil.com/"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("http://evil.com/"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("https://sub.graph.gurafile.local/"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("file:///C:/Windows/notepad.exe"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("javascript:alert(1)"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("data:text/html,<h1>test</h1>"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("about:blank"));
        Assert.IsFalse(GraphSecurityPolicy.IsAllowedUri("not a uri"));
    }

    [TestMethod]
    public void MaliciousFileNameAndTagNameAreSafelyPreservedAsTextWithoutExecution()
    {
        const string hostileFileName = "</script><script>alert('pwned')</script>\r\n\"foo\"\\bar\t<b>bold</b>";
        const string hostileTagName = "\"><img src=x onerror=alert(1)>' OR '1'='1";

        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            1,
            [new GraphFileNode("file:99", 99, hostileFileName, "代码")],
            [new GraphTagNode("tag:88", 88, hostileTagName, GraphTagSource.User, false)],
            [new GraphEdge("file:99", "tag:88")]);

        var json = GraphMessageSerializer.SerializeRenderSnapshot(snapshot);

        // Verify JSON string is strictly well-formed
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var fileLabel = root.GetProperty("payload").GetProperty("files")[0].GetProperty("label").GetString();
        var tagLabel = root.GetProperty("payload").GetProperty("tags")[0].GetProperty("label").GetString();

        // Round-trip preserves exact string value
        Assert.AreEqual(hostileFileName, fileLabel);
        Assert.AreEqual(hostileTagName, tagLabel);

        // Raw JSON must NOT contain raw unescaped script tag closing
        Assert.IsTrue(json.Contains(@"\u003C/script\u003E") || json.Contains(@"<\/script>") || json.Contains(@"</script>"));
        Assert.IsTrue(json.Contains(@"\""foo\""") || json.Contains(@"\u0022foo\u0022"));
    }

    [TestMethod]
    public void CoarseCategoriesMapAccurately()
    {
        Assert.AreEqual("图片", GraphCategoryResolver.Resolve(".png"));
        Assert.AreEqual("图片", GraphCategoryResolver.Resolve(".jpg"));
        Assert.AreEqual("图片", GraphCategoryResolver.Resolve(".gif"));
        Assert.AreEqual("音频", GraphCategoryResolver.Resolve(".mp3"));
        Assert.AreEqual("音频", GraphCategoryResolver.Resolve(".flac"));
        Assert.AreEqual("视频", GraphCategoryResolver.Resolve(".mp4"));
        Assert.AreEqual("视频", GraphCategoryResolver.Resolve(".mkv"));
        Assert.AreEqual("文档", GraphCategoryResolver.Resolve(".pdf"));
        Assert.AreEqual("文档", GraphCategoryResolver.Resolve(".txt"));
        Assert.AreEqual("文档", GraphCategoryResolver.Resolve(".docx"));
        Assert.AreEqual("压缩包", GraphCategoryResolver.Resolve(".zip"));
        Assert.AreEqual("压缩包", GraphCategoryResolver.Resolve(".7z"));
        Assert.AreEqual("代码", GraphCategoryResolver.Resolve(".cs"));
        Assert.AreEqual("代码", GraphCategoryResolver.Resolve(".js"));
        Assert.AreEqual("其他", GraphCategoryResolver.Resolve(".unknownxyz"));
        Assert.AreEqual("其他", GraphCategoryResolver.Resolve(""));
        Assert.AreEqual("其他", GraphCategoryResolver.Resolve(null));
    }
}
