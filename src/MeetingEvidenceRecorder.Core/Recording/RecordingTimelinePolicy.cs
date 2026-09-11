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
    /// A small amount of native callback reordering is acceptable. Larger rewinds are source
    /// timeline corruption and must be rejected before media transport sees the sample.
    /// </summary>
    public static readonly TimeSpan SourceTimestampReorderTolerance = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Active synthetic video intentionally trails the canonical clock so a later real frame has
    /// a bounded opportunity to arrive at its canonical position before that slot is committed.
    /// </summary>
    public static readonly TimeSpan ActiveVideoWatermarkHoldback = TimeSpan.FromSeconds(1);

    /// <summary>Cadence for advancing active static-video coverage from the recording clock.</summary>
    public static readonly TimeSpan ActiveVideoWatermarkInterval = TimeSpan.FromMilliseconds(50);

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
    /// Defense-in-depth bound for active audio silence insertion. Source timestamp integrity is
    /// decided by the application timeline guard; this only prevents an accidental audio gap
    /// from creating an unbounded silence write inside the transport.
    /// </summary>
    public static readonly TimeSpan MaximumActiveAudioFillGap = TimeSpan.FromSeconds(10);

}
