using MeetingEvidenceRecorder.Application.Recording;
using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Infrastructure.Media;
using MeetingEvidenceRecorder.Infrastructure.Persistence;

namespace MeetingEvidenceRecorder.Infrastructure.Recording;

public sealed class FileRecordingBundleStoreFactory : IRecordingBundleStoreFactory
{
    private readonly string ffprobePath;

    public FileRecordingBundleStoreFactory(string ffprobePath)
    {
        if (string.IsNullOrWhiteSpace(ffprobePath) || !Path.IsPathRooted(ffprobePath))
            throw new ArgumentException("An absolute ffprobe executable path is required.", nameof(ffprobePath));
        if (!File.Exists(ffprobePath))
            throw new FileNotFoundException("The configured ffprobe executable does not exist.", ffprobePath);

        this.ffprobePath = ffprobePath;
    }

    public IRecordingBundleStore Create(
        RecordingSessionOptions options,
        Guid sessionId,
        DateTimeOffset createdAt) =>
        new FileRecordingBundleStore(
            options.OutputDirectory,
            sessionId,
            createdAt,
            new FfmpegCompletionMediaValidator(new FfmpegMediaProbe(ffprobePath)));
}

public sealed class FileRecordingBundleStore : IRecordingBundleStore
{
    private readonly ManifestWriter manifestWriter = new();
    private readonly EventJournal journal;
    private readonly ICompletionMediaValidator completionMediaValidator;
    private bool disposed;

    public FileRecordingBundleStore(
        string outputDirectory,
        Guid sessionId,
        DateTimeOffset createdAt,
        ICompletionMediaValidator completionMediaValidator)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        this.completionMediaValidator = completionMediaValidator ??
            throw new ArgumentNullException(nameof(completionMediaValidator));

        var baseDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(baseDirectory);
        Root = CreateUniqueRoot(baseDirectory, createdAt);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, ".work"));
        Directory.CreateDirectory(Path.Combine(Root, "screenshots"));
        VerifyWritable(Root);
        journal = new EventJournal(Root, "events.jsonl");
        SessionId = sessionId;
        CreatedAt = createdAt;
    }

    public string Root { get; }
    public Guid SessionId { get; }
    public DateTimeOffset CreatedAt { get; }

    public void WriteActive(SessionManifest manifest)
    {
        EnsureNotDisposed();
        manifestWriter.WriteActive(Root, manifest);
    }

    public void AppendEvent(RecordingEvent entry)
    {
        EnsureNotDisposed();
        journal.Append(entry);
    }

    public IReadOnlyList<BundleDiagnostic> CommitCompleted(SessionManifest finalizingManifest)
    {
        EnsureNotDisposed();
        return manifestWriter.CommitCompleted(Root, finalizingManifest, completionMediaValidator);
    }

    public void MarkIncomplete(SessionManifest manifest)
    {
        EnsureNotDisposed();
        var candidate = manifest.Status is SessionStatus.Incomplete or SessionStatus.Failed
            ? manifest
            : manifest with { Status = SessionStatus.Incomplete };
        manifestWriter.WriteActive(Root, candidate);
    }

    private static string CreateUniqueRoot(string baseDirectory, DateTimeOffset createdAt)
    {
        var stem = $"meeting-{createdAt:yyyyMMdd-HHmmss}";
        var root = Path.Combine(baseDirectory, stem);
        for (var suffix = 1; Directory.Exists(root); suffix++)
            root = Path.Combine(baseDirectory, $"{stem}-{suffix:D2}");
        return root;
    }

    private static void VerifyWritable(string root)
    {
        var probe = Path.Combine(root, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probe))
                File.Delete(probe);
        }
    }

    private void EnsureNotDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(FileRecordingBundleStore));
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            journal.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
