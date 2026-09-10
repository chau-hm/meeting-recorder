namespace MeetingEvidenceRecorder.Core.Abstractions;

public enum PermissionStatus
{
    Granted,
    Denied,
    NotDetermined,
    Unsupported,
    Unavailable
}

public enum RecorderErrorSeverity
{
    Warning,
    Recoverable,
    Fatal
}

public sealed record RecorderError(
    string Code,
    RecorderErrorSeverity Severity,
    string UserMessage,
    string DiagnosticMessage);

public sealed class RecorderException(RecorderError error) : Exception(error.UserMessage)
{
    public RecorderError Error { get; } = error;
}

public sealed class RecorderErrorEventArgs(RecorderError error) : EventArgs
{
    public RecorderError Error { get; } = error;
}
