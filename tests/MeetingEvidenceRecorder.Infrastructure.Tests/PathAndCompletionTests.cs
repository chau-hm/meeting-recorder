using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Infrastructure.Persistence;

namespace MeetingEvidenceRecorder.Infrastructure.Tests;

public class PathAndCompletionTests
{
    public static TheoryData<string> UnsafePaths => new()
    {
        "../secret.txt", "screenshots/../../outside.png", "/absolute.png", "C:/secret.png", "C:relative.png",
        "\\\\server\\share\\shot.png", "screenshots\\..\\secret.png", "screenshots/./shot.png", "screenshots//shot.png",
        "screenshots/shot.png/", "screenshots/.. /secret.png", "screenshots/shot.png.", "screenshots/shot.png ",
        "screenshots/shot.png:stream", "screenshots/CON.png", "screenshots/com1.png", "screenshots/a\nb.png", ""
    };

    [Theory] [MemberData(nameof(UnsafePaths))]
    public void UnsafePathsAreRejectedAcrossPlatformSyntax(string path)
    {
        using var bundle = new SyntheticBundle();
        Assert.Throws<BundlePathException>(() => new BundlePathResolver(bundle.Root).Resolve(path));
    }

    [SymlinkFact]
    public void SymlinkFileDirectoryAndDanglingReferenceAreRejected()
    {
        using var bundle = new SyntheticBundle();
        var resolver = new BundlePathResolver(bundle.Root);
        File.CreateSymbolicLink(Path.Combine(bundle.Root, "link.png"), Path.Combine(bundle.Root, "recording.mp4"));
        Directory.CreateSymbolicLink(Path.Combine(bundle.Root, "linked-dir"), Path.Combine(bundle.Root, "screenshots"));
        File.CreateSymbolicLink(Path.Combine(bundle.Root, "dangling.png"), Path.Combine(bundle.Root, "absent"));
        Assert.Throws<BundlePathException>(() => resolver.Resolve("link.png"));
        Assert.Throws<BundlePathException>(() => resolver.Resolve("linked-dir/shot.png"));
        Assert.Throws<BundlePathException>(() => resolver.Resolve("dangling.png"));
    }

    [Fact]
    public void DirectCompletedWriteIsBlockedAndMediaFailureLeavesFinalizing()
    {
        using var bundle = new SyntheticBundle(1);
        var writer = new ManifestWriter();
        Assert.Throws<InvalidOperationException>(() => writer.WriteActive(bundle.Root, bundle.Manifest with { Status = SessionStatus.Completed }));
        Assert.Throws<ArgumentNullException>(() => writer.CommitCompleted(bundle.Root, bundle.Manifest, null!));
        var errors = writer.CommitCompleted(bundle.Root, bundle.Manifest, new RejectSyntheticMedia());
        Assert.Contains(errors, d => d.Code == "BUNDLE_RECORDING_UNREADABLE");
        Assert.Equal(SessionStatus.Finalizing, FakeConsumer.Open(bundle.Root, true).Session!.Status);
        Assert.False(FakeConsumer.Open(bundle.Root).IsStructurallyValid);
        Assert.Empty(Directory.GetFiles(bundle.Root, "*.tmp"));
    }

    [Fact]
    public void MissingReferencesBlockCompletionBeforeMediaProbe()
    {
        using var bundle = new SyntheticBundle(1);
        File.Delete(Path.Combine(bundle.Root, "screenshots/shot-0000.png"));
        var errors = new ManifestWriter().CommitCompleted(bundle.Root, bundle.Manifest, new MustNotProbe());
        Assert.Contains(errors, d => d.Code == "BUNDLE_ASSET_MISSING");
        Assert.Equal(SessionStatus.Finalizing, FakeConsumer.Open(bundle.Root, true).Session!.Status);
    }

