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
}

// Windows symlink creation can require privileges. Keep the default harness runnable without them.
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Symlink creation requires optional Windows privileges; run equivalent platform validation separately.";
    }
}
