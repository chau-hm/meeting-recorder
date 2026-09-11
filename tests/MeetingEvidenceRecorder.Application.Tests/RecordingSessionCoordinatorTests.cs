using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
            new FakeClock { RunningElapsed = TimeSpan.FromSeconds(2) });

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        Assert.Equal(RecordingState.Recording, coordinator.State);
        var activeManifests = store.Store!.ActiveManifests
            .Where(manifest => manifest.Status is SessionStatus.Initializing or SessionStatus.Recording)
            .ToArray();
        Assert.Equal(2, activeManifests.Length);
        Assert.All(
            activeManifests,
            manifest =>
            {
                Assert.Null(manifest.Recording.Audio.SystemAudio);
                Assert.Null(manifest.Recording.Audio.OutputMode);
                Assert.Null(manifest.Recording.Audio.SampleRate);
            });

        var completion = await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingState.Completed, completion.State);
        Assert.Equal(TimeSpan.FromSeconds(2), completion.Duration);
        Assert.Null(completion.Error);
        Assert.True(store.Store!.Committed);
        Assert.True(store.Store.CommittedManifest!.Recording.Audio.SystemAudio);
        Assert.False(store.Store.CommittedManifest.Recording.Audio.Microphone);
        Assert.Equal("system_audio_only", store.Store.CommittedManifest.Recording.Audio.OutputMode);
        Assert.Equal(48000, store.Store.CommittedManifest.Recording.Audio.SampleRate);
        Assert.Contains("capture-stop", log);
        Assert.Contains("writer-finalize", log);
    }

    [Fact]
    public async Task StopReleasesAudioTransportBackpressureBeforeDrainingPumps()
    {
        var capture = new FakeCapture([]);
        var writer = new BackpressuredMediaWriter();
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock
            {
                RunningElapsed = TimeSpan.FromSeconds(2),
                StoppedElapsed = TimeSpan.FromSeconds(99)
            });

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        await ArrangeBackpressuredMediaAsync(capture, writer);

        var stopTask = coordinator.StopAsync(CancellationToken.None);
        var finalizationReleasedAudio = false;
        try
        {
            try
            {
                await writer.FinalizationReleasedAudio.Task.WaitAsync(TimeSpan.FromSeconds(2));
                finalizationReleasedAudio = true;
            }
            catch (TimeoutException)
            {
                // Release the fake transport below so the test can finish its cleanup if the
                // shutdown path regresses into a wait.
            }

            var completion = await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(RecordingState.Completed, completion.State);
            Assert.Equal(TimeSpan.FromSeconds(2), completion.Duration);
            Assert.Equal(1, writer.FinalizeCalls);
            Assert.True(writer.SecondAudioWriteCompleted.Task.IsCompletedSuccessfully);
            Assert.True(writer.SecondVideoWriteCompleted.Task.IsCompletedSuccessfully);
            Assert.Equal(1, capture.StopCalls);
        }
        finally
        {
            writer.ReleaseBlockedAudio();
            await writer.SecondAudioWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await writer.SecondVideoWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(
            finalizationReleasedAudio,
            "Finalization mode must release blocked audio before the video pump can drain.");
    }

    [Fact]
    public async Task FatalShutdownCancelsBackpressuredMediaWithoutHanging()
    {
        var capture = new FakeCapture([]);
        var writer = new BackpressuredMediaWriter();
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock { RunningElapsed = TimeSpan.FromSeconds(2) });

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        await ArrangeBackpressuredMediaAsync(capture, writer);
        capture.RaiseError(new RecorderError(
            "CAPTURE_SOURCE_LOST",
            RecorderErrorSeverity.Fatal,
            "The selected display is no longer available.",
            "Synthetic source loss while media is backpressured."));

        var completion = await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal("CAPTURE_SOURCE_LOST", completion.Error!.Code);
        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(0, writer.FinalizeCalls);
        Assert.True(writer.Disposed);
        Assert.True(store.Store!.MarkedIncomplete);
    }

    [Fact]
    public async Task DisposeCompletesWhileMediaIsBackpressured()
    {
        var capture = new FakeCapture([]);
        var writer = new BackpressuredMediaWriter();
        var store = new FakeBundleStoreFactory([]);
        var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock { RunningElapsed = TimeSpan.FromSeconds(2) });

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        await ArrangeBackpressuredMediaAsync(capture, writer);

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var completion = await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal("RECORDING_DISPOSED", completion.Error!.Code);
        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(0, writer.FinalizeCalls);
        Assert.True(writer.Disposed);
        Assert.True(store.Store!.MarkedIncomplete);
    }

    [Fact]
    public async Task PermissionFailureNeverEntersRecordingOrClaimsSystemAudio()
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
        Assert.Null(store.Store.IncompleteManifest!.Recording.Audio.SystemAudio);
        Assert.True(coordinator.Completion.IsCompletedSuccessfully);
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
    public async Task StopWithoutAnInitialAudioSampleTerminatesIncomplete()
    {
        var capture = new FakeCapture([]);
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            new FakeMediaWriter([]),
            store,
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitVideo();

        var completion = await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal("AUDIO_SYSTEM_UNAVAILABLE", completion.Error!.Code);
        Assert.Equal(1, capture.StopCalls);
        Assert.Null(store.Store!.IncompleteManifest!.Recording.Audio.SystemAudio);
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
        Assert.Equal("MEDIA_PIPELINE_FAILED", completion.Error!.Code);
        Assert.True(store.Store!.MarkedIncomplete);
        Assert.Contains("writer-dispose", log);
        Assert.Null(store.Store.IncompleteManifest!.Recording.Audio.SystemAudio);
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
        Assert.True(store.Store.IncompleteManifest!.Recording.Audio.SystemAudio);
    }

    [Fact]
    public async Task FatalCaptureErrorAutomaticallyTerminatesRecording()
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
        capture.RaiseError(new RecorderError(
            "CAPTURE_SOURCE_LOST",
            RecorderErrorSeverity.Fatal,
            "The selected display is no longer available.",
            "Synthetic source loss."));

        var completion = await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal("CAPTURE_SOURCE_LOST", completion.Error!.Code);
        Assert.Equal(RecordingState.Incomplete, coordinator.State);
        Assert.Equal(1, capture.StopCalls);
        Assert.True(writer.Disposed);
        Assert.True(store.Store!.MarkedIncomplete);
        Assert.Null(store.Store.IncompleteManifest!.Recording.Audio.SystemAudio);
    }

    [Fact]
    public async Task VideoWriterFailureAutomaticallyTerminatesRecording()
    {
        var capture = new FakeCapture([]);
        var writer = new FakeMediaWriter([])
        {
            VideoWriteFailure = new InvalidOperationException("Synthetic video pipe failure.")
        };
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitVideo();
        capture.EmitAudio();

        var completion = await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal("MEDIA_PIPELINE_FAILED", completion.Error!.Code);
        Assert.Equal(1, capture.StopCalls);
        Assert.True(writer.Disposed);
        Assert.True(store.Store!.MarkedIncomplete);
    }

    [Fact]
    public async Task AudioWriterFailureAutomaticallyTerminatesRecording()
    {
        var capture = new FakeCapture([]);
        var writer = new FakeMediaWriter([])
        {
            AudioWriteFailure = new InvalidOperationException("Synthetic audio pipe failure.")
        };
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitVideo();
        capture.EmitAudio();

        var completion = await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal("MEDIA_PIPELINE_FAILED", completion.Error!.Code);
        Assert.Equal(1, capture.StopCalls);
        Assert.True(writer.Disposed);
        Assert.True(store.Store!.MarkedIncomplete);
    }

    [Fact]
    public async Task MultipleSimultaneousFailuresUseOneShutdownAndKeepFirstCause()
    {
        var capture = new FakeCapture([]);
        var writer = new FakeMediaWriter([])
        {
            VideoWriteFailure = new InvalidOperationException("Synthetic video pipe failure."),
            AudioWriteFailure = new InvalidOperationException("Synthetic audio pipe failure.")
        };
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitVideo();
        capture.EmitAudio();
        capture.RaiseError(new RecorderError(
            "CAPTURE_SOURCE_LOST",
            RecorderErrorSeverity.Fatal,
            "The selected display is no longer available.",
            "Synthetic source loss."));

        var completion = await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(1, writer.DisposeCalls);
        Assert.Equal("CAPTURE_SOURCE_LOST", completion.Error!.Code);
        Assert.Contains(completion.Diagnostics, item => item.Contains("CAPTURE_SOURCE_LOST", StringComparison.Ordinal));

        var repeatedStop = await coordinator.StopAsync(CancellationToken.None);
        Assert.Equal(completion, repeatedStop);
    }

    [Fact]
    public async Task VideoCallbackFirstStillPreservesEarlierAudioTimestamp()
    {
        var capture = new FakeCapture([]);
        var writer = new FakeMediaWriter([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            new FakeBundleStoreFactory([]),
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitVideo(new NativeTimestamp(10020, 1000));
        capture.EmitAudio(new NativeTimestamp(10000, 1000));
        await Task.WhenAll(
            writer.VideoWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            writer.AudioWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(20), writer.VideoTimestamps.Single());
        Assert.Equal(TimeSpan.Zero, writer.AudioTimestamps.Single());
    }

    [Fact]
    public async Task AudioCallbackFirstStillPreservesEarlierVideoTimestamp()
    {
        var capture = new FakeCapture([]);
        var writer = new FakeMediaWriter([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            new FakeBundleStoreFactory([]),
            new FakeClock());

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitAudio(new NativeTimestamp(10020, 1000));
        capture.EmitVideo(new NativeTimestamp(10000, 1000));
        await Task.WhenAll(
            writer.VideoWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            writer.AudioWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, writer.VideoTimestamps.Single());
        Assert.Equal(TimeSpan.FromMilliseconds(20), writer.AudioTimestamps.Single());
    }

    [Fact]
    public async Task AcceptsBoundedVideoLeadAndAudioBufferCoverage()
    {
        var capture = new FakeCapture([]);
        var writer = new FakeMediaWriter([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            new FakeBundleStoreFactory([]),
            new FakeClock { RunningElapsed = TimeSpan.FromSeconds(1) });

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitVideo(new NativeTimestamp(1000, 1000));
        capture.EmitAudio(new NativeTimestamp(1000, 1000));
        capture.EmitVideo(new NativeTimestamp(2030, 1000));
        capture.EmitAudio(new NativeTimestamp(2000, 1000), sampleCount: 2400);
        await Task.WhenAll(
            writer.SecondVideoWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            writer.SecondAudioWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var completion = await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingState.Completed, completion.State);
        Assert.Contains(TimeSpan.FromMilliseconds(1030), writer.VideoTimestamps);
        Assert.Contains(TimeSpan.FromSeconds(1), writer.AudioTimestamps);
    }

    [Fact]
    public async Task CompletedManifestUsesCanonicalClockDurationInsteadOfFinalizedMediaDuration()
    {
        var writer = new FakeMediaWriter([])
        {
            FinalizedDurationOverride = TimeSpan.FromSeconds(100)
        };
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            new FakeCapture([]),
            writer,
            store,
            new FakeClock { RunningElapsed = TimeSpan.FromSeconds(10) });

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        var completion = await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingState.Completed, completion.State);
        Assert.Equal(TimeSpan.FromSeconds(10), completion.Duration);
        Assert.Equal(10_000L, store.Store!.CommittedManifest!.DurationMs);
    }

    [Fact]
    public async Task LargeVideoTimestampDiscontinuityAutomaticallyTerminatesRecording()
    {
        var capture = new FakeCapture([]);
        var writer = new FakeMediaWriter([]);
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock { RunningElapsed = TimeSpan.FromSeconds(10) });

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitVideo(new NativeTimestamp(0, 1));
        capture.EmitAudio(new NativeTimestamp(0, 1));
        capture.EmitVideo(new NativeTimestamp(100, 1));

        var completion = await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal("MEDIA_TIMELINE_DISCONTINUITY", completion.Error!.Code);
        Assert.Null(completion.Duration);
        Assert.True(coordinator.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, capture.StopCalls);
        Assert.True(store.Store!.MarkedIncomplete);
        Assert.Null(store.Store.IncompleteManifest!.DurationMs);
        Assert.Equal(0, writer.FinalizeCalls);
    }

    [Fact]
    public async Task LargeAudioTimestampDiscontinuityAutomaticallyTerminatesRecording()
    {
        var capture = new FakeCapture([]);
        var writer = new FakeMediaWriter([]);
        var store = new FakeBundleStoreFactory([]);
        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            writer,
            store,
            new FakeClock { RunningElapsed = TimeSpan.FromSeconds(10) });

        await coordinator.StartAsync(CreateOptions(), CancellationToken.None);
        capture.EmitVideo(new NativeTimestamp(0, 1));
        capture.EmitAudio(new NativeTimestamp(0, 1));
        capture.EmitAudio(new NativeTimestamp(100, 1));

        var completion = await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Incomplete, completion.State);
        Assert.Equal("MEDIA_TIMELINE_DISCONTINUITY", completion.Error!.Code);
        Assert.Null(completion.Duration);
        Assert.True(coordinator.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, capture.StopCalls);
        Assert.True(store.Store!.MarkedIncomplete);
        Assert.Null(store.Store.IncompleteManifest!.DurationMs);
        Assert.Equal(0, writer.FinalizeCalls);
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

    private static async Task ArrangeBackpressuredMediaAsync(
        FakeCapture capture,
        BackpressuredMediaWriter writer)
    {
        capture.EmitVideo(new NativeTimestamp(0, 1));
        capture.EmitAudio(new NativeTimestamp(0, 1), sampleCount: 960);
        await writer.FirstVideoWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.FirstAudioWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        capture.EmitAudio(new NativeTimestamp(20, 1000), sampleCount: 960);
        await writer.BlockedAudioWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        capture.EmitVideo(new NativeTimestamp(20, 1000));
        await writer.PendingVideoWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeClock : IRecordingClock
    {
        public TimeSpan Elapsed { get; private set; }
        public TimeSpan RunningElapsed { get; init; }
        public TimeSpan StoppedElapsed { get; init; } = TimeSpan.FromSeconds(2);
        public bool IsRunning { get; private set; }
        public bool IsPaused => false;

        public void Start()
        {
            IsRunning = true;
            Elapsed = RunningElapsed;
        }
        public void Pause() => throw new NotSupportedException();
        public void Resume() => throw new NotSupportedException();

        public void Stop()
        {
            IsRunning = false;
            Elapsed = StoppedElapsed;
        }
    }

    private sealed class FakeCapture(List<string> log) : IDisplaySystemAudioCaptureBackend
    {
        private readonly CaptureSource source =
            new("display-1", "Display 1", CaptureSourceKind.Display, 2, 2);
        private readonly Channel<VideoFrame> videoFrames = Channel.CreateUnbounded<VideoFrame>();
        private readonly Channel<AudioFrame> audioFrames = Channel.CreateUnbounded<AudioFrame>();
        private long sequence;

        public event EventHandler<RecorderErrorEventArgs>? Error;

        public PermissionStatus Permission { get; set; } = PermissionStatus.Granted;
        public Exception? StartFailure { get; set; }
        public bool Started { get; private set; }
        public int StopCalls { get; private set; }
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
            CaptureSource requestedSource,
            VideoCaptureOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StartFailure is not null)
                return Task.FromException(StartFailure);

            Started = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in videoFrames.Reader.ReadAllAsync(cancellationToken))
                yield return frame;
        }

        public async IAsyncEnumerable<AudioFrame> ReadSystemAudioAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in audioFrames.Reader.ReadAllAsync(cancellationToken))
                yield return frame;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            if (Started)
            {
                Started = false;
                log.Add("capture-stop");
            }

            videoFrames.Writer.TryComplete();
            audioFrames.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public void EmitVideo(NativeTimestamp? timestamp = null) =>
            videoFrames.Writer.TryWrite(new VideoFrame(
                Interlocked.Increment(ref sequence),
                timestamp ?? new NativeTimestamp(10, 1),
                new byte[16],
                new VideoFormat(2, 2, 30)));

        public void EmitAudio(NativeTimestamp? timestamp = null, int sampleCount = 1) =>
            audioFrames.Writer.TryWrite(new AudioFrame(
                AudioSourceKind.System,
                timestamp ?? new NativeTimestamp(10, 1),
                sampleCount,
                new float[sampleCount * 2],
                new AudioFormat(48000, 2)));

        public void RaiseError(RecorderError error) =>
            Error?.Invoke(this, new RecorderErrorEventArgs(error));

        public ValueTask DisposeAsync()
        {
            videoFrames.Writer.TryComplete();
            audioFrames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMediaWriter(List<string> log) : IMediaWriter
    {
        public Exception? InitializeFailure { get; set; }
        public Exception? VideoWriteFailure { get; set; }
        public Exception? AudioWriteFailure { get; set; }
        public Exception? FinalizeFailure { get; set; }
        public TimeSpan? FinalizedDurationOverride { get; set; }
        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }
        public int DisposeCalls { get; private set; }
        public int FinalizeCalls { get; private set; }
        public TaskCompletionSource<bool> VideoWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AudioWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondVideoWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondAudioWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TimeSpan> VideoTimestamps { get; } = [];
        public List<TimeSpan> AudioTimestamps { get; } = [];

        public Task InitializeAsync(MediaWriterConfiguration configuration, CancellationToken cancellationToken)
        {
            if (InitializeFailure is not null)
                return Task.FromException(InitializeFailure);

            Initialized = true;
            return Task.CompletedTask;
        }

        public async ValueTask WriteVideoAsync(
            TimedVideoFrame frame,
            CancellationToken cancellationToken)
        {
            VideoWriteEntered.TrySetResult(true);
            if (VideoWriteFailure is not null)
                throw VideoWriteFailure;
            cancellationToken.ThrowIfCancellationRequested();
            VideoTimestamps.Add(frame.RecordingTimestamp);
            if (VideoTimestamps.Count >= 2)
                SecondVideoWriteEntered.TrySetResult(true);
            await Task.CompletedTask;
        }

        public async ValueTask WriteAudioAsync(
            TimedAudioFrame frame,
            CancellationToken cancellationToken)
        {
            AudioWriteEntered.TrySetResult(true);
            if (AudioWriteFailure is not null)
                throw AudioWriteFailure;
            cancellationToken.ThrowIfCancellationRequested();
            AudioTimestamps.Add(frame.RecordingTimestamp);
            if (AudioTimestamps.Count >= 2)
                SecondAudioWriteEntered.TrySetResult(true);
            await Task.CompletedTask;
        }

        public ValueTask BeginFinalizationAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public Task CompleteVideoTransportAsync(
            TimeSpan recordingEnd,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAudioTransportAsync(
            TimeSpan recordingEnd,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FinalizedMedia> FinalizeAsync(
            TimeSpan recordingEnd,
            CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            if (FinalizeFailure is not null)
                return Task.FromException<FinalizedMedia>(FinalizeFailure);

            log.Add("writer-finalize");
            return Task.FromResult(new FinalizedMedia(
                "recording.mp4",
                FinalizedDurationOverride ?? recordingEnd,
                new VideoFormat(2, 2, 30),
                new AudioFormat(48000, 2),
                HasVideo: true,
                HasAudio: true));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (!Disposed)
            {
                Disposed = true;
                log.Add("writer-dispose");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class BackpressuredMediaWriter : IMediaWriter
    {
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private readonly TaskCompletionSource<bool> blockedAudioRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int audioWriteCount;
        private int videoWriteCount;
        private int audioBlocked;

        public bool Disposed { get; private set; }

        public TaskCompletionSource<bool> FirstVideoWriteCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstAudioWriteCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BlockedAudioWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> PendingVideoWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FinalizationReleasedAudio { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondAudioWriteCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondVideoWriteCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FinalizeCalls { get; private set; }

        public Task InitializeAsync(
            MediaWriterConfiguration configuration,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async ValueTask WriteVideoAsync(
            TimedVideoFrame frame,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref audioBlocked) != 0)
                PendingVideoWriteEntered.TrySetResult(true);

            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Interlocked.Increment(ref videoWriteCount) == 1)
                    FirstVideoWriteCompleted.TrySetResult(true);
                else
                    SecondVideoWriteCompleted.TrySetResult(true);
            }
            finally
            {
                writeGate.Release();
            }
        }

        public async ValueTask WriteAudioAsync(
            TimedAudioFrame frame,
            CancellationToken cancellationToken)
        {
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Interlocked.Increment(ref audioWriteCount) == 2)
                {
                    Volatile.Write(ref audioBlocked, 1);
                    BlockedAudioWriteEntered.TrySetResult(true);
                    await blockedAudioRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref audioBlocked, 0);
                }

                if (audioWriteCount == 1)
                    FirstAudioWriteCompleted.TrySetResult(true);
                else
                    SecondAudioWriteCompleted.TrySetResult(true);
            }
            finally
            {
                writeGate.Release();
            }
        }

        public ValueTask BeginFinalizationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizationReleasedAudio.TrySetResult(true);
            blockedAudioRelease.TrySetResult(true);
            return ValueTask.CompletedTask;
        }

        public Task CompleteVideoTransportAsync(
            TimeSpan recordingEnd,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAudioTransportAsync(
            TimeSpan recordingEnd,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FinalizedMedia> FinalizeAsync(
            TimeSpan recordingEnd,
            CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            return Task.FromResult(new FinalizedMedia(
                "recording.mp4",
                recordingEnd,
                new VideoFormat(2, 2, 30),
                new AudioFormat(48000, 2),
                HasVideo: true,
                HasAudio: true));
        }

        public void ReleaseBlockedAudio() => blockedAudioRelease.TrySetResult(true);

        public ValueTask DisposeAsync()
        {
            ReleaseBlockedAudio();
            Disposed = true;
            writeGate.Dispose();
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
        public SessionManifest? CommittedManifest { get; private set; }
        public SessionManifest? IncompleteManifest { get; private set; }
        public List<SessionManifest> ActiveManifests { get; } = [];

        public void WriteActive(SessionManifest manifest)
        {
            ActiveManifests.Add(manifest);
        }

        public void AppendEvent(RecordingEvent entry)
        {
        }

        public IReadOnlyList<BundleDiagnostic> CommitCompleted(SessionManifest finalizingManifest)
        {
            CommittedManifest = finalizingManifest;
            Committed = !commitDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
            return commitDiagnostics;
        }

        public void MarkIncomplete(SessionManifest manifest)
        {
            IncompleteManifest = manifest;
            MarkedIncomplete = true;
        }

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
