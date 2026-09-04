using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuraFile.Storage;

public static class GraphMessageTypes
{
    public const string RenderSnapshot = "renderSnapshot";
    public const string FitViewport = "fitViewport";
    public const string SetBroadTagsVisible = "setBroadTagsVisible";
    public const string SelectNode = "selectNode";
    public const string SelectionChanged = "selectionChanged";
    public const string SetSelection = "setSelection";
    public const string NodeSelected = "nodeSelected";
    public const string NodeActivated = "nodeActivated";
    public const string Ready = "ready";
    public const string FirstFrameRendered = "firstFrameRendered";
    public const string Error = "error";
}

public sealed record SelectNodePayload(
    [property: JsonPropertyName("nodeId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? NodeId);

public sealed record GraphSelectionChangedPayload(
    [property: JsonPropertyName("fileIds")] IReadOnlyList<long> FileIds,
    [property: JsonPropertyName("count")] int Count);

public sealed record GraphSetSelectionPayload(
    [property: JsonPropertyName("fileIds")] IReadOnlyList<long> FileIds);

public sealed record GraphNodeActionPayload(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("fileId")] long? FileId = null,
    [property: JsonPropertyName("tagId")] long? TagId = null,
    [property: JsonPropertyName("label")] string? Label = null);

public sealed record GraphMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] string Version = GraphMessage.CurrentVersion,
    [property: JsonPropertyName("payload")] JsonElement? Payload = null)
{
    public const string CurrentVersion = "1.0";
}

public sealed record GraphFileDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fileId")] long FileId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("category")] string Category);

public sealed record GraphTagDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tagId")] long TagId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("isBroad")] bool IsBroad);

public sealed record GraphEdgeDto(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("target")] string Target);

public sealed record RenderSnapshotPayload(
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("files")] IReadOnlyList<GraphFileDto> Files,
    [property: JsonPropertyName("tags")] IReadOnlyList<GraphTagDto> Tags,
    [property: JsonPropertyName("edges")] IReadOnlyList<GraphEdgeDto> Edges);

public sealed record SetBroadTagsVisiblePayload(
    [property: JsonPropertyName("visible")] bool Visible);

public sealed record FirstFrameRenderedPayload(
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("edgeCount")] int EdgeCount,
    [property: JsonPropertyName("renderDurationMs")] double RenderDurationMs);

public sealed record ErrorPayload(
    [property: JsonPropertyName("message")] string Message);

public static class GraphMessageSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SerializeRenderSnapshot(GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var fileDtos = snapshot.FileNodes
            .Select(f => new GraphFileDto(f.Id, f.FileId, f.Label, f.Category))
            .ToArray();

        var tagDtos = snapshot.TagNodes
            .Select(t => new GraphTagDto(
                t.Id,
                t.TagId,
                t.Label,
                t.Source == GraphTagSource.Automatic ? "automatic" : "user",
                t.IsBroad))
            .ToArray();

        var edgeDtos = snapshot.Edges
            .Select(e => new GraphEdgeDto(e.SourceId, e.TargetId))
            .ToArray();

        var payload = new RenderSnapshotPayload(snapshot.FileCount, fileDtos, tagDtos, edgeDtos);
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);

        var message = new GraphMessage(
            GraphMessageTypes.RenderSnapshot,
            GraphMessage.CurrentVersion,
            payloadElement);

        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string SerializeFitViewport()
    {
        var message = new GraphMessage(
            GraphMessageTypes.FitViewport,
            GraphMessage.CurrentVersion,
            null);

        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string SerializeSetBroadTagsVisible(bool visible)
    {
        var payload = new SetBroadTagsVisiblePayload(visible);
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);

        var message = new GraphMessage(
            GraphMessageTypes.SetBroadTagsVisible,
            GraphMessage.CurrentVersion,
            payloadElement);

        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static GraphMessage Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var message = JsonSerializer.Deserialize<GraphMessage>(json, JsonOptions);
        if (message is null || string.IsNullOrWhiteSpace(message.Type))
        {
            throw new JsonException("Invalid graph message format.");
        }

        return message;
    }

    public static FirstFrameRenderedPayload ParseFirstFrameMetrics(GraphMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload is null)
        {
            throw new ArgumentException("Payload is missing in firstFrameRendered message.", nameof(message));
        }

        return message.Payload.Value.Deserialize<FirstFrameRenderedPayload>(JsonOptions)
               ?? throw new JsonException("Failed to deserialize FirstFrameRenderedPayload.");
    }

    public static string ParseErrorMessage(GraphMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload is null)
        {
            return "Unknown error";
        }

        var error = message.Payload.Value.Deserialize<ErrorPayload>(JsonOptions);
        return error?.Message ?? "Unknown error";
    }

    public static string SerializeSelectNode(string? nodeId)
    {
        var payload = new SelectNodePayload(nodeId);
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);

        var message = new GraphMessage(
            GraphMessageTypes.SelectNode,
            GraphMessage.CurrentVersion,
            payloadElement);

        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string SerializeNodeSelected(GraphNodeActionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);

        var message = new GraphMessage(
            GraphMessageTypes.NodeSelected,
            GraphMessage.CurrentVersion,
            payloadElement);

        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string SerializeNodeActivated(GraphNodeActionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);

        var message = new GraphMessage(
            GraphMessageTypes.NodeActivated,
            GraphMessage.CurrentVersion,
            payloadElement);

        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static SelectNodePayload ParseSelectNode(GraphMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload is null)
        {
            return new SelectNodePayload(null);
        }

        return message.Payload.Value.Deserialize<SelectNodePayload>(JsonOptions)
               ?? new SelectNodePayload(null);
    }

    public static GraphNodeActionPayload ParseNodeAction(GraphMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload is null)
        {
            throw new ArgumentException("Payload is missing in node action message.", nameof(message));
        }

        return message.Payload.Value.Deserialize<GraphNodeActionPayload>(JsonOptions)
               ?? throw new JsonException("Failed to deserialize GraphNodeActionPayload.");
    }

    public static string SerializeSelectionChanged(IReadOnlyList<long> fileIds)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var payload = new GraphSelectionChangedPayload(fileIds, fileIds.Count);
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);

        var message = new GraphMessage(
            GraphMessageTypes.SelectionChanged,
            GraphMessage.CurrentVersion,
            payloadElement);

        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static GraphSelectionChangedPayload ParseSelectionChanged(GraphMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload is null)
        {
            return new GraphSelectionChangedPayload([], 0);
        }

        return message.Payload.Value.Deserialize<GraphSelectionChangedPayload>(JsonOptions)
               ?? new GraphSelectionChangedPayload([], 0);
    }

    public static string SerializeSetSelection(IReadOnlyList<long> fileIds)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var payload = new GraphSetSelectionPayload(fileIds);
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);

        var message = new GraphMessage(
            GraphMessageTypes.SetSelection,
            GraphMessage.CurrentVersion,
            payloadElement);

        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static GraphSetSelectionPayload ParseSetSelection(GraphMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload is null)
        {
            return new GraphSetSelectionPayload([]);
        }

        return message.Payload.Value.Deserialize<GraphSetSelectionPayload>(JsonOptions)
               ?? new GraphSetSelectionPayload([]);
    }
}
