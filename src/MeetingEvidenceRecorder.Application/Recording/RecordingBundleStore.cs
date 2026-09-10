using MeetingEvidenceRecorder.Core.Evidence;

namespace MeetingEvidenceRecorder.Application.Recording;

public interface IRecordingBundleStore : IAsyncDisposable
{
    string Root { get; }

    void WriteActive(SessionManifest manifest);

    void AppendEvent(RecordingEvent entry);

    IReadOnlyList<BundleDiagnostic> CommitCompleted(SessionManifest finalizingManifest);

    void MarkIncomplete(SessionManifest manifest);
}

public interface IRecordingBundleStoreFactory
{
    IRecordingBundleStore Create(
        RecordingSessionOptions options,
        Guid sessionId,
        DateTimeOffset createdAt);
}
