using MeetingEvidenceRecorder.Core.Capture;

namespace MeetingEvidenceRecorder.Core.Abstractions;

public sealed record MediaWriterConfiguration(
    string WorkDirectory,
    string WorkMediaPath,
    string FinalMediaPath,
    VideoFormat VideoFormat,
    AudioFormat AudioFormat);

public sealed record FinalizedMedia(
    string Path,
    TimeSpan Duration,
    VideoFormat VideoFormat,
    AudioFormat? AudioFormat,
    bool HasVideo,
    bool HasAudio);

public interface IMediaWriter : IAsyncDisposable
{
    Task InitializeAsync(MediaWriterConfiguration configuration, CancellationToken cancellationToken);

    ValueTask WriteVideoAsync(TimedVideoFrame frame, CancellationToken cancellationToken);

    ValueTask WriteAudioAsync(TimedAudioFrame frame, CancellationToken cancellationToken);

    ValueTask BeginFinalizationAsync(CancellationToken cancellationToken);

    Task CompleteAudioTransportAsync(
        TimeSpan recordingEnd,
        CancellationToken cancellationToken);

    Task<FinalizedMedia> FinalizeAsync(
        TimeSpan recordingEnd,
        CancellationToken cancellationToken);
}
