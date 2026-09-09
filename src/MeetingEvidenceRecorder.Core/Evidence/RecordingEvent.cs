using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingEvidenceRecorder.Core.Evidence;

public abstract record RecordingEvent
{
    public required string EventId { get; init; }
    public abstract string Type { get; }
    /// <summary>Canonical final playback position in integer milliseconds, never event identity.</summary>
    public required long TimestampMs { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>Human-triggered evidence at the saved frame's normalized timestamp.</summary>
public sealed record ScreenshotEvent : RecordingEvent
{
    public override string Type => "screenshot";
    public required string Asset { get; init; }
}

/// <summary>Preserved in journal order but skipped by screenshot consumers.</summary>
public sealed record UnknownRecordingEvent : RecordingEvent
{
    [JsonIgnore] public string EventType { get; init; } = "";
    public override string Type => EventType;
}
