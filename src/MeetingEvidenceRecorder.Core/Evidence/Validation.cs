using System.Text.RegularExpressions;

namespace MeetingEvidenceRecorder.Core.Evidence;

public enum DiagnosticSeverity { Warning, Error }
public sealed record BundleDiagnostic(string Code, string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error, string? Path = null, int? Line = null);

public static class ContractValidation
{
    public static bool SupportsSchema(string? version) => version is not null &&
        Regex.IsMatch(version, @"\A1\.[0-9]+\z", RegexOptions.CultureInvariant);

    public static IReadOnlyList<BundleDiagnostic> Validate(SessionManifest manifest)
    {
        var errors = new List<BundleDiagnostic>();
        void Error(string code, string message) => errors.Add(new(code, message));
        if (!SupportsSchema(manifest.SchemaVersion)) Error("BUNDLE_SCHEMA_UNSUPPORTED", "Expected schema major 1 in MAJOR.MINOR format.");
        if (string.IsNullOrWhiteSpace(manifest.SessionId)) Error("BUNDLE_METADATA_INVALID", "session_id must be nonempty.");
        if (!Enum.IsDefined(manifest.Status)) Error("BUNDLE_STATUS_INVALID", "Unknown session status.");
        if (manifest.DurationMs < 0 || (manifest.Status == SessionStatus.Completed && manifest.DurationMs is not > 0))
            Error("BUNDLE_DURATION_INVALID", "Completed duration must be positive; active duration may be null.");
        if (manifest.Application is null || manifest.Platform is null || manifest.Recording is null ||
            manifest.Recording.Video is null || manifest.Recording.Audio is null)
        {
            Error("BUNDLE_METADATA_INVALID", "Required metadata objects must be present and non-null.");
            return errors;
        }
        if (manifest.Status != SessionStatus.Initializing && string.IsNullOrWhiteSpace(manifest.EventsFile))
            Error("BUNDLE_EVENTS_MISSING", "events_file is required after initialization.");
        if (manifest.Status == SessionStatus.Completed && string.IsNullOrWhiteSpace(manifest.Recording.File))
            Error("BUNDLE_RECORDING_MISSING", "Completed recording.file is required.");
        var video = manifest.Recording.Video;
        if (video.Width <= 0 || video.Height <= 0 || video.Fps <= 0 || (video.Fps is double fps && !double.IsFinite(fps)))
            Error("BUNDLE_METADATA_INVALID", "Video dimensions and fps, when supplied, must be positive.");
        var audio = manifest.Recording.Audio;
        if (audio.SampleRate <= 0)
            Error("BUNDLE_AUDIO_METADATA_INCONSISTENT", "Audio sample_rate must be positive or null.");
        if (audio.Microphone == false && audio.MicrophoneDevice is not null)
            Error("BUNDLE_AUDIO_METADATA_INCONSISTENT", "microphone_device must be null when microphone is false.");
        bool inconsistent = audio.OutputMode switch
        {
            "mixed" => audio.SystemAudio == false || audio.Microphone == false,
            "system_audio_only" => audio.SystemAudio == false || audio.Microphone == true,
            "microphone_only" => audio.SystemAudio == true || audio.Microphone == false,
            "none" => audio.SystemAudio == true || audio.Microphone == true || audio.SampleRate is not null,
            _ => false // Future output modes do not change the schema major.
        };
        if (audio.SystemAudio == false && audio.Microphone == false && audio.OutputMode != "none") inconsistent = true;
        if (inconsistent) Error("BUNDLE_AUDIO_METADATA_INCONSISTENT", "Audio flags, output_mode and sample_rate contradict one another.");
        if (audio.SynchronizedToRecordingTimeline == false)
            errors.Add(new("BUNDLE_AUDIO_SYNC_WARNING", "Producer reports unsynchronized audio.", DiagnosticSeverity.Warning));
        return errors;
    }

    public static IReadOnlyList<BundleDiagnostic> Validate(RecordingEvent entry, long? completedDuration = null)
    {
        var errors = new List<BundleDiagnostic>();
        if (string.IsNullOrWhiteSpace(entry.EventId) || string.IsNullOrWhiteSpace(entry.Type))
            errors.Add(new("BUNDLE_EVENTS_INVALID", "event_id and type must be nonempty strings."));
        if (entry.TimestampMs < 0 || (completedDuration is > 0 && entry.TimestampMs > completedDuration && entry.TimestampMs - completedDuration > 1000))
            errors.Add(new("BUNDLE_TIMESTAMP_INVALID", "Timestamp must be nonnegative and within duration plus 1000 ms tolerance."));
        else if (completedDuration is > 0 && entry.TimestampMs > completedDuration)
            errors.Add(new("BUNDLE_TIMESTAMP_NEAR_DURATION_BOUNDARY", "Timestamp lies within the 1000 ms end tolerance.", DiagnosticSeverity.Warning));
        if (entry is ScreenshotEvent shot && string.IsNullOrWhiteSpace(shot.Asset))
            errors.Add(new("BUNDLE_EVENTS_INVALID", "Screenshot asset is required."));
        if (entry is UnknownRecordingEvent)
            errors.Add(new("BUNDLE_UNKNOWN_EVENT_TYPE", "Unknown event preserved and skipped for screenshot processing.", DiagnosticSeverity.Warning));
        return errors;
    }
}
