namespace MeetingEvidenceRecorder.Core.Recording;

/// <summary>
/// Shared bounds for converting native capture timing into the canonical recording timeline.
/// </summary>
public static class RecordingTimelinePolicy
{
    /// <summary>
    /// The current macOS capture path uses 30 fps video and bounded callback queues. One second
    /// covers a frame cadence, native audio-buffer coverage, and bounded callback scheduling
    /// while still rejecting multi-second source timestamp discontinuities.
    /// </summary>
    public static readonly TimeSpan SourceTimestampLeadTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Only a small codec/buffer tail may be synthesized when finalizing audio to the canonical
    /// recording end. Larger missing intervals remain visible to completion coverage validation.
    /// </summary>
    public static readonly TimeSpan FinalizationTailTolerance = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Small audio lead used to keep FFmpeg's dual raw-input demuxers flowing while the final
    /// CFR video frame is written. The remux step caps the published media at recording end.
    /// </summary>
    public static readonly TimeSpan FinalizationAudioLead = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Defense-in-depth bound for active writer gap padding. Finalization is allowed to extend
    /// the last frame to the canonical end independently of this runtime-gap limit.
    /// </summary>
    public static readonly TimeSpan MaximumRuntimeWriterGap = TimeSpan.FromSeconds(10);

}
