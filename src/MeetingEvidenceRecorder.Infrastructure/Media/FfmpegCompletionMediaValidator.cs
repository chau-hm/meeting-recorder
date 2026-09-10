using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Core.Recording;
using MeetingEvidenceRecorder.Infrastructure.Persistence;

namespace MeetingEvidenceRecorder.Infrastructure.Media;

public sealed class FfmpegCompletionMediaValidator : ICompletionMediaValidator
{
    // H.264/AAC packetization can move reported stream boundaries by a few packets.
    private static readonly TimeSpan StreamCoverageTolerance = RecordingTimelinePolicy.FinalizationTailTolerance;
    private readonly IMediaProbe probe;

    public FfmpegCompletionMediaValidator(IMediaProbe probe)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public IReadOnlyList<BundleDiagnostic> Validate(string recordingPath, SessionManifest candidate)
    {
        var diagnostics = new List<BundleDiagnostic>();
        if (!File.Exists(recordingPath) || new FileInfo(recordingPath).Length == 0)
        {
            diagnostics.Add(new("BUNDLE_RECORDING_UNREADABLE", "The finalized recording is missing or empty."));
            return diagnostics;
        }

        MediaProbeResult result;
        try
        {
            result = probe.ProbeAsync(recordingPath, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (MediaProbeException ex)
        {
            diagnostics.Add(new("BUNDLE_RECORDING_UNREADABLE", ex.Message));
            return diagnostics;
        }

        if (!result.HasVideo)
            diagnostics.Add(new("BUNDLE_VIDEO_STREAM_MISSING", "The finalized media has no video stream."));
        if (candidate.Recording.Audio.SystemAudio == true && !result.HasAudio)
            diagnostics.Add(new("BUNDLE_AUDIO_STREAM_MISSING", "System audio was required but the finalized media has no audio stream."));
        if (result.HasVideo && !string.Equals(result.VideoCodecName, "h264", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new("BUNDLE_VIDEO_CODEC_INVALID", "The finalized media video stream is not H.264."));
        if (result.HasAudio && candidate.Recording.Audio.SystemAudio == true &&
            !string.Equals(result.AudioCodecName, "aac", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new("BUNDLE_AUDIO_CODEC_INVALID", "The finalized media audio stream is not AAC."));
        if (result.Duration <= TimeSpan.Zero)
            diagnostics.Add(new("BUNDLE_DURATION_INVALID", "The finalized media duration must be positive."));

        var expectedDuration = candidate.DurationMs is long durationMs
            ? TimeSpan.FromMilliseconds(durationMs)
            : TimeSpan.Zero;
        if (expectedDuration > TimeSpan.Zero && Math.Abs((result.Duration - expectedDuration).TotalMilliseconds) > 1500)
        {
            diagnostics.Add(new(
                "BUNDLE_MEDIA_DURATION_MISMATCH",
                $"Manifest duration {expectedDuration.TotalMilliseconds:0} ms differs from media duration {result.Duration.TotalMilliseconds:0} ms."));
        }

        if (expectedDuration > TimeSpan.Zero)
        {
            AddCoverageDiagnostic(
                diagnostics,
                "VIDEO",
                result.HasVideo,
                result.VideoStartTime,
                result.VideoDuration,
                result.VideoEndTime,
                expectedDuration);
            if (candidate.Recording.Audio.SystemAudio == true)
            {
                AddCoverageDiagnostic(
                    diagnostics,
                    "AUDIO",
                    result.HasAudio,
                    result.AudioStartTime,
                    result.AudioDuration,
                    result.AudioEndTime,
                    expectedDuration);
            }
        }

        var expectedVideo = candidate.Recording.Video;
        if (result.VideoWidth is int width && expectedVideo.Width is int expectedWidth && width != expectedWidth)
            diagnostics.Add(new("BUNDLE_VIDEO_METADATA_INCONSISTENT", "Final video width does not match session metadata."));
        if (result.VideoHeight is int height && expectedVideo.Height is int expectedHeight && height != expectedHeight)
            diagnostics.Add(new("BUNDLE_VIDEO_METADATA_INCONSISTENT", "Final video height does not match session metadata."));
        if (result.VideoFramesPerSecond is double actualFps && expectedVideo.Fps is double expectedFps &&
            Math.Abs(actualFps - expectedFps) > 0.5)
            diagnostics.Add(new("BUNDLE_VIDEO_METADATA_INCONSISTENT", "Final video frame rate does not match session metadata."));
        if (result.AudioSampleRate is int actualRate && candidate.Recording.Audio.SampleRate is int expectedRate &&
            actualRate != expectedRate)
            diagnostics.Add(new("BUNDLE_AUDIO_METADATA_INCONSISTENT", "Final audio sample rate does not match session metadata."));

        return diagnostics;
    }

    private static void AddCoverageDiagnostic(
        ICollection<BundleDiagnostic> diagnostics,
        string streamName,
        bool hasStream,
        TimeSpan? start,
        TimeSpan? duration,
        TimeSpan? end,
        TimeSpan expectedDuration)
    {
        if (!hasStream)
            return;

        if (start is not TimeSpan startTime ||
            duration is not TimeSpan streamDuration ||
            end is not TimeSpan endTime ||
            streamDuration <= TimeSpan.Zero)
        {
            diagnostics.Add(new(
                $"BUNDLE_{streamName}_COVERAGE_INVALID",
                $"The finalized {streamName.ToLowerInvariant()} stream does not expose usable per-stream timing."));
            return;
        }

        if (startTime > StreamCoverageTolerance)
        {
            diagnostics.Add(new(
                $"BUNDLE_{streamName}_COVERAGE_INVALID",
                $"The finalized {streamName.ToLowerInvariant()} stream starts at {startTime.TotalMilliseconds:0} ms, after the canonical recording start."));
        }

        if (endTime + StreamCoverageTolerance < expectedDuration)
        {
            diagnostics.Add(new(
                $"BUNDLE_{streamName}_COVERAGE_INVALID",
                $"The finalized {streamName.ToLowerInvariant()} stream ends at {endTime.TotalMilliseconds:0} ms, before the canonical recording end of {expectedDuration.TotalMilliseconds:0} ms."));
        }
    }
}
