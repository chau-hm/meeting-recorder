using System.Text.Json;
using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Infrastructure.Persistence;

namespace MeetingEvidenceRecorder.Infrastructure.Tests;

public class PersistenceTests
{
    [Fact]
    public void MissingAssetCannotProduceSuccessfulJournalRecord()
    {
        using var bundle = new SyntheticBundle();
        var path = Path.Combine(bundle.Root, "events.jsonl");
        using var journal = new EventJournal(bundle.Root, "events.jsonl");
        Assert.Throws<InvalidDataException>(() => journal.Append(new ScreenshotEvent
            { EventId = "missing", TimestampMs = 0, Asset = "screenshots/missing.png" }));
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void MalformedMiddleRecordRemainsAnErrorAndDoesNotReorderLaterEvents()
    {
        using var bundle = new SyntheticBundle(1);
        var path = Path.Combine(bundle.Root, "events.jsonl");
        File.AppendAllText(path, "{bad}\n{\"event_id\":\"last\",\"type\":\"bookmark\",\"timestamp_ms\":0}\n", BundleJson.Utf8);
        var before = File.ReadAllBytes(path);
        var result = FakeConsumer.Open(bundle.Root, true);
        Assert.False(result.IsStructurallyValid);
        Assert.Equal(new[] { "evt-0000", "last" }, result.Events.Select(e => e.EventId));
        Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_EVENTS_INVALID" && d.Line == 2);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void FailedManifestSerializationPreservesPreviousFile()
    {
        using var bundle = new SyntheticBundle();
        var path = Path.Combine(bundle.Root, "session.json");
        var before = File.ReadAllBytes(path);
        var invalid = bundle.Manifest with { Extensions = new() { ["status"] = JsonSerializer.SerializeToElement("completed") } };
        Assert.Throws<JsonException>(() => new ManifestWriter().WriteActive(bundle.Root, invalid));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(bundle.Root, "*.tmp"));
    }
    [Theory]
    [InlineData(SessionStatus.Initializing)] [InlineData(SessionStatus.Recording)] [InlineData(SessionStatus.Paused)]
    [InlineData(SessionStatus.Finalizing)] [InlineData(SessionStatus.Completed)] [InlineData(SessionStatus.Incomplete)] [InlineData(SessionStatus.Failed)]
    public void ManifestRoundTripPreservesIdentityOffsetDurationAndWireStatus(SessionStatus status)
    {
        using var bundle = new SyntheticBundle();
        var input = bundle.Manifest with { Status = status };
        var bytes = BundleJson.SerializeSession(input);
        var output = BundleJson.ReadSession(bytes);
        Assert.Equal(input.SessionId, output.SessionId);
        Assert.Equal(input.CreatedAt, output.CreatedAt);
        Assert.Equal(input.CreatedAt.Offset, output.CreatedAt.Offset);
        Assert.Equal(input.DurationMs, output.DurationMs);
        Assert.Equal(input.Status, output.Status);
        Assert.Contains($"\"status\":\"{status.ToString().ToLowerInvariant()}\"", BundleJson.Utf8.GetString(bytes));
        Assert.Equal(bytes, BundleJson.SerializeSession(output));
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }));
    }

    [Theory]
    [InlineData(0L)] [InlineData(1L)] [InlineData(999L)] [InlineData(1000L)]
    [InlineData(60000L)] [InlineData(3600000L)] [InlineData(7200000L)] [InlineData(long.MaxValue)]
    public void SavedFrameTimestampRoundTripsWithoutFilenameOrWallClockInference(long timestamp)
    {
        var shot = new ScreenshotEvent { EventId = "evt", TimestampMs = timestamp, Asset = "screenshots/shot_00-00-00.000.png" };
        var output = Assert.IsType<ScreenshotEvent>(BundleJson.ReadEvent(BundleJson.Utf8.GetString(BundleJson.SerializeEvent(shot))));
        Assert.Equal(timestamp, output.TimestampMs);
        Assert.Equal(shot.Asset, output.Asset);
    }

    [Fact]
    public void DuplicateTimestampsAndDecreasingTimestampsPreserveAppendOrder()
    {
        using var bundle = new SyntheticBundle(1000, 1000, 1);
        var before = File.ReadAllBytes(Path.Combine(bundle.Root, "events.jsonl"));
        using (var writer = new EventJournal(bundle.Root, "events.jsonl"))
            writer.Append(new UnknownRecordingEvent { EventId = "future", EventType = "bookmark", TimestampMs = 0 });
        var after = File.ReadAllBytes(Path.Combine(bundle.Root, "events.jsonl"));
        Assert.Equal(before, after.Take(before.Length).ToArray());
        var result = FakeConsumer.Open(bundle.Root, inspect: true);
        Assert.True(result.IsStructurallyValid);
        Assert.Equal(new long[] { 1000, 1000, 1, 0 }, result.Events.Select(e => e.TimestampMs));
        Assert.Equal(4, result.Events.Select(e => e.EventId).Distinct().Count());
        Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_EVENT_OUT_OF_ORDER");
    }

    [Theory]
    [InlineData("{broken}\n", "BUNDLE_EVENTS_INVALID")]
    [InlineData("{\"event_id\":", "BUNDLE_EVENTS_INTERRUPTED")]
    [InlineData("{\"event_id\":\"a\",\"type\":\"question\",\"timestamp_ms\":0}", "BUNDLE_EVENTS_INTERRUPTED")]
    [InlineData("\n", "BUNDLE_EVENTS_INVALID")]
    public void MalformedOrInterruptedRecordsCannotPassValidation(string tail, string code)
    {
        using var bundle = new SyntheticBundle(1);
        File.AppendAllText(Path.Combine(bundle.Root, "events.jsonl"), tail, BundleJson.Utf8);
        var result = FakeConsumer.Open(bundle.Root, inspect: true);
        Assert.False(result.IsStructurallyValid);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Line == 2);
        Assert.Single(result.Events);
        if (!tail.EndsWith('\n')) Assert.Throws<InvalidDataException>(() => new EventJournal(bundle.Root, "events.jsonl"));
    }

    [Theory]
    [InlineData("{\"type\":\"screenshot\",\"event_id\":\"a\",\"timestamp_ms\":0}")]
    [InlineData("{\"type\":\"screenshot\",\"event_id\":\"a\",\"timestamp_ms\":0,\"asset\":null}")]
    [InlineData("{\"type\":\"future\",\"event_id\":\"a\",\"timestamp_ms\":1.5}")]
    [InlineData("{\"type\":\"future\",\"event_id\":\"a\"}")]
    [InlineData("{\"type\":\"future\",\"event_id\":\"a\",\"timestamp_ms\":0,\"timestamp_ms\":1}")]
    public void InvalidEventEnvelopeIsRejected(string json) => Assert.Throws<JsonException>(() => BundleJson.ReadEvent(json));

    [Fact]
    public void UnknownMetadataAndEventPayloadArePreserved()
    {
        using var bundle = new SyntheticBundle();
        var input = BundleJson.Utf8.GetString(BundleJson.SerializeSession(bundle.Manifest));
        var output = BundleJson.ReadSession(BundleJson.Utf8.GetBytes(input.Replace("\"schema_version\":\"1.0\"", "\"schema_version\":\"1.9\",\"future\":{\"x\":42}")));
        Assert.Equal(42, output.Extensions!["future"].GetProperty("x").GetInt32());
        var entry = BundleJson.ReadEvent("{\"event_id\":\"future\",\"type\":\"decision\",\"timestamp_ms\":0,\"note\":{\"value\":42}}");
        var reread = BundleJson.ReadEvent(BundleJson.Utf8.GetString(BundleJson.SerializeEvent(entry)));
        Assert.IsType<UnknownRecordingEvent>(reread);
        Assert.Equal(42, reread.Extensions!["note"].GetProperty("value").GetInt32());
    }

    [Fact]
    public void CopyAndRenamePreserveRelativeReferencesAndIdentity()
    {
        using var bundle = new SyntheticBundle(0, 1000);
        var before = FakeConsumer.Open(bundle.Root, inspect: true);
        string copy = bundle.Root + "-copy", renamed = bundle.Root + "-renamed";
        try
        {
            foreach (var file in Directory.GetFiles(bundle.Root, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(bundle.Root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            Directory.Move(copy, renamed);
            Directory.Delete(bundle.Root, true); // Original no longer available to accidentally resolve against.
            var after = FakeConsumer.Open(renamed, inspect: true);
            Assert.True(before.IsStructurallyValid);
            Assert.True(after.IsStructurallyValid);
            Assert.Equal(before.Session!.SessionId, after.Session!.SessionId);
            Assert.Equal(before.Events.Select(e => e.EventId), after.Events.Select(e => e.EventId));
        }
        finally { if (Directory.Exists(copy)) Directory.Delete(copy, true); if (Directory.Exists(renamed)) Directory.Delete(renamed, true); }
    }

    [Fact]
    public void MissingScreenshotAndDuplicateIdentityAreErrors()
    {
        using var bundle = new SyntheticBundle(1);
        var path = Path.Combine(bundle.Root, "events.jsonl");
        File.AppendAllText(path, File.ReadAllText(path), BundleJson.Utf8);
        File.Delete(Path.Combine(bundle.Root, "screenshots/shot-0000.png"));
        var result = FakeConsumer.Open(bundle.Root, inspect: true);
        Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_EVENT_ID_DUPLICATE");
        Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_ASSET_MISSING");
    }

    [Fact]
    public void InvalidUtf8IsNotSilentlyReplaced()
    {
        using var bundle = new SyntheticBundle();
        File.WriteAllBytes(Path.Combine(bundle.Root, "events.jsonl"), [0xff, 0x0a]);
        Assert.Contains(FakeConsumer.Open(bundle.Root, true).Diagnostics, d => d.Code == "BUNDLE_EVENTS_INVALID");
    }
}
