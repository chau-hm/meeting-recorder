using System.Text.Json;
using MeetingEvidenceRecorder.Core.Evidence;

namespace MeetingEvidenceRecorder.Infrastructure.Persistence;

public sealed record JournalReadResult(IReadOnlyList<RecordingEvent> Events, IReadOnlyList<BundleDiagnostic> Diagnostics);

/// <summary>Single writer. Every successful append is flushed to disk; previous bytes are never rewritten.</summary>
public sealed class EventJournal : IDisposable
{
    private readonly FileStream stream;
    private readonly BundlePathResolver resolver;
    private bool faulted;
    private readonly object gate = new();

    public EventJournal(string bundleRoot, string relativePath)
    {
        resolver = new BundlePathResolver(bundleRoot);
        var path = resolver.Resolve(relativePath);
        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n') throw new InvalidDataException("Refusing to append after an interrupted final record.");
            }
            stream.Position = stream.Length;
        }
        catch { stream.Dispose(); throw; }
    }

    public void Append(RecordingEvent entry)
    {
        var errors = ContractValidation.Validate(entry);
        if (errors.Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException(errors[0].Message);
        if (entry is ScreenshotEvent shot && !File.Exists(resolver.Resolve(shot.Asset)))
            throw new InvalidDataException("Screenshot asset must exist before its event is appended.");
        var bytes = BundleJson.SerializeEvent(entry);
        // Validate the exact serialized envelope, including extension-data collisions, before touching the file.
        BundleJson.ReadEvent(BundleJson.Utf8.GetString(bytes));
        lock (gate)
        {
            if (faulted) throw new InvalidOperationException("Journal is faulted; inspect/recover before reopening.");
            try
            {
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            catch { faulted = true; throw; }
        }
    }

    public void Dispose() { lock (gate) stream.Dispose(); }

    public static JournalReadResult Read(string path)
    {
        var events = new List<RecordingEvent>();
        var diagnostics = new List<BundleDiagnostic>();
        // Strict decoder: invalid UTF-8 cannot silently become replacement characters.
        var text = BundleJson.Utf8.GetString(File.ReadAllBytes(path));
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index == lines.Length - 1 && lines[index].Length == 0) break;
            if (index == lines.Length - 1)
            {
                diagnostics.Add(new("BUNDLE_EVENTS_INTERRUPTED", "Final record lacks its newline commit delimiter.", Path: path, Line: index + 1));
                break;
            }
            try { events.Add(BundleJson.ReadEvent(lines[index].TrimEnd('\r'))); }
            catch (JsonException ex) { diagnostics.Add(new("BUNDLE_EVENTS_INVALID", ex.Message, Path: path, Line: index + 1)); }
        }
        return new(events, diagnostics);
    }
}
