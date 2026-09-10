using MeetingEvidenceRecorder.Core.Abstractions;
using MeetingEvidenceRecorder.Core.Capture;

namespace MeetingEvidenceRecorder.Application.Recording;

public sealed record RecordingSessionOptions(
    string OutputDirectory,
    CaptureSource Source,
    VideoCaptureOptions VideoOptions,
    string ApplicationVersion = "0.2.0");

public sealed record RecordingCompletion(
    string BundlePath,
    RecordingState State,
    TimeSpan? Duration,
    IReadOnlyList<string> Diagnostics,
    RecorderError? Error = null);
