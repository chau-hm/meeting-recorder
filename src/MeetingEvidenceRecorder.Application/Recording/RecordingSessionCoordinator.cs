using MeetingEvidenceRecorder.Core.Abstractions;
using MeetingEvidenceRecorder.Core.Capture;
using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Core.Recording;

namespace MeetingEvidenceRecorder.Application.Recording;

/// <summary>
/// Coordinates native capture, bounded consumers, media finalization and bundle commit.
/// </summary>
public sealed class RecordingSessionCoordinator : IAsyncDisposable
{
    private readonly IDisplaySystemAudioCaptureBackend capture;
    private readonly IMediaWriter mediaWriter;
    private readonly IRecordingBundleStoreFactory bundleStoreFactory;
    private readonly IRecordingClock clock;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object terminalGate = new();
    private readonly TaskCompletionSource<RecordingCompletion> completionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RecorderError> secondaryRuntimeErrors = [];

    private IRecordingBundleStore? bundleStore;
    private RecordingSessionOptions? options;
    private CancellationTokenSource? sessionCancellation;
    private Task? videoPump;
    private Task? audioPump;
    private Task? videoPumpObserver;
    private Task? audioPumpObserver;
    private RecordingTimestampMapper? timestampMapper;
    private TaskCompletionSource<bool>? timestampOriginReady;
    private NativeTimestamp? firstVideoTimestamp;
    private NativeTimestamp? firstAudioTimestamp;
    private TimeSpan? canonicalRecordingEnd;
    private RecorderError? runtimeError;
    private Task<RecordingCompletion>? terminalTask;
    private Guid sessionId;
    private DateTimeOffset createdAt;
    private bool captureStarted;
    private bool disposed;
    private int state = (int)RecordingState.Idle;

    public RecordingSessionCoordinator(
        IDisplaySystemAudioCaptureBackend capture,
        IMediaWriter mediaWriter,
        IRecordingBundleStoreFactory bundleStoreFactory,
        IRecordingClock? clock = null)
    {
        this.capture = capture;
        this.mediaWriter = mediaWriter;
        this.bundleStoreFactory = bundleStoreFactory;
        this.clock = clock ?? new RecordingClock();
        capture.Error += OnCaptureError;
    }

    public RecordingState State => (RecordingState)Volatile.Read(ref state);

    public Task<RecordingCompletion> Completion => completionSource.Task;

    public string? BundlePath => bundleStore?.Root;

    public long DroppedVideoFrames => capture.DroppedVideoFrames;

