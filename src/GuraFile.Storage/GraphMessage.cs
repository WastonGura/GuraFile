using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuraFile.Storage;

public static class GraphMessageTypes
{
    public const string RenderSnapshot = "renderSnapshot";
    public const string FitViewport = "fitViewport";
    public const string SetBroadTagsVisible = "setBroadTagsVisible";
    public const string Ready = "ready";
    public const string FirstFrameRendered = "firstFrameRendered";
    public const string Error = "error";
}

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
}
