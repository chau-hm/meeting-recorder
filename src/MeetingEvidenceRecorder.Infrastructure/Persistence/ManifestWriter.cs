using MeetingEvidenceRecorder.Core.Evidence;

namespace MeetingEvidenceRecorder.Infrastructure.Persistence;

/// <summary>
/// Supplied by a future media implementation. Must probe finalized container, readable video,
/// actual duration/timestamps and audio tracks against the candidate metadata. Never infer success from file existence.
/// </summary>
public interface ICompletionMediaValidator
{
    IReadOnlyList<BundleDiagnostic> Validate(string recordingPath, SessionManifest candidate);
}

public sealed class ManifestWriter
{
    public void WriteActive(string root, SessionManifest manifest)
    {
        if (manifest.Status == SessionStatus.Completed)
            throw new InvalidOperationException("Completed requires CommitCompleted and a media validator.");
        ValidateManifest(root, manifest);
        WriteAtomic(root, manifest);
    }

    /// <summary>Caller must have stopped all artifact writers and hold exclusive ownership of the bundle.</summary>
    public IReadOnlyList<BundleDiagnostic> CommitCompleted(string root, SessionManifest finalizing,
        ICompletionMediaValidator mediaValidator)
    {
        ArgumentNullException.ThrowIfNull(mediaValidator);
        if (finalizing.Status != SessionStatus.Finalizing)
            throw new ArgumentException("Completion candidate must be finalizing.", nameof(finalizing));
        var candidate = finalizing with { Status = SessionStatus.Completed };
        var diagnostics = ContractValidation.Validate(candidate).ToList();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return diagnostics;
        ValidateManifest(root, candidate);
        // Persist finalizing first: no failure below may advertise completed.
        WriteAtomic(root, finalizing);
        var inspected = new BundleValidator().Read(root, BundleReadMode.InspectIncomplete);
        diagnostics.AddRange(inspected.Diagnostics.Where(d => d.Code != "BUNDLE_STATUS_NOT_COMPLETED"));
        foreach (var entry in inspected.Events) diagnostics.AddRange(ContractValidation.Validate(entry, candidate.DurationMs));
        var recording = new BundlePathResolver(root).Resolve(candidate.Recording.File!);
        if (!File.Exists(recording)) diagnostics.Add(new("BUNDLE_RECORDING_MISSING", "Final recording is missing."));
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return diagnostics;
        diagnostics.AddRange(mediaValidator.Validate(recording, candidate));
        if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) WriteAtomic(root, candidate);
        return diagnostics;
    }

    private static void ValidateManifest(string root, SessionManifest manifest)
    {
        var errors = ContractValidation.Validate(manifest).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) throw new InvalidDataException(string.Join("; ", errors.Select(e => e.Message)));
        var resolver = new BundlePathResolver(root);
        resolver.Resolve("session.json");
        if (manifest.Recording.File is not null) resolver.Resolve(manifest.Recording.File);
        if (manifest.EventsFile is not null) resolver.Resolve(manifest.EventsFile);
    }

    private static void WriteAtomic(string root, SessionManifest manifest)
    {
        var resolver = new BundlePathResolver(root);
        var finalPath = resolver.Resolve("session.json");
        var bytes = BundleJson.SerializeSession(manifest);
        BundleJson.ReadSession(bytes); // Validate the actual wire object before publication.
        var temporary = resolver.Resolve($"session-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, finalPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
