using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Infrastructure.Persistence;

namespace MeetingEvidenceRecorder.Infrastructure.Tests;

public class SchemaFixtureTests
{
    static SchemaFixtureTests()
    {
        // JsonSchema.Net 7.x's built-in date-time checks lexical shape only. Add calendar validation.
        Formats.Register(new PredicateFormat("date-time", node => node is not JsonValue value ||
            !value.TryGetValue<string>(out var text) || System.DateTimeOffset.TryParse(text,
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)));
    }
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "bundles", name);
    private static JsonSchema Schema(string kind) => JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", $"meeting-evidence-{kind}-v1.schema.json"));
    private static bool Matches(JsonSchema schema, JsonNode node) => schema.Evaluate(node, new EvaluationOptions { RequireFormatValidation = true }).IsValid;

    [Theory]
    [InlineData("valid-completed")] [InlineData("valid-no-screenshots")] [InlineData("valid-system-audio-only")]
    [InlineData("valid-microphone-only")] [InlineData("valid-video-only")] [InlineData("valid-unknown-event")]
    [InlineData("incomplete-session")]
    public void RepresentativeFixturesMatchSchemasAndReferenceContract(string name)
    {
        var root = Fixture(name);
        Assert.True(Matches(Schema("session"), JsonNode.Parse(File.ReadAllText(Path.Combine(root, "session.json")))!));
        foreach (var line in File.ReadLines(Path.Combine(root, "events.jsonl")))
            Assert.True(Matches(Schema("event"), JsonNode.Parse(line)!));
        var result = FakeConsumer.Open(root, name == "incomplete-session");
        Assert.True(result.IsStructurallyValid, string.Join("; ", result.Diagnostics));
        Assert.False(result.MediaValidated);
        if (name != "incomplete-session") Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_MEDIA_NOT_VALIDATED");
    }

    [Theory]
    [InlineData("invalid-missing-recording", "BUNDLE_RECORDING_MISSING")]
    [InlineData("invalid-missing-screenshot", "BUNDLE_ASSET_MISSING")]
    [InlineData("invalid-path-traversal", "BUNDLE_PATH_INVALID")]
    [InlineData("invalid-event-json", "BUNDLE_EVENTS_INVALID")]
    [InlineData("invalid-unsupported-schema", "BUNDLE_SCHEMA_UNSUPPORTED")]
    public void InvalidFixturesReportTheirSpecificDefect(string name, string code)
    {
        var result = FakeConsumer.Open(Fixture(name));
        Assert.False(result.IsStructurallyValid);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData("schema_version", "\"2.0\"")]
    [InlineData("schema_version", "\"1.0\\n\"")]
    [InlineData("duration_ms", "null")]
    [InlineData("duration_ms", "0")]
    [InlineData("duration_ms", "-1")]
    [InlineData("duration_ms", "1.5")]
    [InlineData("status", "\"Completed\"")]
    [InlineData("created_at", "\"2026-09-09T10:30:00\"")]
    [InlineData("created_at", "\"2026-99-99T10:30:00Z\"")]
    [InlineData("recording", "null")]
    [InlineData("events_file", "\"../events.jsonl\"")]
    public void InvalidManifestValuesFailSchemaAndConsumer(string property, string json)
    {
        using var bundle = new SyntheticBundle();
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixture("valid-completed"), "session.json")))!;
        node[property] = JsonNode.Parse(json);
        Assert.False(Matches(Schema("session"), node));
        File.WriteAllText(Path.Combine(bundle.Root, "session.json"), node.ToJsonString(), BundleJson.Utf8);
        Assert.False(FakeConsumer.Open(bundle.Root).IsStructurallyValid);
    }

    [Theory]
    [InlineData("schema_version")] [InlineData("session_id")] [InlineData("created_at")]
    [InlineData("duration_ms")] [InlineData("status")] [InlineData("recording")]
    [InlineData("events_file")] [InlineData("application")] [InlineData("platform")]
    public void MissingRequiredManifestFieldsFail(string field)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixture("valid-completed"), "session.json")))!.AsObject();
        node.Remove(field);
        Assert.False(Matches(Schema("session"), node));
        using var bundle = new SyntheticBundle();
        File.WriteAllText(Path.Combine(bundle.Root, "session.json"), node.ToJsonString(), BundleJson.Utf8);
        Assert.False(FakeConsumer.Open(bundle.Root).IsStructurallyValid);
    }

    [Theory]
    [InlineData("video", "width")] [InlineData("video", "fps")]
    [InlineData("audio", "microphone")] [InlineData("audio", "output_mode")]
    public void SuppliedRecommendedValuesCannotBeNull(string section, string field)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixture("valid-completed"), "session.json")))!;
        node["recording"]![section]![field] = null;
        Assert.False(Matches(Schema("session"), node));
        Assert.Throws<JsonException>(() => BundleJson.ReadSession(BundleJson.Utf8.GetBytes(node.ToJsonString())));
    }

    [Theory] [MemberData(nameof(PathAndCompletionTests.UnsafePaths), MemberType = typeof(PathAndCompletionTests))]
    public void SchemaAlsoRejectsUnsafeScreenshotPaths(string path)
    {
        var node = new JsonObject { ["event_id"] = "a", ["type"] = "screenshot", ["timestamp_ms"] = 0, ["asset"] = path };
        Assert.False(Matches(Schema("event"), node));
    }

    [Fact]
    public void AudioContradictionsFailButFutureSourceAndOutputModesAreTolerated()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixture("valid-video-only"), "session.json")))!;
        node["recording"]!["audio"]!["system_audio"] = true;
        Assert.False(Matches(Schema("session"), node));
        Assert.Contains(ContractValidation.Validate(BundleJson.ReadSession(BundleJson.Utf8.GetBytes(node.ToJsonString()))), d => d.Code == "BUNDLE_AUDIO_METADATA_INCONSISTENT");
        node["recording"]!["audio"]!["output_mode"] = "mixed_and_separate";
        node["recording"]!["video"]!["source_type"] = "future-camera";
        Assert.True(Matches(Schema("session"), node));
        Assert.Empty(ContractValidation.Validate(BundleJson.ReadSession(BundleJson.Utf8.GetBytes(node.ToJsonString()))));
    }

    [Fact]
    public void MicrophoneDisabledCannotDeclareADevice()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixture("valid-video-only"), "session.json")))!;
        node["recording"]!["audio"]!["microphone_device"] = "USB Microphone";

        Assert.False(Matches(Schema("session"), node));
        Assert.Contains(ContractValidation.Validate(BundleJson.ReadSession(BundleJson.Utf8.GetBytes(node.ToJsonString()))),
            d => d.Code == "BUNDLE_AUDIO_METADATA_INCONSISTENT");
    }

    [Fact]
    public void MicrophoneDeviceNullWhenDisabledRemainsValid()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixture("valid-video-only"), "session.json")))!;
        node["recording"]!["audio"]!["microphone_device"] = null;

        Assert.True(Matches(Schema("session"), node));
        Assert.DoesNotContain(ContractValidation.Validate(BundleJson.ReadSession(BundleJson.Utf8.GetBytes(node.ToJsonString()))),
            d => d.Code == "BUNDLE_AUDIO_METADATA_INCONSISTENT");
    }

    [Fact]
    public void MicrophoneDeviceRemainsValidWhenMicrophoneIsEnabled()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixture("valid-microphone-only"), "session.json")))!;
        node["recording"]!["audio"]!["microphone_device"] = "USB Microphone";

        Assert.True(Matches(Schema("session"), node));
        Assert.DoesNotContain(ContractValidation.Validate(BundleJson.ReadSession(BundleJson.Utf8.GetBytes(node.ToJsonString()))),
            d => d.Code == "BUNDLE_AUDIO_METADATA_INCONSISTENT");
    }
}
