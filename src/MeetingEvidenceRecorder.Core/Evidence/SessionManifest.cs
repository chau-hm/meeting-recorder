using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingEvidenceRecorder.Core.Evidence;

public enum SessionStatus { Initializing, Recording, Paused, Finalizing, Completed, Incomplete, Failed }

/// <summary>Wire contract, not a recording lifecycle implementation.</summary>
public sealed record SessionManifest
{
    public required string SchemaVersion { get; init; }
    public required string SessionId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Final playback duration; excludes paused wall-clock time.</summary>
    public required long? DurationMs { get; init; }
    public required SessionStatus Status { get; init; }
    public required RecordingMetadata Recording { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventsFile { get; init; }
    public required ApplicationMetadata Application { get; init; }
    public required PlatformMetadata Platform { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record RecordingMetadata
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; init; }
    public required VideoMetadata Video { get; init; }
    public required AudioMetadata Audio { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

// Recommended metadata remains optional; when present it must be truthful and correctly typed.
public sealed record VideoMetadata
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Width { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Height { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Fps { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record AudioMetadata
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SystemAudio { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Microphone { get; init; }
    public string? MicrophoneDevice { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OutputMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? SampleRate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SystemAudioCaptureMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SynchronizedToRecordingTimeline { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record ApplicationMetadata
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Version { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record PlatformMetadata
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Os { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Architecture { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
