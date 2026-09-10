using MeetingEvidenceRecorder.Core.Capture;
using MeetingEvidenceRecorder.Core.Recording;

namespace MeetingEvidenceRecorder.Core.Abstractions;

public interface IDisplaySystemAudioCaptureBackend : IAsyncDisposable
{
    event EventHandler<RecorderErrorEventArgs>? Error;

    long DroppedVideoFrames { get; }
    NativeTimestamp? SourceTimestampOrigin { get; }

    Task<PermissionStatus> GetScreenCaptureStatusAsync(CancellationToken cancellationToken);

    Task<PermissionStatus> RequestScreenCaptureAccessAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(CancellationToken cancellationToken);

    Task StartAsync(
        CaptureSource source,
        VideoCaptureOptions options,
        CancellationToken cancellationToken);

    IAsyncEnumerable<VideoFrame> ReadFramesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<AudioFrame> ReadSystemAudioAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