    [Fact]
    public void MissingRecordingAndBeyondDurationBlockCompletion()
    {
        using var bundle = new SyntheticBundle(7201001);
        File.Delete(Path.Combine(bundle.Root, "recording.mp4"));
        var errors = new ManifestWriter().CommitCompleted(bundle.Root, bundle.Manifest, new MustNotProbe());
        Assert.Contains(errors, d => d.Code == "BUNDLE_RECORDING_MISSING");
        Assert.Contains(errors, d => d.Code == "BUNDLE_TIMESTAMP_INVALID");
    }

    [Fact]
    public void RecordingAndAssetPathsDifferingOnlyByCaseAreRejected()
    {
        var result = ReadWithScreenshotAssets("Recording.mp4");
        Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_PATH_INVALID" && d.Path == "Recording.mp4");
    }

    [Fact]
    public void EventsFileAndAssetPathsDifferingOnlyByCaseAreRejected()
    {
        var result = ReadWithScreenshotAssets("Events.jsonl");
        Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_PATH_INVALID" && d.Path == "Events.jsonl");
    }

    [Fact]
    public void SessionFileAndAssetPathsDifferingOnlyByCaseAreRejected()
    {
        var result = ReadWithScreenshotAssets("Session.json");
        Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_PATH_INVALID" && d.Path == "Session.json");
    }

    [Fact]
    public void ScreenshotPathsDifferingOnlyByCaseAreRejected()
    {
        var result = ReadWithScreenshotAssets("screenshots/a.png", "screenshots/A.png");
        Assert.Contains(result.Diagnostics, d => d.Code == "BUNDLE_PATH_INVALID" && d.Path == "screenshots/A.png");
    }

    [Fact]
    public void IncompleteManifestIsInspectableButNeverStrictlyCompleted()
    {
        using var bundle = new SyntheticBundle();
        new ManifestWriter().WriteActive(bundle.Root, bundle.Manifest with
        {
            Status = SessionStatus.Incomplete, DurationMs = null,
            Recording = new() { Video = new(), Audio = new() }
        });
        Assert.False(FakeConsumer.Open(bundle.Root).IsStructurallyValid);
        var inspected = FakeConsumer.Open(bundle.Root, true);
        Assert.True(inspected.IsStructurallyValid);
        Assert.Null(inspected.Session!.DurationMs);
        Assert.Contains(inspected.Diagnostics, d => d.Code == "BUNDLE_STATUS_NOT_COMPLETED");
    }

    private sealed class RejectSyntheticMedia : ICompletionMediaValidator
    {
        public IReadOnlyList<BundleDiagnostic> Validate(string recordingPath, SessionManifest candidate) =>
            [new("BUNDLE_RECORDING_UNREADABLE", "Synthetic fixture contains no decodable media; completion denied.")];
    }
    private sealed class MustNotProbe : ICompletionMediaValidator
    {
        public IReadOnlyList<BundleDiagnostic> Validate(string recordingPath, SessionManifest candidate) =>
            throw new InvalidOperationException("Structural failure must prevent the media probe.");
    }

    private static BundleValidationResult ReadWithScreenshotAssets(params string[] assets)
    {
        using var bundle = new SyntheticBundle(Enumerable.Range(0, assets.Length).Select(i => (long)i).ToArray());
        foreach (var asset in assets)
        {
            var fullPath = Path.Combine(bundle.Root, asset.Replace('/', Path.DirectorySeparatorChar));
            if (asset.Equals("Recording.mp4", StringComparison.OrdinalIgnoreCase) ||
                asset.Equals("Events.jsonl", StringComparison.OrdinalIgnoreCase) ||
                asset.Equals("Session.json", StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, [1]);
        }
        var journalPath = Path.Combine(bundle.Root, bundle.Manifest.EventsFile!);
        var journalText = File.ReadAllText(journalPath);
        for (var i = 0; i < assets.Length; i++)
        {
            journalText = journalText.Replace($"screenshots/shot-{i:D4}.png", assets[i], StringComparison.Ordinal);
        }
        File.WriteAllText(journalPath, journalText, BundleJson.Utf8);
        return FakeConsumer.Open(bundle.Root);
    }
}

// Windows symlink creation can require privileges. Keep the default harness runnable without them.
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Symlink creation requires optional Windows privileges; run equivalent platform validation separately.";
    }
}
