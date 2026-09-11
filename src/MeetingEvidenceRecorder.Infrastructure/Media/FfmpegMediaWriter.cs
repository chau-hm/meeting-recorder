using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using MeetingEvidenceRecorder.Core.Abstractions;
using MeetingEvidenceRecorder.Core.Capture;
using MeetingEvidenceRecorder.Core.Recording;

namespace MeetingEvidenceRecorder.Infrastructure.Media;

public sealed class MediaWriterException(string code, string message, Exception? inner = null)
    : InvalidOperationException($"{code}: {message}", inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Streams normalized frames through bounded OS FIFOs into FFmpeg. Native timestamps are
/// converted to deterministic CFR/PCM positions before the encoder sees the bytes.
/// </summary>
public sealed class FfmpegMediaWriter : IMediaWriter
{
    private readonly string ffmpegPath;
    private readonly string ffprobePath;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private MediaWriterConfiguration? configuration;
    private FileStream? videoPipe;
    private FileStream? audioPipe;
    private string? audioFifoPath;
    private Channel<AudioTransportChunk>? audioWriteQueue;
    private Task? audioWriterTask;
    private Exception? audioWriterFailure;
    private CancellationTokenSource? audioTransportCancellation;
    private int audioTransportStarted;
    private readonly object audioTransportGate = new();
    private TaskCompletionSource<bool> audioTransportChanged = CreateAudioTransportSignal();
    private bool audioTransportFinalizing;
    private Process? encoder;
    private Task<string>? encoderErrorTask;
    private byte[]? lastVideoFrame;
    private byte[]? blackVideoFrame;
    private long nextVideoFrame;
    private long nextAudioSample;
    private bool hasVideo;
    private bool hasAudio;
    private bool finalized;
    private bool disposed;

    public FfmpegMediaWriter(string ffmpegPath, string ffprobePath)
    {
        this.ffmpegPath = ValidateExecutable(ffmpegPath, nameof(ffmpegPath));
        this.ffprobePath = ValidateExecutable(ffprobePath, nameof(ffprobePath));
    }

    public async Task InitializeAsync(MediaWriterConfiguration writerConfiguration, CancellationToken cancellationToken)
    {
        if (configuration is not null)
            throw new InvalidOperationException("The media writer has already been initialized.");
        if (writerConfiguration.VideoFormat.Width <= 0 || writerConfiguration.VideoFormat.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(writerConfiguration), "Video dimensions must be positive.");
        if (writerConfiguration.AudioFormat.SampleRate <= 0 || writerConfiguration.AudioFormat.Channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(writerConfiguration), "Audio format must be positive.");

        configuration = writerConfiguration;
        Directory.CreateDirectory(configuration.WorkDirectory);
        DeleteIfPresent(configuration.WorkMediaPath);
        var videoFifo = Path.Combine(configuration.WorkDirectory, "video.frames.fifo");
        var audioFifo = Path.Combine(configuration.WorkDirectory, "audio.samples.fifo");
        audioFifoPath = audioFifo;
        DeleteIfPresent(videoFifo);
        DeleteIfPresent(audioFifo);
        await CreateFifoAsync(videoFifo, cancellationToken).ConfigureAwait(false);
        await CreateFifoAsync(audioFifo, cancellationToken).ConfigureAwait(false);

        encoder = StartEncoder(videoFifo, audioFifo);
        encoderErrorTask = encoder.StandardError.ReadToEndAsync(cancellationToken);
        // Open only the video write end during initialization. FFmpeg opens its audio input
        // after it receives video, so the audio write end is opened lazily after the first real
        // video frame. Both opens use non-blocking native FIFO polling so cancellation and
        // encoder failure cannot strand a managed FileStream on an unpaired FIFO open.
        videoPipe = await OpenFifoForWriteAsync(videoFifo, cancellationToken).ConfigureAwait(false);
        audioWriteQueue = Channel.CreateBounded<AudioTransportChunk>(new BoundedChannelOptions(AudioTransportQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        audioTransportCancellation = new CancellationTokenSource();
    }

    public async ValueTask WriteVideoAsync(TimedVideoFrame timedFrame, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteVideoCoreAsync(timedFrame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async ValueTask WriteVideoCoreAsync(
        TimedVideoFrame timedFrame,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        var frame = timedFrame.Frame;
        var expectedBytes = checked(frame.Format.Width * frame.Format.Height * 4);
        if (frame.Data.Length != expectedBytes)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "A video frame did not contain tightly packed BGRA data.");

        var targetIndex = Math.Max(0, (long)Math.Round(
            timedFrame.RecordingTimestamp.TotalSeconds * frame.Format.FramesPerSecond,
            MidpointRounding.AwayFromZero));
        EnsureRuntimeVideoGapWithinBound(targetIndex);
        while (nextVideoFrame < targetIndex)
        {
            var padding = lastVideoFrame is null ? GetBlackVideoFrame(expectedBytes) : lastVideoFrame;
            await videoPipe!.WriteAsync(padding, cancellationToken).ConfigureAwait(false);
            nextVideoFrame++;
        }

        // CFR slot policy is deterministic: the first frame committed for a slot wins. A later
        // frame mapped to an already committed slot is discarded; only a real video arrival (or
        // finalization) is allowed to advance the committed position, never an audio write.
        if (targetIndex < nextVideoFrame)
            return;

        await videoPipe!.WriteAsync(frame.Data, cancellationToken).ConfigureAwait(false);
        lastVideoFrame = frame.Data.ToArray();
        nextVideoFrame = targetIndex + 1;
        hasVideo = true;
        StartAudioTransportIfNeeded();
        SignalAudioTransportProgress();
    }

    public async ValueTask WriteAudioAsync(TimedAudioFrame timedFrame, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAudioCoreAsync(timedFrame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async ValueTask WriteAudioCoreAsync(
        TimedAudioFrame timedFrame,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        var frame = timedFrame.Frame;
        var channels = configuration!.AudioFormat.Channels;
        if (frame.Format.Channels != channels || frame.Format.SampleRate != configuration.AudioFormat.SampleRate)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "Audio frames must use the configured normalized format.");
        if (frame.Samples.Length < frame.SampleCount * channels)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "An audio frame contained fewer samples than declared.");

        var targetSample = Math.Max(0, (long)Math.Round(
            timedFrame.RecordingTimestamp.TotalSeconds * frame.Format.SampleRate,
            MidpointRounding.AwayFromZero));
        var skipSamples = Math.Max(0, nextAudioSample - targetSample);
        if (skipSamples >= frame.SampleCount)
            return;

        if (targetSample > nextAudioSample)
        {
            var missingSamples = targetSample - nextAudioSample;
            var maximumGapSamples = checked((long)Math.Ceiling(
                RecordingTimelinePolicy.MaximumRuntimeWriterGap.TotalSeconds * frame.Format.SampleRate));
            if (missingSamples > maximumGapSamples)
            {
                throw new MediaWriterException(
                    "MEDIA_ENCODER_FAILED",
                    "An audio timestamp requested an unreasonable active-recording gap.");
            }

            await WriteSilenceAsync(missingSamples, cancellationToken).ConfigureAwait(false);
            nextAudioSample = targetSample;
        }

        var samplesToWrite = frame.Samples
            .Slice(checked((int)(skipSamples * channels)), checked((frame.SampleCount - (int)skipSamples) * channels));
        var bytesToWrite = MemoryMarshal.AsBytes(samplesToWrite.Span).ToArray();
        var acceptedSampleCount = frame.SampleCount - skipSamples;
        var audioEndSample = checked(nextAudioSample + acceptedSampleCount);
        await QueueAudioBytesAsync(bytesToWrite, audioEndSample, cancellationToken).ConfigureAwait(false);
        nextAudioSample = audioEndSample;
        hasAudio = true;
    }

    public async Task<FinalizedMedia> FinalizeAsync(
        TimeSpan recordingEnd,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FinalizeCoreAsync(recordingEnd, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task<FinalizedMedia> FinalizeCoreAsync(
        TimeSpan recordingEnd,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        if (finalized)
            throw new InvalidOperationException("The media writer has already been finalized.");
        if (recordingEnd < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recordingEnd), "Recording end cannot be negative.");
        if (!hasVideo)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "No video frames were accepted.");
        if (!hasAudio)
            throw new MediaWriterException("AUDIO_SYSTEM_UNAVAILABLE", "No system-audio samples were accepted.");

        await ExtendInputsToRecordingEndAsync(recordingEnd, cancellationToken).ConfigureAwait(false);
        lock (audioTransportGate)
        {
            audioTransportFinalizing = true;
            audioTransportChanged.TrySetResult(true);
        }
        finalized = true;
        await CloseInputPipesAsync().ConfigureAwait(false);
        await encoder!.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var encoderError = encoderErrorTask is null ? "" : await encoderErrorTask.ConfigureAwait(false);
        if (encoder.ExitCode != 0)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", encoderError.Trim());

        await RemuxToMp4Async(
                configuration!.WorkMediaPath,
                configuration.FinalMediaPath,
                recordingEnd,
                cancellationToken)
            .ConfigureAwait(false);
        var probe = new FfmpegMediaProbe(ffprobePath);
        var result = await probe.ProbeAsync(configuration.FinalMediaPath, cancellationToken).ConfigureAwait(false);
        if (!result.HasVideo || !result.HasAudio)
            throw new MediaWriterException("MEDIA_MUX_FAILED", "Final media probe did not find both video and system-audio streams.");
        if (result.AudioSampleRate is not int sampleRate)
            throw new MediaWriterException("MEDIA_MUX_FAILED", "Final media probe did not report the audio sample rate.");
        if (!string.Equals(result.VideoCodecName, "h264", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.AudioCodecName, "aac", StringComparison.OrdinalIgnoreCase))
            throw new MediaWriterException("MEDIA_MUX_FAILED", "Final media did not contain the requested H.264/AAC streams.");

        DeleteIfPresent(Path.Combine(configuration.WorkDirectory, "video.frames.fifo"));
        DeleteIfPresent(Path.Combine(configuration.WorkDirectory, "audio.samples.fifo"));
        var videoFormat = result.VideoFramesPerSecond is double framesPerSecond
            ? configuration.VideoFormat with { FramesPerSecond = framesPerSecond }
            : configuration.VideoFormat;
        return new FinalizedMedia(
            configuration.FinalMediaPath,
            result.Duration,
            videoFormat,
            configuration.AudioFormat with { SampleRate = sampleRate },
            result.HasVideo,
            result.HasAudio);
    }

    private async Task ExtendInputsToRecordingEndAsync(
        TimeSpan recordingEnd,
        CancellationToken cancellationToken)
    {
        var writerConfiguration = configuration!;
        // Capture has stopped and both accepted queues have drained before this method is
        // called. Only the canonical recording end may advance media now; source stream ends
        // are never allowed to redefine the final duration.
        await ExtendVideoToTimeAsync(
                recordingEnd,
                cancellationToken,
                allowLargeGap: true)
            .ConfigureAwait(false);

        var targetAudioSamples = checked((long)Math.Ceiling(
            recordingEnd.TotalSeconds * writerConfiguration.AudioFormat.SampleRate));
        var missingAudioSamples = targetAudioSamples - nextAudioSample;
        var maximumTailSamples = checked((long)Math.Ceiling(
            RecordingTimelinePolicy.FinalizationTailTolerance.TotalSeconds * writerConfiguration.AudioFormat.SampleRate));
        if (missingAudioSamples > 0 && missingAudioSamples <= maximumTailSamples)
        {
            await WriteSilenceAsync(missingAudioSamples, cancellationToken)
                .ConfigureAwait(false);
            nextAudioSample = targetAudioSamples;
        }
    }

    private async Task ExtendVideoToTimeAsync(
        TimeSpan targetEnd,
        CancellationToken cancellationToken,
        bool allowLargeGap = false)
    {
        var targetVideoFrames = checked((long)Math.Ceiling(
            targetEnd.TotalSeconds * configuration!.VideoFormat.FramesPerSecond));
        if (!allowLargeGap)
            EnsureRuntimeVideoGapWithinBound(targetVideoFrames);

        while (nextVideoFrame < targetVideoFrames)
        {
            await videoPipe!.WriteAsync(lastVideoFrame!, cancellationToken).ConfigureAwait(false);
            nextVideoFrame++;
            SignalAudioTransportProgress();
        }
    }

    private void EnsureRuntimeVideoGapWithinBound(long targetVideoFrame)
    {
        var gapFrames = targetVideoFrame - nextVideoFrame;
        var maximumGapFrames = checked((long)Math.Ceiling(
            RecordingTimelinePolicy.MaximumRuntimeWriterGap.TotalSeconds * configuration!.VideoFormat.FramesPerSecond));
        if (gapFrames <= maximumGapFrames)
            return;

        throw new MediaWriterException(
            "MEDIA_ENCODER_FAILED",
            "A video timestamp requested an unreasonable active-recording gap.");
    }

    private Process StartEncoder(string videoFifo, string audioFifo)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Add(startInfo, "-hide_banner");
        Add(startInfo, "-loglevel");
        Add(startInfo, "error");
        Add(startInfo, "-y");
        Add(startInfo, "-thread_queue_size");
        Add(startInfo, InputThreadQueueSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-f");
        Add(startInfo, "rawvideo");
        Add(startInfo, "-pixel_format");
        Add(startInfo, "bgra");
        Add(startInfo, "-video_size");
        Add(startInfo, $"{configuration!.VideoFormat.Width}x{configuration.VideoFormat.Height}");
        Add(startInfo, "-framerate");
        Add(startInfo, configuration.VideoFormat.FramesPerSecond.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-i");
        Add(startInfo, videoFifo);
        Add(startInfo, "-thread_queue_size");
        Add(startInfo, InputThreadQueueSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-f");
        Add(startInfo, "f32le");
        Add(startInfo, "-ar");
        Add(startInfo, configuration.AudioFormat.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-ac");
        Add(startInfo, configuration.AudioFormat.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-i");
        Add(startInfo, audioFifo);
        Add(startInfo, "-map");
        Add(startInfo, "0:v:0");
        Add(startInfo, "-map");
        Add(startInfo, "1:a:0");
        Add(startInfo, "-c:v");
        Add(startInfo, "h264_videotoolbox");
        Add(startInfo, "-b:v");
        Add(startInfo, "8M");
        Add(startInfo, "-pix_fmt");
        Add(startInfo, "yuv420p");
        Add(startInfo, "-c:a");
        Add(startInfo, "aac");
        Add(startInfo, "-b:a");
        Add(startInfo, "192k");
        Add(startInfo, "-ar");
        Add(startInfo, configuration.AudioFormat.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-f");
        Add(startInfo, "matroska");
        Add(startInfo, configuration.WorkMediaPath);

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new MediaWriterException("MEDIA_ENCODER_FAILED", "FFmpeg did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "The configured FFmpeg executable could not be started.", ex);
        }

        return process;
    }

    private async Task RemuxToMp4Async(
        string workPath,
        string finalPath,
        TimeSpan recordingEnd,
        CancellationToken cancellationToken)
    {
        DeleteIfPresent(finalPath);
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Add(startInfo, "-hide_banner");
        Add(startInfo, "-loglevel");
        Add(startInfo, "error");
        Add(startInfo, "-y");
        Add(startInfo, "-i");
        Add(startInfo, workPath);
        Add(startInfo, "-t");
        Add(startInfo, recordingEnd.TotalSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "-map");
        Add(startInfo, "0:v:0");
        Add(startInfo, "-map");
        Add(startInfo, "0:a:0");
        Add(startInfo, "-c");
        Add(startInfo, "copy");
        Add(startInfo, "-movflags");
        Add(startInfo, "+faststart");
        Add(startInfo, finalPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new MediaWriterException("MEDIA_MUX_FAILED", "FFmpeg remux did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MediaWriterException("MEDIA_MUX_FAILED", "The configured FFmpeg executable could not be started for remux.", ex);
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new MediaWriterException("MEDIA_MUX_FAILED", error.Trim());
    }

    private async Task CloseInputPipesAsync()
    {
        if (videoPipe is not null)
        {
            await videoPipe.DisposeAsync().ConfigureAwait(false);
            videoPipe = null;
        }

        Exception? transportFailure = null;
        if (audioWriteQueue is not null)
        {
            audioWriteQueue.Writer.TryComplete();
            if (audioWriterTask is not null)
            {
                try
                {
                    await audioWriterTask.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    transportFailure = new MediaWriterException(
                        "MEDIA_ENCODER_FAILED",
                        "The audio transport failed while closing the media inputs.",
                        exception);
                }
                finally
                {
                    audioWriterTask = null;
                }
            }
            audioWriteQueue = null;
        }

        try
        {
            if (audioPipe is not null)
                await audioPipe.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            audioPipe = null;
        }

        if (transportFailure is not null)
            throw transportFailure;
    }

    private async Task WriteSilenceAsync(long sampleCount, CancellationToken cancellationToken)
    {
        var channels = configuration!.AudioFormat.Channels;
        var chunkSamples = (long)configuration.AudioFormat.SampleRate;
        var silence = new byte[checked((int)(chunkSamples * channels * sizeof(float)))];
        var nextSilenceSample = nextAudioSample;
        while (sampleCount > 0)
        {
            var samples = Math.Min(sampleCount, chunkSamples);
            var bytes = silence.AsMemory(0, checked((int)(samples * channels * sizeof(float))));
            nextSilenceSample = checked(nextSilenceSample + samples);
            await QueueAudioBytesAsync(bytes.ToArray(), nextSilenceSample, cancellationToken).ConfigureAwait(false);
            sampleCount -= samples;
        }
    }

    private ValueTask QueueAudioBytesAsync(
        byte[] bytes,
        long audioEndSample,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (audioWriterFailure is not null)
        {
            throw new MediaWriterException(
                "MEDIA_ENCODER_FAILED",
                "The audio transport failed while recording.",
                audioWriterFailure);
        }

        if (!audioWriteQueue!.Writer.TryWrite(
                new AudioTransportChunk(bytes, RequiredVideoFrameForAudioSample(audioEndSample))))
        {
            throw new MediaWriterException(
                "MEDIA_ENCODER_FAILED",
                "The bounded audio transport queue is full or closed.");
        }

        return ValueTask.CompletedTask;
    }

    private async Task DrainAudioPipeAsync()
    {
        try
        {
            await foreach (var chunk in audioWriteQueue!.Reader.ReadAllAsync(audioTransportCancellation!.Token).ConfigureAwait(false))
            {
                await WaitForVideoCoverageAsync(chunk.RequiredVideoFrame).ConfigureAwait(false);
                await audioPipe!.WriteAsync(chunk.Bytes).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            audioWriterFailure = exception;
            audioWriteQueue!.Writer.TryComplete(exception);
            throw;
        }
    }

    private void StartAudioTransportIfNeeded()
    {
        if (Interlocked.Exchange(ref audioTransportStarted, 1) != 0)
            return;

        audioWriterTask = Task.Run(
            async () =>
            {
                audioPipe = await OpenFifoForWriteAsync(
                        audioFifoPath!,
                        audioTransportCancellation!.Token)
                    .ConfigureAwait(false);
                await DrainAudioPipeAsync().ConfigureAwait(false);
            });
    }

    private async Task WaitForVideoCoverageAsync(long requiredVideoFrame)
    {
        while (true)
        {
            Task waitForProgress;
            lock (audioTransportGate)
            {
                if (audioTransportFinalizing || requiredVideoFrame <= Volatile.Read(ref nextVideoFrame))
                    return;
                waitForProgress = audioTransportChanged.Task;
            }

            await waitForProgress.ConfigureAwait(false);
        }
    }

    private long RequiredVideoFrameForAudioSample(long audioEndSample) =>
        checked((long)Math.Ceiling(
            audioEndSample / (double)configuration!.AudioFormat.SampleRate * configuration.VideoFormat.FramesPerSecond));

    private void SignalAudioTransportProgress()
    {
        lock (audioTransportGate)
        {
            audioTransportChanged.TrySetResult(true);
            audioTransportChanged = CreateAudioTransportSignal();
        }
    }

    private static TaskCompletionSource<bool> CreateAudioTransportSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<FileStream> OpenFifoForWriteAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "The FIFO media writer requires macOS.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileDescriptor = NativeOpen(path, O_WRONLY | O_NONBLOCK, 0);
            if (fileDescriptor >= 0)
            {
                try
                {
                    var flags = NativeFcntl(fileDescriptor, F_GETFL, 0);
                    if (flags < 0 || NativeFcntl(fileDescriptor, F_SETFL, flags & ~O_NONBLOCK) < 0)
                        throw new MediaWriterException(
                            "MEDIA_ENCODER_FAILED",
                            $"Could not configure FIFO '{path}' for blocking writes.",
                            new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));

                    var handle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
                    return new FileStream(handle, FileAccess.Write, 64 * 1024, isAsync: false);
                }
                catch
                {
                    NativeClose(fileDescriptor);
                    throw;
                }
            }

            var error = Marshal.GetLastWin32Error();
            if (error is not (EINTR or ENXIO or EAGAIN))
                throw new MediaWriterException(
                    "MEDIA_ENCODER_FAILED",
                    $"Could not open FIFO '{path}' for writing.",
                    new System.ComponentModel.Win32Exception(error));

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private byte[] GetBlackVideoFrame(int size)
    {
        blackVideoFrame ??= new byte[size];
        return blackVideoFrame;
    }

    private async Task CreateFifoAsync(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "The FIFO media writer requires macOS.");
        var startInfo = new ProcessStartInfo("/usr/bin/mkfifo")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo) ??
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "mkfifo did not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", $"mkfifo failed for {path}.");
    }

    private static void Add(ProcessStartInfo startInfo, string argument) => startInfo.ArgumentList.Add(argument);

    private const int AudioTransportQueueCapacity = 1024;
    private const int InputThreadQueueSize = 8;

    private const int O_WRONLY = 0x0001;
    private const int O_NONBLOCK = 0x0004;
    private const int EINTR = 4;
    private const int ENXIO = 6;
    private const int EAGAIN = 35;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int NativeOpen(string path, int flags, int mode);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int NativeFcntl(int fileDescriptor, int command, int argument);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int fileDescriptor);

    private readonly record struct AudioTransportChunk(byte[] Bytes, long RequiredVideoFrame);

    private static string ValidateExecutable(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An executable path is required.", parameterName);
        if (!Path.IsPathRooted(path) || !File.Exists(path))
            throw new FileNotFoundException("The configured executable path does not exist.", path);
        return path;
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private void EnsureReady()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(FfmpegMediaWriter));
        if (configuration is null || encoder is null || videoPipe is null || audioWriteQueue is null)
            throw new InvalidOperationException("The media writer has not been initialized.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        try
        {
            audioTransportCancellation?.Cancel();
            if (encoder is { HasExited: false })
            {
                try
                {
                    encoder.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process may have exited between the state check and Kill.
                }
            }
            await CloseInputPipesAsync().ConfigureAwait(false);
            if (encoder is not null)
                await encoder.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process may already have exited while a failure is being unwound.
        }
        finally
        {
            if (configuration is not null)
            {
                DeleteIfPresent(Path.Combine(configuration.WorkDirectory, "video.frames.fifo"));
                DeleteIfPresent(Path.Combine(configuration.WorkDirectory, "audio.samples.fifo"));
            }
            encoder?.Dispose();
            audioTransportCancellation?.Dispose();
            audioTransportCancellation = null;
        }
    }
}