    public async Task StartAsync(RecordingSessionOptions sessionOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (State != RecordingState.Idle)
                throw new InvalidOperationException($"Cannot start from state {State}.");

            SetState(RecordingState.Starting);
            options = sessionOptions;
            lock (terminalGate)
            {
                runtimeError = null;
                secondaryRuntimeErrors.Clear();
                terminalTask = null;
            }

            sessionId = Guid.NewGuid();
            createdAt = DateTimeOffset.Now;
            timestampMapper = new RecordingTimestampMapper();
            timestampOriginReady = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            firstVideoTimestamp = null;
            firstAudioTimestamp = null;
            canonicalRecordingEnd = null;
            sessionCancellation = new CancellationTokenSource();
            bundleStore = bundleStoreFactory.Create(sessionOptions, sessionId, createdAt);

            bundleStore.WriteActive(CreateManifest(sessionOptions, SessionStatus.Initializing, null));

            var permission = await capture.GetScreenCaptureStatusAsync(cancellationToken).ConfigureAwait(false);
            if (permission != PermissionStatus.Granted)
            {
                throw new RecorderException(new RecorderError(
                    permission is PermissionStatus.Unsupported or PermissionStatus.Unavailable
                        ? "CAPTURE_SCREEN_UNAVAILABLE"
                        : "CAPTURE_SCREEN_PERMISSION_DENIED",
                    RecorderErrorSeverity.Fatal,
                    permission is PermissionStatus.Unsupported or PermissionStatus.Unavailable
                        ? "macOS Screen Recording is unavailable on this system."
                        : "Screen Recording permission is required before recording can start.",
                    $"Screen Recording capability reported {permission}."));
            }

            var sources = await capture.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
            if (!sources.Any(source => source.Id == sessionOptions.Source.Id))
            {
                throw new RecorderException(new RecorderError(
                    "CAPTURE_SOURCE_LOST",
                    RecorderErrorSeverity.Fatal,
                    "The selected display is no longer available.",
                    $"Display '{sessionOptions.Source.Id}' was not returned by the native source enumeration."));
            }

            await mediaWriter.InitializeAsync(
                new MediaWriterConfiguration(
                    bundleStore.Root,
                    Path.Combine(bundleStore.Root, ".work", "recording.partial.mkv"),
                    Path.Combine(bundleStore.Root, "recording.mp4"),
                    new VideoFormat(
                        sessionOptions.Source.Width,
                        sessionOptions.Source.Height,
                        sessionOptions.VideoOptions.FramesPerSecond),
                    new AudioFormat(48000, 2)),
                cancellationToken).ConfigureAwait(false);

            clock.Start();
            await capture.StartAsync(sessionOptions.Source, sessionOptions.VideoOptions, cancellationToken)
                .ConfigureAwait(false);
            captureStarted = true;

            lock (terminalGate)
            {
                if (runtimeError is not null)
                    throw new RecorderException(runtimeError);
                SetState(RecordingState.Recording);
            }

            bundleStore.WriteActive(CreateManifest(sessionOptions, SessionStatus.Recording, null));
            videoPump = PumpVideoAsync(sessionCancellation.Token);
            audioPump = PumpAudioAsync(sessionCancellation.Token);
            videoPumpObserver = ObservePumpAsync(videoPump, "video", sessionCancellation.Token);
            audioPumpObserver = ObservePumpAsync(audioPump, "audio", sessionCancellation.Token);
        }
        catch (Exception exception)
        {
            await RollbackStartAsync(exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<RecordingCompletion> StopAsync(CancellationToken cancellationToken)
    {
        Task<RecordingCompletion> terminal;
        lock (terminalGate)
        {
            if (completionSource.Task.IsCompleted)
            {
                terminal = completionSource.Task;
            }
            else
            {
                if (State != RecordingState.Recording)
                    throw new InvalidOperationException($"Cannot stop from state {State}.");

                terminal = terminalTask ??= Task.Run(
                    () => RunTerminalStopAsync(forceIncomplete: false));
            }
        }

        return await terminal.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecordingCompletion> RunTerminalStopAsync(bool forceIncomplete)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (completionSource.Task.IsCompleted)
                return await completionSource.Task.ConfigureAwait(false);
            if (State != RecordingState.Recording)
                return await completionSource.Task.ConfigureAwait(false);

            SetState(RecordingState.Stopping);
            RecordingCompletion completion;
            try
            {
                completion = await StopCoreAsync(forceIncomplete).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                completion = await FailAfterStopAsync(
                    exception,
                    GetPrimaryRuntimeError()).ConfigureAwait(false);
            }

            return PublishCompletion(completion);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<RecordingCompletion> StopCoreAsync(bool forceIncomplete)
    {
        var sessionToken = sessionCancellation?.Token ?? CancellationToken.None;
        TimeSpan? recordingEnd = null;
        Exception? pipelineFailure = null;

        try
        {
            if (captureStarted)
            {
                try
                {
                    await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    captureStarted = false;
                }
                catch
                {
                    sessionCancellation?.Cancel();
                    throw;
                }
                recordingEnd = clock.Elapsed;
                canonicalRecordingEnd = recordingEnd;
            }

            if (timestampMapper is { HasOrigin: false })
            {
                var missingOriginError = new RecorderException(new RecorderError(
                    "AUDIO_SYSTEM_UNAVAILABLE",
                    RecorderErrorSeverity.Fatal,
                    "System audio did not produce an initial timestamp before recording stopped.",
                    "The shared recording timeline could not be established for both required streams."));
                timestampOriginReady?.TrySetException(missingOriginError);
            }

            // Let accepted audio drain without waiting for another active-recording video commit.
            // All real video samples must be accepted before the canonical finalization tail is
            // emitted; that tail then releases any FFmpeg FIFO backpressure for audio.
            await mediaWriter.BeginFinalizationAsync(CancellationToken.None).ConfigureAwait(false);
            recordingEnd ??= CaptureCanonicalRecordingEnd();
            await AwaitPumpAsync(videoPump, sessionToken).ConfigureAwait(false);
            var videoTransportCompletion = mediaWriter.CompleteVideoTransportAsync(
                recordingEnd.Value,
                sessionToken);
            await AwaitPumpAsync(audioPump, sessionToken).ConfigureAwait(false);
            await mediaWriter.CompleteAudioTransportAsync(
                recordingEnd.Value,
                CancellationToken.None).ConfigureAwait(false);
            await videoTransportCompletion.ConfigureAwait(false);

        }
        catch (Exception exception)
        {
            pipelineFailure = exception;
        }

        var primaryRuntimeError = GetPrimaryRuntimeError();
        if (forceIncomplete || pipelineFailure is not null || primaryRuntimeError is not null)
        {
            var failure = pipelineFailure ??
                (primaryRuntimeError is not null
                    ? new RecorderException(primaryRuntimeError)
                    : new InvalidOperationException("Recording was terminated before media finalization."));
            return await FailAfterStopAsync(failure, primaryRuntimeError).ConfigureAwait(false);
        }

        recordingEnd ??= CaptureCanonicalRecordingEnd();
        clock.Stop();
        var finalized = await mediaWriter.FinalizeAsync(
            recordingEnd.Value,
            CancellationToken.None).ConfigureAwait(false);
        await mediaWriter.DisposeAsync().ConfigureAwait(false);

        var finalizing = CreateManifest(
            options!,
            SessionStatus.Finalizing,
            finalized,
            recordingEnd.Value);
        bundleStore!.WriteActive(finalizing);

        var diagnostics = bundleStore.CommitCompleted(finalizing);
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            bundleStore.MarkIncomplete(finalizing with { Status = SessionStatus.Incomplete });
            await bundleStore.DisposeAsync().ConfigureAwait(false);
            SetState(RecordingState.Incomplete);
            return new RecordingCompletion(
                bundleStore.Root,
                State,
                recordingEnd.Value,
                AddRuntimeDiagnostics(
                    diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}").ToArray()));
        }

        await bundleStore.DisposeAsync().ConfigureAwait(false);
        SetState(RecordingState.Completed);
        return new RecordingCompletion(
            bundleStore.Root,
            State,
            recordingEnd.Value,
            AddRuntimeDiagnostics(
                diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}").ToArray()));
    }

    private async Task PumpVideoAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in capture.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            var timestamp = await MapCaptureTimestampAsync(
                frame.SourceTimestamp,
                CaptureStream.Video,
                cancellationToken).ConfigureAwait(false);
            ValidateMappedTimestamp(timestamp, CaptureStream.Video);
            await mediaWriter.WriteVideoAsync(
                new TimedVideoFrame(frame, timestamp),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpAudioAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in capture.ReadSystemAudioAsync(cancellationToken).ConfigureAwait(false))
        {
            var timestamp = await MapCaptureTimestampAsync(
                frame.SourceTimestamp,
                CaptureStream.Audio,
                cancellationToken).ConfigureAwait(false);
            ValidateMappedTimestamp(
                timestamp,
                CaptureStream.Audio,
                frame.SampleCount,
                frame.Format.SampleRate);
            await mediaWriter.WriteAudioAsync(
                new TimedAudioFrame(frame, timestamp),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ObservePumpAsync(
        Task pump,
        string streamName,
        CancellationToken cancellationToken)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RegisterRuntimeError(
                exception is RecorderException recorderException
                    ? recorderException.Error
                    : new RecorderError(
                        "MEDIA_PIPELINE_FAILED",
                        RecorderErrorSeverity.Fatal,
                        $"The {streamName} recording pipeline failed.",
                        exception.Message));
        }
    }

    private async Task<RecordingCompletion> FailAfterStopAsync(
        Exception failure,
        RecorderError? primaryError)
    {
        sessionCancellation?.Cancel();
        var error = primaryError ?? CreatePipelineError(failure);
        var diagnostics = new List<string>
        {
            $"{error.Code}: {error.UserMessage}"
        };
        if (!string.IsNullOrWhiteSpace(error.DiagnosticMessage))
            diagnostics.Add(error.DiagnosticMessage);

        lock (terminalGate)
        {
            foreach (var secondary in secondaryRuntimeErrors)
            {
                diagnostics.Add($"{secondary.Code}: {secondary.UserMessage}");
                if (!string.IsNullOrWhiteSpace(secondary.DiagnosticMessage))
                    diagnostics.Add(secondary.DiagnosticMessage);
            }
        }

        if (captureStarted)
        {
            try
            {
                await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stopFailure)
            {
                diagnostics.Add($"CAPTURE_STOP_FAILED: {stopFailure.Message}");
            }
            finally
            {
                captureStarted = false;
            }
        }

        await AwaitPumpForShutdownAsync(videoPump, diagnostics).ConfigureAwait(false);
        await AwaitPumpForShutdownAsync(audioPump, diagnostics).ConfigureAwait(false);
        await AwaitPumpForShutdownAsync(videoPumpObserver, diagnostics).ConfigureAwait(false);
        await AwaitPumpForShutdownAsync(audioPumpObserver, diagnostics).ConfigureAwait(false);

        try
        {
            await mediaWriter.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeFailure)
        {
            diagnostics.Add($"MEDIA_DISPOSE_FAILED: {disposeFailure.Message}");
        }

        if (bundleStore is not null)
        {
            var failed = CreateManifest(options!, SessionStatus.Incomplete, null);
            try
            {
                bundleStore.MarkIncomplete(failed);
            }
            catch (Exception markFailure)
            {
                diagnostics.Add($"BUNDLE_INCOMPLETE_MARK_FAILED: {markFailure.Message}");
            }
            try
            {
                await bundleStore.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeFailure)
            {
                diagnostics.Add($"BUNDLE_DISPOSE_FAILED: {disposeFailure.Message}");
            }
            if (capture.DroppedVideoFrames > 0)
                diagnostics.Add($"CAPTURE_VIDEO_FRAMES_DROPPED: {capture.DroppedVideoFrames}");
            SetState(RecordingState.Incomplete);
            return new RecordingCompletion(
                bundleStore.Root,
                State,
                null,
                diagnostics,
                error);
        }

        SetState(RecordingState.Failed);
        return new RecordingCompletion("", State, null, diagnostics, error);
    }

    private async Task AwaitPumpForShutdownAsync(Task? pump, ICollection<string> diagnostics)
    {
        if (pump is null)
            return;

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception pumpFailure)
        {
            diagnostics.Add($"CAPTURE_PIPELINE_FAILED: {pumpFailure.Message}");
        }
    }

    private string[] AddRuntimeDiagnostics(IEnumerable<string> diagnostics)
    {
        var result = diagnostics.ToList();
        if (capture.DroppedVideoFrames > 0)
            result.Add($"CAPTURE_VIDEO_FRAMES_DROPPED: {capture.DroppedVideoFrames}");
        return result.ToArray();
    }

    private async ValueTask<TimeSpan> MapCaptureTimestampAsync(
        NativeTimestamp sourceTimestamp,
        CaptureStream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            Task? waitForOrigin = null;
            lock (terminalGate)
            {
                if (stream == CaptureStream.Video)
                    firstVideoTimestamp ??= sourceTimestamp;
                else
                    firstAudioTimestamp ??= sourceTimestamp;

                if (!timestampMapper!.HasOrigin &&
                    firstVideoTimestamp is NativeTimestamp video &&
                    firstAudioTimestamp is NativeTimestamp audio)
                {
                    timestampMapper.SetOrigin(Earlier(video, audio));
                    timestampOriginReady!.TrySetResult(true);
                }

                if (!timestampMapper.HasOrigin)
                    waitForOrigin = timestampOriginReady!.Task;
            }

            if (waitForOrigin is not null)
                await waitForOrigin.WaitAsync(cancellationToken).ConfigureAwait(false);

            return timestampMapper!.Map(sourceTimestamp);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or DivideByZeroException or OverflowException)
        {
            throw CreateTimelineDiscontinuity(
                stream,
                "The native timestamp could not be mapped onto the canonical recording timeline.",
                exception.Message);
        }
    }

    private void ValidateMappedTimestamp(
        TimeSpan timestamp,
        CaptureStream stream,
        int sampleCount = 0,
        int sampleRate = 0)
    {
        var coveredUntil = timestamp;
        if (stream == CaptureStream.Audio)
        {
            if (sampleCount < 0 || sampleRate <= 0)
            {
                throw CreateTimelineDiscontinuity(
                    stream,
                    "The native audio buffer has invalid duration metadata.",
                    $"Sample count={sampleCount}, sample rate={sampleRate}.");
            }

            try
            {
                coveredUntil = timestamp + TimeSpan.FromSeconds((double)sampleCount / sampleRate);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
            {
                throw CreateTimelineDiscontinuity(
                    stream,
                    "The native audio buffer duration overflowed the canonical recording timeline.",
                    exception.Message);
            }
        }

        var clockElapsed = canonicalRecordingEnd ?? clock.Elapsed;
        var allowedEnd = clockElapsed >= TimeSpan.MaxValue - RecordingTimelinePolicy.SourceTimestampLeadTolerance
            ? TimeSpan.MaxValue
            : clockElapsed + RecordingTimelinePolicy.SourceTimestampLeadTolerance;
        if (coveredUntil <= allowedEnd)
            return;

        throw CreateTimelineDiscontinuity(
            stream,
            "A native media timestamp advanced materially beyond the canonical recording clock.",
            $"Mapped {stream.ToString().ToLowerInvariant()} timestamp covers through {coveredUntil.TotalMilliseconds:0} ms while the canonical recording end is {clockElapsed.TotalMilliseconds:0} ms; allowed lead is {RecordingTimelinePolicy.SourceTimestampLeadTolerance.TotalMilliseconds:0} ms.");
    }

    private TimeSpan CaptureCanonicalRecordingEnd()
    {
        var end = clock.Elapsed;
        canonicalRecordingEnd ??= end;
        return canonicalRecordingEnd.Value;
    }

    private static RecorderException CreateTimelineDiscontinuity(
        CaptureStream stream,
        string userMessage,
        string diagnosticMessage) =>
        new(new RecorderError(
            "MEDIA_TIMELINE_DISCONTINUITY",
            RecorderErrorSeverity.Fatal,
            userMessage,
            $"Stream={stream}; {diagnosticMessage}"));

    private static NativeTimestamp Earlier(NativeTimestamp first, NativeTimestamp second)
    {
        var firstSeconds = (decimal)first.Value / first.Timescale;
        var secondSeconds = (decimal)second.Value / second.Timescale;
        return firstSeconds <= secondSeconds ? first : second;
    }

    private async Task RollbackStartAsync(Exception original)
    {
        sessionCancellation?.Cancel();
        if (captureStarted)
        {
            try
            {
                await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stopFailure)
            {
                original = new AggregateException(original, stopFailure);
            }
            finally
            {
                captureStarted = false;
            }
        }

        await AwaitPumpForShutdownAsync(videoPump, []);
        await AwaitPumpForShutdownAsync(audioPump, []);
        await AwaitPumpForShutdownAsync(videoPumpObserver, []);
        await AwaitPumpForShutdownAsync(audioPumpObserver, []);

        try
        {
            await mediaWriter.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (bundleStore is not null)
            {
                try
                {
                    bundleStore.MarkIncomplete(CreateManifest(options!, SessionStatus.Failed, null));
                }
                finally
                {
                    await bundleStore.DisposeAsync().ConfigureAwait(false);
                }
            }

            SetState(RecordingState.Failed);
            PublishCompletion(new RecordingCompletion(
                bundleStore?.Root ?? "",
                State,
                null,
                [$"START_FAILED: {original.Message}"],
                original is RecorderException recorderException
                    ? recorderException.Error
                    : CreatePipelineError(original, "Recording startup failed.")));
        }
    }

    private static async Task AwaitPumpAsync(Task? pump, CancellationToken cancellationToken)
    {
        if (pump is null)
            return;

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private SessionManifest CreateManifest(
        RecordingSessionOptions sessionOptions,
        SessionStatus status,
        FinalizedMedia? finalized,
        TimeSpan? canonicalDuration = null)
    {
        var video = finalized?.VideoFormat ?? new VideoFormat(
            sessionOptions.Source.Width,
            sessionOptions.Source.Height,
            sessionOptions.VideoOptions.FramesPerSecond);
        var audio = new AudioMetadata
        {
            Microphone = false,
            MicrophoneDevice = null
        };
        if (finalized is { HasAudio: true, AudioFormat: not null } finalMedia)
        {
            audio = new AudioMetadata
            {
                SystemAudio = true,
                Microphone = false,
                MicrophoneDevice = null,
                OutputMode = "system_audio_only",
                SampleRate = finalMedia.AudioFormat.SampleRate,
                SystemAudioCaptureMode = "os_native",
                SynchronizedToRecordingTimeline = true
            };
        }
        else if (finalized is { HasAudio: false })
        {
            audio = new AudioMetadata
            {
                SystemAudio = false,
                Microphone = false,
                MicrophoneDevice = null,
                OutputMode = "none",
                SystemAudioCaptureMode = "os_native",
                SynchronizedToRecordingTimeline = true
            };
        }

        return new SessionManifest
        {
            SchemaVersion = "1.0",
            SessionId = sessionId.ToString(),
            CreatedAt = createdAt,
            DurationMs = finalized is null
                ? null
                : Math.Max(
                    1,
                    (long)Math.Round((canonicalDuration ?? finalized.Duration).TotalMilliseconds)),
            Status = status,
            Recording = new RecordingMetadata
            {
                File = "recording.mp4",
                Video = new VideoMetadata
                {
                    SourceType = "display",
                    Width = video.Width,
                    Height = video.Height,
                    Fps = video.FramesPerSecond
                },
                Audio = audio
            },
            EventsFile = "events.jsonl",
            Application = new ApplicationMetadata
            {
                Name = "Meeting Evidence Recorder",
                Version = sessionOptions.ApplicationVersion
            },
            Platform = new PlatformMetadata
            {
                Os = "macOS",
                Architecture = "arm64"
            }
        };
    }

    private void OnCaptureError(object? sender, RecorderErrorEventArgs args)
    {
        if (args.Error.Severity != RecorderErrorSeverity.Fatal)
            return;

        RegisterRuntimeError(args.Error);
    }

    private void RegisterRuntimeError(RecorderError error)
    {
        lock (terminalGate)
        {
            if (completionSource.Task.IsCompleted)
                return;

            if (runtimeError is null)
                runtimeError = error;
            else
                secondaryRuntimeErrors.Add(error);

            sessionCancellation?.Cancel();
            if (State == RecordingState.Recording && terminalTask is null)
            {
                terminalTask = Task.Run(
                    () => RunTerminalStopAsync(forceIncomplete: true));
            }
        }
    }

    private RecorderError? GetPrimaryRuntimeError()
    {
        lock (terminalGate)
            return runtimeError;
    }

    private static RecorderError CreatePipelineError(
        Exception exception,
        string? userMessage = null) =>
        exception is RecorderException recorderException
            ? recorderException.Error
            : new RecorderError(
                "MEDIA_PIPELINE_FAILED",
                RecorderErrorSeverity.Fatal,
                userMessage ?? "The recording media pipeline failed.",
                exception.Message);

    private RecordingCompletion PublishCompletion(RecordingCompletion completion)
    {
        if (completionSource.TrySetResult(completion))
            return completion;
        return completionSource.Task.GetAwaiter().GetResult();
    }

    private void SetState(RecordingState value) =>
        Volatile.Write(ref state, (int)value);

    private void EnsureNotDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(RecordingSessionCoordinator));
    }

    public async ValueTask DisposeAsync()
    {
        Task<RecordingCompletion>? pendingTerminal;
        lock (terminalGate)
        {
            if (State == RecordingState.Recording &&
                !completionSource.Task.IsCompleted &&
                terminalTask is null)
            {
                runtimeError ??= new RecorderError(
                    "RECORDING_DISPOSED",
                    RecorderErrorSeverity.Fatal,
                    "Recording was disposed before it could be finalized.",
                    "The coordinator was disposed while recording was active.");
                sessionCancellation?.Cancel();
                terminalTask = Task.Run(
                    () => RunTerminalStopAsync(forceIncomplete: true));
            }

            pendingTerminal = terminalTask;
        }

        if (pendingTerminal is not null && !completionSource.Task.IsCompleted)
            await pendingTerminal.ConfigureAwait(false);

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;

            disposed = true;
            sessionCancellation?.Cancel();
            Exception? shutdownFailure = null;
            if (captureStarted)
            {
                try
                {
                    await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    shutdownFailure = exception;
                }
                finally
                {
                    captureStarted = false;
                }
            }

            var shutdownDiagnostics = new List<string>();
            await AwaitPumpForShutdownAsync(videoPump, shutdownDiagnostics).ConfigureAwait(false);
            await AwaitPumpForShutdownAsync(audioPump, shutdownDiagnostics).ConfigureAwait(false);
            await AwaitPumpForShutdownAsync(videoPumpObserver, shutdownDiagnostics).ConfigureAwait(false);
            await AwaitPumpForShutdownAsync(audioPumpObserver, shutdownDiagnostics).ConfigureAwait(false);
            if (shutdownFailure is null &&
                shutdownDiagnostics.Count > 0 &&
                !completionSource.Task.IsCompleted)
                shutdownFailure = new InvalidOperationException(string.Join("; ", shutdownDiagnostics));

            try
            {
                await mediaWriter.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (bundleStore is not null)
                        await bundleStore.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    sessionCancellation?.Dispose();
                    sessionCancellation = null;
                    capture.Error -= OnCaptureError;
                    if (State is not (RecordingState.Completed or RecordingState.Incomplete or RecordingState.Failed))
                        SetState(RecordingState.Failed);
                }
            }

            if (shutdownFailure is not null)
                throw shutdownFailure;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private enum CaptureStream
    {
        Video,
        Audio
    }
}
