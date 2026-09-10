using MeetingEvidenceRecorder.Core.Recording;

namespace MeetingEvidenceRecorder.Core.Capture;

public enum CaptureSourceKind
{
    Display
}

public sealed record CaptureSource(
    string Id,
    string Name,
    CaptureSourceKind Kind,
    int Width,
    int Height,
    string? StableDisplayId = null);

public sealed record VideoCaptureOptions(
    double FramesPerSecond = 30,
    bool ShowCursor = true);

public sealed record VideoFormat(
    int Width,
    int Height,
    double FramesPerSecond,
    string PixelFormat = "bgra");

public enum AudioSampleFormat
{
    Float32Interleaved
}

public sealed record AudioFormat(
    int SampleRate,
    int Channels,
    AudioSampleFormat SampleFormat = AudioSampleFormat.Float32Interleaved);

public enum AudioSourceKind
{
    System
}

public sealed record VideoFrame(
    long Sequence,
    NativeTimestamp SourceTimestamp,
    ReadOnlyMemory<byte> Data,
    VideoFormat Format);

public sealed record AudioFrame(
    AudioSourceKind Source,
    NativeTimestamp SourceTimestamp,
    int SampleCount,
    ReadOnlyMemory<float> Samples,
    AudioFormat Format);

public sealed record TimedVideoFrame(VideoFrame Frame, TimeSpan RecordingTimestamp);

public sealed record TimedAudioFrame(AudioFrame Frame, TimeSpan RecordingTimestamp);
