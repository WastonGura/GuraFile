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
        string select = GraphMessageTypes.SelectNode;
        string selectionChanged = GraphMessageTypes.SelectionChanged;
        string setSelection = GraphMessageTypes.SetSelection;
        string nodeSelected = GraphMessageTypes.NodeSelected;
        string nodeActivated = GraphMessageTypes.NodeActivated;
        string ready = GraphMessageTypes.Ready;
        string frame = GraphMessageTypes.FirstFrameRendered;
        string err = GraphMessageTypes.Error;
        string ver = GraphMessage.CurrentVersion;

        Assert.AreEqual("renderSnapshot", render);
        Assert.AreEqual("fitViewport", fit);
        Assert.AreEqual("setBroadTagsVisible", broad);
        Assert.AreEqual("selectNode", select);
        Assert.AreEqual("selectionChanged", selectionChanged);
        Assert.AreEqual("setSelection", setSelection);
        Assert.AreEqual("nodeSelected", nodeSelected);
        Assert.AreEqual("nodeActivated", nodeActivated);
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

    [TestMethod]
    public void OutboundSelectNodeSerializesCorrectly()
    {
        var jsonWithNode = GraphMessageSerializer.SerializeSelectNode("file:42");
        using (var doc = JsonDocument.Parse(jsonWithNode))
        {
            var root = doc.RootElement;
            Assert.AreEqual("selectNode", root.GetProperty("type").GetString());
            Assert.AreEqual("1.0", root.GetProperty("version").GetString());
            Assert.AreEqual("file:42", root.GetProperty("payload").GetProperty("nodeId").GetString());
        }

        var jsonWithNull = GraphMessageSerializer.SerializeSelectNode(null);
        using (var doc = JsonDocument.Parse(jsonWithNull))
        {
            var root = doc.RootElement;
            Assert.AreEqual("selectNode", root.GetProperty("type").GetString());
            Assert.AreEqual("1.0", root.GetProperty("version").GetString());
            Assert.AreEqual(JsonValueKind.Null, root.GetProperty("payload").GetProperty("nodeId").ValueKind);
        }
    }

    [TestMethod]
    public void SelectNodePayloadDeserializesCorrectly()
    {
        var json = """
            {
                "type": "selectNode",
                "version": "1.0",
                "payload": {
                    "nodeId": "file:100"
                }
            }
            """;
        var message = GraphMessageSerializer.Deserialize(json);
        Assert.AreEqual("selectNode", message.Type);

        var payload = GraphMessageSerializer.ParseSelectNode(message);
        Assert.AreEqual("file:100", payload.NodeId);
    }

    [TestMethod]
    public void InboundNodeSelectedDeserializesWithFullPayload()
    {
        var json = """
            {
                "type": "nodeSelected",
                "version": "1.0",
                "payload": {
                    "nodeId": "file:12",
                    "kind": "file",
                    "fileId": 12,
                    "tagId": null,
                    "label": "Document.docx"
                }
            }
            """;
        var message = GraphMessageSerializer.Deserialize(json);
        Assert.AreEqual("nodeSelected", message.Type);

        var action = GraphMessageSerializer.ParseNodeAction(message);
        Assert.AreEqual("file:12", action.NodeId);
        Assert.AreEqual("file", action.Kind);
        Assert.AreEqual(12L, action.FileId);
        Assert.IsNull(action.TagId);
        Assert.AreEqual("Document.docx", action.Label);
    }

    [TestMethod]
    public void InboundNodeActivatedDeserializesWithFullPayload()
    {
        var json = """
            {
                "type": "nodeActivated",
                "version": "1.0",
                "payload": {
                    "nodeId": "file:99",
                    "kind": "file",
                    "fileId": 99,
                    "tagId": null,
                    "label": "Music.mp3"
                }
            }
            """;
        var message = GraphMessageSerializer.Deserialize(json);
        Assert.AreEqual("nodeActivated", message.Type);

        var action = GraphMessageSerializer.ParseNodeAction(message);
        Assert.AreEqual("file:99", action.NodeId);
        Assert.AreEqual("file", action.Kind);
        Assert.AreEqual(99L, action.FileId);
        Assert.IsNull(action.TagId);
        Assert.AreEqual("Music.mp3", action.Label);
    }

    [TestMethod]
    public void OutboundNodeActionSerializesCorrectly()
    {
        var payload = new GraphNodeActionPayload("tag:5", "tag", null, 5L, "工作");
        var jsonSelected = GraphMessageSerializer.SerializeNodeSelected(payload);
        using (var doc = JsonDocument.Parse(jsonSelected))
        {
            var root = doc.RootElement;
            Assert.AreEqual("nodeSelected", root.GetProperty("type").GetString());
            Assert.AreEqual("tag:5", root.GetProperty("payload").GetProperty("nodeId").GetString());
            Assert.AreEqual("tag", root.GetProperty("payload").GetProperty("kind").GetString());
            Assert.AreEqual(5L, root.GetProperty("payload").GetProperty("tagId").GetInt64());
            Assert.AreEqual("工作", root.GetProperty("payload").GetProperty("label").GetString());
        }

        var jsonActivated = GraphMessageSerializer.SerializeNodeActivated(payload);
        using (var doc = JsonDocument.Parse(jsonActivated))
        {
            var root = doc.RootElement;
            Assert.AreEqual("nodeActivated", root.GetProperty("type").GetString());
        }
    }

    [TestMethod]
    public void OutboundSetSelectionSerializesCorrectly()
    {
        var json = GraphMessageSerializer.SerializeSetSelection([10L, 20L]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("setSelection", root.GetProperty("type").GetString());
        Assert.AreEqual("1.0", root.GetProperty("version").GetString());
        var fileIds = root.GetProperty("payload").GetProperty("fileIds");
        Assert.AreEqual(2, fileIds.GetArrayLength());
        Assert.AreEqual(10L, fileIds[0].GetInt64());
        Assert.AreEqual(20L, fileIds[1].GetInt64());
    }

    [TestMethod]
    public void InboundSetSelectionDeserializesCorrectly()
    {
        var json = """
            {
                "type": "setSelection",
                "version": "1.0",
                "payload": {
                    "fileIds": [1, 2, 3]
                }
            }
            """;
        var message = GraphMessageSerializer.Deserialize(json);
        Assert.AreEqual("setSelection", message.Type);

        var payload = GraphMessageSerializer.ParseSetSelection(message);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, payload.FileIds.ToArray());
    }

    [TestMethod]
    public void OutboundSelectionChangedSerializesCorrectly()
    {
        var json = GraphMessageSerializer.SerializeSelectionChanged([100L, 200L, 300L]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("selectionChanged", root.GetProperty("type").GetString());
        Assert.AreEqual("1.0", root.GetProperty("version").GetString());
        var payload = root.GetProperty("payload");
        Assert.AreEqual(3, payload.GetProperty("count").GetInt32());
        var fileIds = payload.GetProperty("fileIds");
        Assert.AreEqual(3, fileIds.GetArrayLength());
        Assert.AreEqual(100L, fileIds[0].GetInt64());
        Assert.AreEqual(200L, fileIds[1].GetInt64());
        Assert.AreEqual(300L, fileIds[2].GetInt64());
    }

    [TestMethod]
    public void InboundSelectionChangedDeserializesCorrectly()
    {
        var json = """
            {
                "type": "selectionChanged",
                "version": "1.0",
                "payload": {
                    "fileIds": [42, 43],
                    "count": 2
                }
            }
            """;
        var message = GraphMessageSerializer.Deserialize(json);
        Assert.AreEqual("selectionChanged", message.Type);

        var payload = GraphMessageSerializer.ParseSelectionChanged(message);
        Assert.AreEqual(2, payload.Count);
        CollectionAssert.AreEqual(new long[] { 42, 43 }, payload.FileIds.ToArray());
    }
}
