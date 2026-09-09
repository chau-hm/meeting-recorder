using System.Text;
using System.Text.Json;
using MeetingEvidenceRecorder.Core.Evidence;

namespace MeetingEvidenceRecorder.Infrastructure.Persistence;

public enum BundleReadMode { Strict, InspectIncomplete }

/// <summary>Success covers levels 1/2 only. It never certifies decodable media.</summary>
public sealed record BundleValidationResult(SessionManifest? Session, IReadOnlyList<RecordingEvent> Events,
    IReadOnlyList<BundleDiagnostic> Diagnostics)
{
    public bool IsStructurallyValid => Session is not null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public bool MediaValidated => false;
}

public sealed class BundleValidator
{
    public BundleValidationResult Read(string root, BundleReadMode mode = BundleReadMode.Strict)
    {
        var diagnostics = new List<BundleDiagnostic>();
        SessionManifest? session = null;
        IReadOnlyList<RecordingEvent> events = [];
        try
        {
            var resolver = new BundlePathResolver(root);
            var sessionPath = resolver.Resolve("session.json");
            if (!File.Exists(sessionPath)) return new(null, [], [new("BUNDLE_SESSION_MISSING", "session.json is missing.")]);
            byte[] bytes = File.ReadAllBytes(sessionPath);
            // Major compatibility precedes interpretation of the remainder of the manifest.
            using (var document = JsonDocument.Parse(bytes))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Session must be an object.");
                if (!document.RootElement.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.String ||
                    !ContractValidation.SupportsSchema(version.GetString()))
                    return new(null, [], [new("BUNDLE_SCHEMA_UNSUPPORTED", "Expected schema major 1 in MAJOR.MINOR format.")]);
            }
            session = BundleJson.ReadSession(bytes);
            diagnostics.AddRange(ContractValidation.Validate(session));
            if (session.Status != SessionStatus.Completed)
                diagnostics.Add(new("BUNDLE_STATUS_NOT_COMPLETED", "Input is not a completed bundle.",
                    mode == BundleReadMode.Strict ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning));

            void Reference(string? reference, string code, bool required)
            {
                if (reference is null) return; // Required missing fields already diagnosed by the contract validator.
                try
                {
                    var path = resolver.Resolve(reference);
                    if (required && !File.Exists(path)) diagnostics.Add(new(code, "Referenced file is missing.", Path: reference));
                }
                catch (BundlePathException ex) { diagnostics.Add(new("BUNDLE_PATH_INVALID", ex.Message, Path: reference)); }
            }
            Reference(session.Recording?.File, "BUNDLE_RECORDING_MISSING", session.Status == SessionStatus.Completed);
            if (session.EventsFile is not null)
            {
                var journalPath = resolver.Resolve(session.EventsFile);
                if (!File.Exists(journalPath)) diagnostics.Add(new("BUNDLE_EVENTS_MISSING", "Referenced journal is missing.", Path: session.EventsFile));
                else
                {
                    try
                    {
                        var journal = EventJournal.Read(journalPath);
                        events = journal.Events;
                        diagnostics.AddRange(journal.Diagnostics);
                    }
                    catch (DecoderFallbackException ex) { diagnostics.Add(new("BUNDLE_EVENTS_INVALID", ex.Message, Path: session.EventsFile)); }
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    long previous = -1;
                    foreach (var entry in events)
                    {
                        diagnostics.AddRange(ContractValidation.Validate(entry, session.Status == SessionStatus.Completed ? session.DurationMs : null));
                        if (!ids.Add(entry.EventId)) diagnostics.Add(new("BUNDLE_EVENT_ID_DUPLICATE", "Event IDs must be unique within the session."));
                        if (entry.TimestampMs < previous)
                            diagnostics.Add(new("BUNDLE_EVENT_OUT_OF_ORDER", "Journal order preserved despite decreasing timestamp.", DiagnosticSeverity.Warning));
                        previous = entry.TimestampMs;
                        if (entry is ScreenshotEvent shot)
                        {
                            Reference(shot.Asset, "BUNDLE_ASSET_MISSING", true);
                            if (assets.TryGetValue(shot.Asset, out var prior) && prior != shot.Asset)
                                diagnostics.Add(new("BUNDLE_PATH_INVALID", "Screenshot paths differ only by case.", Path: shot.Asset));
                            assets[shot.Asset] = shot.Asset;
                        }
                    }
                }
            }
            if (session.Status == SessionStatus.Completed)
                diagnostics.Add(new("BUNDLE_MEDIA_NOT_VALIDATED", "Levels 1/2 only: media readability, tracks and actual duration have not been probed.", DiagnosticSeverity.Warning));
        }
        catch (BundlePathException ex) { diagnostics.Add(new("BUNDLE_PATH_INVALID", ex.Message)); }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { diagnostics.Add(new("BUNDLE_SESSION_INVALID_JSON", ex.Message)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { diagnostics.Add(new("BUNDLE_IO_ERROR", ex.Message)); }
        return new(session, events, diagnostics);
    }
}
