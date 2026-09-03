using System.Text.Json;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class GraphMessageProtocolTests
{
    [TestMethod]
    public void ProtocolConstantsMatchSpecification()
    {
        string render = GraphMessageTypes.RenderSnapshot;
        string fit = GraphMessageTypes.FitViewport;
        string broad = GraphMessageTypes.SetBroadTagsVisible;
        string ready = GraphMessageTypes.Ready;
        string frame = GraphMessageTypes.FirstFrameRendered;
        string err = GraphMessageTypes.Error;
        string ver = GraphMessage.CurrentVersion;

        Assert.AreEqual("renderSnapshot", render);
        Assert.AreEqual("fitViewport", fit);
        Assert.AreEqual("setBroadTagsVisible", broad);
        Assert.AreEqual("ready", ready);
        Assert.AreEqual("firstFrameRendered", frame);
        Assert.AreEqual("error", err);
        Assert.AreEqual("1.0", ver);
    }

    [TestMethod]
    public void OutboundRenderSnapshotSerializesStructuredJsonCorrectly()
    {
        var snapshot = new GraphSnapshot(
            GraphSnapshotStatus.Ready,
            1,
            [new GraphFileNode("file:1", 1, "test.png", "图片")],
            [new GraphTagNode("tag:2", 2, "Photo", GraphTagSource.User, false)],
            [new GraphEdge("file:1", "tag:2")]);

        var json = GraphMessageSerializer.SerializeRenderSnapshot(snapshot);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("renderSnapshot", root.GetProperty("type").GetString());
        Assert.AreEqual("1.0", root.GetProperty("version").GetString());

        var payload = root.GetProperty("payload");
        Assert.AreEqual(1, payload.GetProperty("fileCount").GetInt32());

        var files = payload.GetProperty("files");
        Assert.AreEqual(1, files.GetArrayLength());
        Assert.AreEqual("file:1", files[0].GetProperty("id").GetString());
        Assert.AreEqual(1L, files[0].GetProperty("fileId").GetInt64());
        Assert.AreEqual("test.png", files[0].GetProperty("label").GetString());
        Assert.AreEqual("图片", files[0].GetProperty("category").GetString());

        var tags = payload.GetProperty("tags");
        Assert.AreEqual(1, tags.GetArrayLength());
        Assert.AreEqual("tag:2", tags[0].GetProperty("id").GetString());
        Assert.AreEqual("Photo", tags[0].GetProperty("label").GetString());
        Assert.AreEqual("user", tags[0].GetProperty("source").GetString());
        Assert.IsFalse(tags[0].GetProperty("isBroad").GetBoolean());

        var edges = payload.GetProperty("edges");
        Assert.AreEqual(1, edges.GetArrayLength());
        Assert.AreEqual("file:1", edges[0].GetProperty("source").GetString());
        Assert.AreEqual("tag:2", edges[0].GetProperty("target").GetString());
    }

    [TestMethod]
    public void OutboundFitViewportSerializesCorrectly()
    {
        var json = GraphMessageSerializer.SerializeFitViewport();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("fitViewport", root.GetProperty("type").GetString());
        Assert.AreEqual("1.0", root.GetProperty("version").GetString());
    }

    [TestMethod]
    public void OutboundSetBroadTagsVisibleSerializesCorrectly()
    {
        var json = GraphMessageSerializer.SerializeSetBroadTagsVisible(true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("setBroadTagsVisible", root.GetProperty("type").GetString());
        Assert.AreEqual("1.0", root.GetProperty("version").GetString());
        Assert.IsTrue(root.GetProperty("payload").GetProperty("visible").GetBoolean());
    }

    [TestMethod]
    public void InboundReadyMessageDeserializesCorrectly()
    {
        var json = """{"type":"ready","version":"1.0"}""";
        var message = GraphMessageSerializer.Deserialize(json);

        Assert.IsNotNull(message);
        Assert.AreEqual("ready", message.Type);
        Assert.AreEqual("1.0", message.Version);
    }

    [TestMethod]
    public void InboundFirstFrameRenderedMessageDeserializesWithMetrics()
    {
        var json = """
            {
                "type": "firstFrameRendered",
                "version": "1.0",
                "payload": {
                    "nodeCount": 42,
                    "edgeCount": 60,
                    "renderDurationMs": 123.45
                }
            }
            """;
        var message = GraphMessageSerializer.Deserialize(json);

        Assert.IsNotNull(message);
        Assert.AreEqual("firstFrameRendered", message.Type);
        var metrics = GraphMessageSerializer.ParseFirstFrameMetrics(message);
        Assert.AreEqual(42, metrics.NodeCount);
        Assert.AreEqual(60, metrics.EdgeCount);
        Assert.AreEqual(123.45, metrics.RenderDurationMs, 0.001);
    }

    [TestMethod]
    public void InboundErrorMessageDeserializesWithPayload()
    {
        var json = """
            {
                "type": "error",
                "version": "1.0",
                "payload": {
                    "message": "Cytoscape layout failed"
                }
            }
            """;
        var message = GraphMessageSerializer.Deserialize(json);

        Assert.IsNotNull(message);
        Assert.AreEqual("error", message.Type);
        var error = GraphMessageSerializer.ParseErrorMessage(message);
        Assert.AreEqual("Cytoscape layout failed", error);
    }
}
