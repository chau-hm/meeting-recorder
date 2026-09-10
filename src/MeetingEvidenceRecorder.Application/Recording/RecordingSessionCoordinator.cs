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

    private IRecordingBundleStore? bundleStore;
    private RecordingSessionOptions? options;
    private CancellationTokenSource? sessionCancellation;
    private Task? videoPump;
    private Task? audioPump;
    private RecordingTimestampMapper? timestampMapper;
    private RecorderError? runtimeError;
    private Guid sessionId;
    private DateTimeOffset createdAt;
    private bool captureStarted;
    private bool disposed;

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

    public RecordingState State { get; private set; } = RecordingState.Idle;

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

            State = RecordingState.Starting;
            options = sessionOptions;
            runtimeError = null;
            sessionId = Guid.NewGuid();
            createdAt = DateTimeOffset.Now;
            timestampMapper = new RecordingTimestampMapper();
            sessionCancellation = new CancellationTokenSource();
            bundleStore = bundleStoreFactory.Create(sessionOptions, sessionId, createdAt);

            var manifest = CreateManifest(sessionOptions, SessionStatus.Initializing, null);
            bundleStore.WriteActive(manifest);

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
            await capture.StartAsync(sessionOptions.Source, sessionOptions.VideoOptions, cancellationToken).ConfigureAwait(false);
            captureStarted = true;
            if (runtimeError is not null)
                throw new RecorderException(runtimeError);
            SetCaptureTimestampOrigin();
            bundleStore.WriteActive(CreateManifest(sessionOptions, SessionStatus.Recording, null));

            videoPump = PumpVideoAsync(sessionCancellation.Token);
            audioPump = PumpAudioAsync(sessionCancellation.Token);
            State = RecordingState.Recording;
        }
        catch
        {
            await RollbackStartAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<RecordingCompletion> StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (State != RecordingState.Recording)
                throw new InvalidOperationException($"Cannot stop from state {State}.");

            State = RecordingState.Stopping;
            var sessionToken = sessionCancellation?.Token ?? CancellationToken.None;
            Exception? pipelineFailure = null;

            try
            {
                if (captureStarted)
                {
                    await capture.StopAsync(cancellationToken).ConfigureAwait(false);
                    captureStarted = false;
                }

                await AwaitPumpAsync(videoPump, sessionToken).ConfigureAwait(false);
                await AwaitPumpAsync(audioPump, sessionToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pipelineFailure = ex;
            }

            if (pipelineFailure is not null || runtimeError is not null)
                return await FailAfterStopAsync(pipelineFailure ?? new RecorderException(runtimeError!)).ConfigureAwait(false);

            try
            {
                clock.Stop();
                var finalized = await mediaWriter.FinalizeAsync(cancellationToken).ConfigureAwait(false);
                await mediaWriter.DisposeAsync().ConfigureAwait(false);
                var finalizing = CreateManifest(
                    options!,
                    SessionStatus.Finalizing,
                    finalized);
                bundleStore!.WriteActive(finalizing);

                var diagnostics = bundleStore.CommitCompleted(finalizing);
                if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    bundleStore.MarkIncomplete(finalizing with { Status = SessionStatus.Incomplete });
                    await bundleStore.DisposeAsync().ConfigureAwait(false);
                    State = RecordingState.Incomplete;
                    return new RecordingCompletion(
                        bundleStore.Root,
                        State,
                        finalized.Duration,
                        AddRuntimeDiagnostics(
                            diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}").ToArray()));
                }

                await bundleStore.DisposeAsync().ConfigureAwait(false);
                State = RecordingState.Completed;
                return new RecordingCompletion(
                    bundleStore.Root,
                    State,
                    finalized.Duration,
                    AddRuntimeDiagnostics(
                        diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}").ToArray()));
            }
            catch (Exception ex)
            {
                return await FailAfterStopAsync(ex).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task PumpVideoAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in capture.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            var timestamp = MapCaptureTimestamp(frame.SourceTimestamp);
            await mediaWriter.WriteVideoAsync(new TimedVideoFrame(frame, timestamp), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PumpAudioAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in capture.ReadSystemAudioAsync(cancellationToken).ConfigureAwait(false))
        {
            var timestamp = MapCaptureTimestamp(frame.SourceTimestamp);
            await mediaWriter.WriteAudioAsync(new TimedAudioFrame(frame, timestamp), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<RecordingCompletion> FailAfterStopAsync(Exception failure)
    {
        sessionCancellation?.Cancel();
        var diagnostics = new List<string>
        {
            $"MEDIA_PIPELINE_FAILED: {failure.Message}"
        };
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
            State = RecordingState.Incomplete;
            return new RecordingCompletion(bundleStore.Root, State, null, diagnostics);
        }

        State = RecordingState.Failed;
        return new RecordingCompletion("", State, null, diagnostics);
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

    private TimeSpan MapCaptureTimestamp(NativeTimestamp sourceTimestamp)
    {
        SetCaptureTimestampOrigin();
        return timestampMapper!.Map(sourceTimestamp);
    }

    private void SetCaptureTimestampOrigin()
    {
        if (capture.SourceTimestampOrigin is NativeTimestamp origin)
            timestampMapper!.SetOrigin(origin);
    }

    private async Task RollbackStartAsync()
    {
        sessionCancellation?.Cancel();
        if (captureStarted)
        {
            try
            {
                await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The original startup exception is the actionable error; resources are still disposed below.
            }
            captureStarted = false;
        }

        try
        {
            await mediaWriter.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (bundleStore is not null)
            {
                bundleStore.MarkIncomplete(CreateManifest(options!, SessionStatus.Failed, null));
                await bundleStore.DisposeAsync().ConfigureAwait(false);
            }

            State = RecordingState.Failed;
            sessionCancellation?.Dispose();
            sessionCancellation = null;
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
        FinalizedMedia? finalized)
    {
        var video = finalized?.VideoFormat ?? new VideoFormat(
            sessionOptions.Source.Width,
            sessionOptions.Source.Height,
            sessionOptions.VideoOptions.FramesPerSecond);
        var audio = finalized?.AudioFormat;
        return new SessionManifest
        {
            SchemaVersion = "1.0",
            SessionId = sessionId.ToString(),
            CreatedAt = createdAt,
            DurationMs = finalized is null ? null : Math.Max(1, (long)Math.Round(finalized.Duration.TotalMilliseconds)),
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
                Audio = new AudioMetadata
                {
                    SystemAudio = true,
                    Microphone = false,
                    MicrophoneDevice = null,
                    OutputMode = "system_audio_only",
                    SampleRate = audio?.SampleRate,
                    SystemAudioCaptureMode = "os_native",
                    SynchronizedToRecordingTimeline = true
                }
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
        runtimeError ??= args.Error;
        sessionCancellation?.Cancel();
    }

    private void EnsureNotDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(RecordingSessionCoordinator));
    }

    public async ValueTask DisposeAsync()
    {
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
            if (shutdownFailure is null && shutdownDiagnostics.Count > 0)
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
                    capture.Error -= OnCaptureError;
                    State = State == RecordingState.Completed ? State : RecordingState.Failed;
                }
            }

            if (shutdownFailure is not null)
                throw shutdownFailure;
        }
        finally
        {
            lifecycleGate.Release();
            lifecycleGate.Dispose();
        }
    }
}
