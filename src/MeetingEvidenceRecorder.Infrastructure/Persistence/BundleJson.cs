using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeetingEvidenceRecorder.Core.Evidence;

namespace MeetingEvidenceRecorder.Infrastructure.Persistence;

public static class BundleJson
{
    public static UTF8Encoding Utf8 { get; } = new(false, true);
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false
        };
        options.Converters.Add(new JsonStringEnumConverter<SessionStatus>(JsonNamingPolicy.SnakeCaseLower, false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static byte[] SerializeSession(SessionManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, Options);

    public static SessionManifest ReadSession(ReadOnlySpan<byte> bytes)
    {
        Utf8.GetCharCount(bytes); // Validate even unknown string fields with the strict UTF-8 decoder.
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Session must be an object.");
        if (!root.TryGetProperty("created_at", out var created) || created.ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(created.GetString()!, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$"))
            throw new JsonException("created_at requires RFC3339 date/time with timezone.");
        if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            status.GetString() is not ("initializing" or "recording" or "paused" or "finalizing" or "completed" or "incomplete" or "failed"))
            throw new JsonException("Invalid wire status.");
        RejectNullDescriptions(root, "application", "name", "version");
        RejectNullDescriptions(root, "platform", "os", "architecture");
        if (root.TryGetProperty("recording", out var recording) && recording.ValueKind == JsonValueKind.Object)
        {
            RejectNullDescriptions(recording, "video", "source_type", "width", "height", "fps");
            RejectNullDescriptions(recording, "audio", "system_audio", "microphone", "output_mode",
                "system_audio_capture_mode", "synchronized_to_recording_timeline");
            if (recording.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Null)
                throw new JsonException("Omit unavailable recording.file; a supplied path must be a string.");
        }
        if (root.TryGetProperty("events_file", out var events) && events.ValueKind == JsonValueKind.Null)
            throw new JsonException("events_file cannot be null.");
        return JsonSerializer.Deserialize<SessionManifest>(bytes, Options) ?? throw new JsonException("Null session.");
    }

    private static void RejectNullDescriptions(JsonElement parent, string name, params string[] fields)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return;
        foreach (var field in fields)
            if (value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.Null)
                throw new JsonException($"{name}.{field} can be omitted, but cannot be null.");
    }

    public static byte[] SerializeEvent(RecordingEvent entry) => JsonSerializer.SerializeToUtf8Bytes(entry, entry.GetType(), Options);

    public static RecordingEvent ReadEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new JsonException("Event requires string type.");
        if (type.GetString() == "screenshot")
            return JsonSerializer.Deserialize<ScreenshotEvent>(json, Options) ?? throw new JsonException("Null event.");
        // Deserialize the common envelope independently: unknown event fields remain opaque.
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(json, Options) ?? throw new JsonException("Null event.");
        return new UnknownRecordingEvent
        {
            EventId = envelope.EventId, EventType = envelope.Type, TimestampMs = envelope.TimestampMs,
            Extensions = envelope.Extensions
        };
    }

    private sealed record EventEnvelope
    {
        public required string EventId { get; init; }
        public required string Type { get; init; }
        public required long TimestampMs { get; init; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
    }
}
