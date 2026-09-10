using System.Runtime.CompilerServices;
using MeetingEvidenceRecorder.Application.Recording;
using MeetingEvidenceRecorder.Core.Abstractions;
using MeetingEvidenceRecorder.Core.Capture;
using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Core.Recording;

namespace MeetingEvidenceRecorder.Application.Tests;

public sealed class RecordingSessionCoordinatorTests
{
    [Fact]
    public async Task SuccessfulStartAndStopPublishesCompletedBundle()
    {
        var log = new List<string>();
        var capture = new FakeCapture(log);
        var writer = new FakeMediaWriter(log);
        var store = new FakeBundleStoreFactory(log);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        Assert.Equal(RecordingState.Recording, coordinator.State);

        var completion = await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingState.Completed, completion.State);
        Assert.Equal(TimeSpan.FromSeconds(2), completion.Duration);
        Assert.True(store.Store!.Committed);
        Assert.Contains("capture-stop", log);
        Assert.Contains("writer-finalize", log);
    }

    [Fact]
    public async Task PermissionFailureNeverEntersRecording()
    {
        var capture = new FakeCapture([]);
        capture.Permission = PermissionStatus.Denied;
        var writer = new FakeMediaWriter([]);
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock());

        var exception = await Assert.ThrowsAsync<RecorderException>(() =>
            coordinator.StartAsync(CreateOptions(), CancellationToken.None));

        Assert.Equal("CAPTURE_SCREEN_PERMISSION_DENIED", exception.Error.Code);
        Assert.Equal(RecordingState.Failed, coordinator.State);
        Assert.False(capture.Started);
        Assert.True(writer.Disposed);
        Assert.True(store.Store!.MarkedIncomplete);
    }

    [Fact]
    public async Task SystemAudioStartFailureNeverEntersRecording()
    {
        var capture = new FakeCapture([]);
        capture.StartFailure = new RecorderException(new RecorderError(
            "AUDIO_SYSTEM_UNAVAILABLE",
            RecorderErrorSeverity.Fatal,
            "System audio is unavailable.",
            "Synthetic failure."));
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            new FakeMediaWriter([]),
            new FakeBundleStoreFactory([]),
            new FakeClock());

        var exception = await Assert.ThrowsAsync<RecorderException>(() =>
            coordinator.StartAsync(CreateOptions(), CancellationToken.None));

        Assert.Equal("AUDIO_SYSTEM_UNAVAILABLE", exception.Error.Code);
        Assert.Equal(RecordingState.Failed, coordinator.State);
        Assert.False(capture.Started);
    }

    [Fact]
    public async Task MediaInitializationFailureNeverEntersRecording()
    {
        var writer = new FakeMediaWriter([]);
        writer.InitializeFailure = new InvalidOperationException("Synthetic media failure.");
        await using var coordinator = new RecordingSessionCoordinator(
            new FakeCapture([]),
            writer,
            new FakeBundleStoreFactory([]),
            new FakeClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartAsync(CreateOptions(), CancellationToken.None));

        Assert.Equal(RecordingState.Failed, coordinator.State);
        Assert.False(writer.Initialized);
    }

    [Fact]
    public async Task MediaFinalizationFailureProducesIncompleteSession()
    {
        var log = new List<string>();
        var writer = new FakeMediaWriter(log)
        {
            FinalizeFailure = new InvalidOperationException("Synthetic finalization failure.")
        };
        var store = new FakeBundleStoreFactory(log);
        await using var coordinator = new RecordingSessionCoordinator(
            new FakeCapture(log),
            writer,
            store,
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        var completion = await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.True(store.Store!.MarkedIncomplete);
        Assert.Contains("writer-dispose", log);
    }

    [Fact]
    public async Task MediaValidationFailureNeverPublishesCompleted()
    {
        var store = new FakeBundleStoreFactory([]);
        store.CommitDiagnostics =
        [
            new BundleDiagnostic("BUNDLE_RECORDING_UNREADABLE", "Synthetic validation failure.")
        ];
        await using var coordinator = new RecordingSessionCoordinator(
            new FakeCapture([]),
            new FakeMediaWriter([]),
            store,
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        var completion = await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.True(store.Store!.MarkedIncomplete);
        Assert.False(store.Store.Committed);
    }

    [Fact]
    public async Task DisposalStopsCaptureBeforeWriterAndStore()
    {
        var log = new List<string>();
        var store = new FakeBundleStoreFactory(log);
        await using (var coordinator = new RecordingSessionCoordinator(
                         new FakeCapture(log),
                         new FakeMediaWriter(log),
                         store,
                         new FakeClock()))
        {
            await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        }

        Assert.Equal(
            ["capture-stop", "writer-dispose", "store-dispose"],
            log.Where(item => item is "capture-stop" or "writer-dispose" or "store-dispose"));
    }

    private static RecordingSessionOptions CreateOptions() =>
        new(
            "test-output",
            new CaptureSource("display-1", "Display 1", CaptureSourceKind.Display, 2, 2),
            new VideoCaptureOptions(30, ShowCursor: false));

    private sealed class FakeClock : IRecordingClock
    {
        public TimeSpan Elapsed { get; private set; }
        public bool IsRunning { get; private set; }
        public bool IsPaused => false;

        public void Start() => IsRunning = true;
        public void Pause() => throw new NotSupportedException();
        public void Resume() => throw new NotSupportedException();

        public void Stop()
        {
            IsRunning = false;
            Elapsed = TimeSpan.FromSeconds(2);
        }
    }

    private sealed class FakeCapture(List<string> log) : IDisplaySystemAudioCaptureBackend
    {
        private readonly CaptureSource source =
            new("display-1", "Display 1", CaptureSourceKind.Display, 2, 2);

        public event EventHandler<RecorderErrorEventArgs>? Error
        {
            add { }
            remove { }
        }
        public PermissionStatus Permission { get; set; } = PermissionStatus.Granted;
        public Exception? StartFailure { get; set; }
        public bool Started { get; private set; }
        public long DroppedVideoFrames => 0;
        public NativeTimestamp? SourceTimestampOrigin => null;

        public Task<PermissionStatus> GetScreenCaptureStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Permission);
        }

        public Task<PermissionStatus> RequestScreenCaptureAccessAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Permission);

        public Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CaptureSource>>([source]);

        public Task StartAsync(
            CaptureSource selectedSource,
            VideoCaptureOptions options,
            CancellationToken cancellationToken)
        {
            if (StartFailure is not null)
                return Task.FromException(StartFailure);

            Started = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VideoFrame(
                1,
                new NativeTimestamp(10, 10),
                new byte[16],
                new VideoFormat(2, 2, 30));
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<AudioFrame> ReadSystemAudioAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AudioFrame(
                AudioSourceKind.System,
                new NativeTimestamp(10, 10),
                1,
                new float[2],
                new AudioFormat(48000, 2));
            await Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Started)
            {
                Started = false;
                log.Add("capture-stop");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMediaWriter(List<string> log) : IMediaWriter
    {
        public Exception? InitializeFailure { get; set; }
        public Exception? FinalizeFailure { get; set; }
        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }

        public Task InitializeAsync(MediaWriterConfiguration configuration, CancellationToken cancellationToken)
        {
            if (InitializeFailure is not null)
                return Task.FromException(InitializeFailure);

            Initialized = true;
            return Task.CompletedTask;
        }

        public ValueTask WriteVideoAsync(TimedVideoFrame frame, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAudioAsync(TimedAudioFrame frame, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public Task<FinalizedMedia> FinalizeAsync(CancellationToken cancellationToken)
        {
            if (FinalizeFailure is not null)
                return Task.FromException<FinalizedMedia>(FinalizeFailure);

            log.Add("writer-finalize");
            return Task.FromResult(new FinalizedMedia(
                "recording.mp4",
                TimeSpan.FromSeconds(2),
                new VideoFormat(2, 2, 30),
                new AudioFormat(48000, 2),
                HasVideo: true,
                HasAudio: true));
        }

        public ValueTask DisposeAsync()
        {
            if (!Disposed)
            {
                Disposed = true;
                log.Add("writer-dispose");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeBundleStoreFactory(List<string> log) : IRecordingBundleStoreFactory
    {
        public FakeBundleStore? Store { get; private set; }
        public IReadOnlyList<BundleDiagnostic> CommitDiagnostics { get; set; } = [];

        public IRecordingBundleStore Create(
            RecordingSessionOptions options,
            Guid sessionId,
            DateTimeOffset createdAt)
        {
            Store = new FakeBundleStore(log, CommitDiagnostics);
            return Store;
        }
    }

    private sealed class FakeBundleStore(
        List<string> log,
        IReadOnlyList<BundleDiagnostic> commitDiagnostics) : IRecordingBundleStore
    {
        public string Root => "test-bundle";
        public bool Committed { get; private set; }
        public bool MarkedIncomplete { get; private set; }

        public void WriteActive(SessionManifest manifest)
        {
        }

        public void AppendEvent(RecordingEvent entry)
        {
        }

        public IReadOnlyList<BundleDiagnostic> CommitCompleted(SessionManifest finalizingManifest)
        {
            Committed = !commitDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
            return commitDiagnostics;
        }

        public void MarkIncomplete(SessionManifest manifest) => MarkedIncomplete = true;

        public ValueTask DisposeAsync()
        {
            if (!Disposed)
            {
                Disposed = true;
                log.Add("store-dispose");
            }
            return ValueTask.CompletedTask;
        }

        private bool Disposed { get; set; }
    }
}
