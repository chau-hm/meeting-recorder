namespace MeetingEvidenceRecorder.Application.Recording;

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Stopping,
    Completed,
    Incomplete,
    Failed
}
