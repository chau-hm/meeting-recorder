using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Infrastructure.Persistence;

namespace MeetingEvidenceRecorder.Infrastructure.Tests;

/// <summary>Contract fixture producer only: never claims synthetic bytes are completed media.</summary>
internal sealed class SyntheticBundle : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "evidence-contract-" + Guid.NewGuid().ToString("N"));
    public SessionManifest Manifest { get; }
    public SyntheticBundle(params long[] timestamps)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "screenshots"));
        Manifest = new()
        {
            SchemaVersion = "1.0", SessionId = "7e177f69-1d91-43ef-b554-d9529148f121",
            CreatedAt = new DateTimeOffset(2026, 9, 9, 10, 30, 0, TimeSpan.FromHours(8)),
            DurationMs = 7200000, Status = SessionStatus.Finalizing, EventsFile = "events.jsonl",
            Recording = new() { File = "recording.mp4", Video = new(), Audio = new() },
            Application = new() { Name = "Phase 0 synthetic contract harness", Version = "0.1.0" },
            Platform = new() { Os = "fixture", Architecture = "portable" }
        };
        File.WriteAllText(Path.Combine(Root, "recording.mp4"), "SYNTHETIC CONTRACT PLACEHOLDER; NOT PLAYABLE MEDIA\n", BundleJson.Utf8);
        using (var journal = new EventJournal(Root, "events.jsonl"))
        {
            for (var i = 0; i < timestamps.Length; i++)
            {
                string asset = $"screenshots/shot-{i:D4}.png";
                File.WriteAllBytes(Path.Combine(Root, asset), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII="));
                journal.Append(new ScreenshotEvent { EventId = $"evt-{i:D4}", TimestampMs = timestamps[i], Asset = asset });
            }
        }
        new ManifestWriter().WriteActive(Root, Manifest);
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}

// Consumer entry point depends only on persisted contract files, not producer state.
internal static class FakeConsumer
{
    public static BundleValidationResult Open(string root, bool inspect = false) =>
        new BundleValidator().Read(root, inspect ? BundleReadMode.InspectIncomplete : BundleReadMode.Strict);
}
