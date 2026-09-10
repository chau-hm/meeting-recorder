using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetingEvidenceRecorder.Core.Abstractions;
using MeetingEvidenceRecorder.Core.Capture;

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
    private MediaWriterConfiguration? configuration;
    private FileStream? videoPipe;
    private FileStream? audioPipe;
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
        DeleteIfPresent(videoFifo);
        DeleteIfPresent(audioFifo);
        await CreateFifoAsync(videoFifo, cancellationToken).ConfigureAwait(false);
        await CreateFifoAsync(audioFifo, cancellationToken).ConfigureAwait(false);

        encoder = StartEncoder(videoFifo, audioFifo);
        encoderErrorTask = encoder.StandardError.ReadToEndAsync(cancellationToken);
        // Open both FIFOs read/write so opening one input cannot block FFmpeg before it reaches
        // the other input. The writer still only writes through these streams.
        var videoOpen = Task.Run(() => new FileStream(videoFifo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite), cancellationToken);
        var audioOpen = Task.Run(() => new FileStream(audioFifo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite), cancellationToken);
        videoPipe = await videoOpen.ConfigureAwait(false);
        audioPipe = await audioOpen.ConfigureAwait(false);
    }

    public async ValueTask WriteVideoAsync(TimedVideoFrame timedFrame, CancellationToken cancellationToken)
    {
        EnsureReady();
        var frame = timedFrame.Frame;
        var expectedBytes = checked(frame.Format.Width * frame.Format.Height * 4);
        if (frame.Data.Length != expectedBytes)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "A video frame did not contain tightly packed BGRA data.");

        var targetIndex = Math.Max(0, (long)Math.Round(
            timedFrame.RecordingTimestamp.TotalSeconds * frame.Format.FramesPerSecond,
            MidpointRounding.AwayFromZero));
        while (nextVideoFrame < targetIndex)
        {
            var padding = lastVideoFrame is null ? GetBlackVideoFrame(expectedBytes) : lastVideoFrame;
            await videoPipe!.WriteAsync(padding, cancellationToken).ConfigureAwait(false);
            nextVideoFrame++;
        }

        if (targetIndex < nextVideoFrame)
            return;

        await videoPipe!.WriteAsync(frame.Data, cancellationToken).ConfigureAwait(false);
        lastVideoFrame = frame.Data.ToArray();
        nextVideoFrame = targetIndex + 1;
        hasVideo = true;
    }

    public async ValueTask WriteAudioAsync(TimedAudioFrame timedFrame, CancellationToken cancellationToken)
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
        if (targetSample > nextAudioSample)
        {
            await WriteSilenceAsync(targetSample - nextAudioSample, cancellationToken).ConfigureAwait(false);
            nextAudioSample = targetSample;
        }

        var skipSamples = Math.Max(0, nextAudioSample - targetSample);
        if (skipSamples >= frame.SampleCount)
            return;

        var samplesToWrite = frame.Samples
            .Slice(checked((int)(skipSamples * channels)), checked((frame.SampleCount - (int)skipSamples) * channels));
        var bytesToWrite = MemoryMarshal.AsBytes(samplesToWrite.Span).ToArray();
        await audioPipe!.WriteAsync(bytesToWrite, cancellationToken).ConfigureAwait(false);
        nextAudioSample += frame.SampleCount - skipSamples;
        hasAudio = true;
    }

    public async Task<FinalizedMedia> FinalizeAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        if (finalized)
            throw new InvalidOperationException("The media writer has already been finalized.");
        if (!hasVideo)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", "No video frames were accepted.");
        if (!hasAudio)
            throw new MediaWriterException("AUDIO_SYSTEM_UNAVAILABLE", "No system-audio samples were accepted.");

        finalized = true;
        await CloseInputPipesAsync().ConfigureAwait(false);
        await encoder!.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var encoderError = encoderErrorTask is null ? "" : await encoderErrorTask.ConfigureAwait(false);
        if (encoder.ExitCode != 0)
            throw new MediaWriterException("MEDIA_ENCODER_FAILED", encoderError.Trim());

        await RemuxToMp4Async(configuration!.WorkMediaPath, configuration.FinalMediaPath, cancellationToken)
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

    private async Task RemuxToMp4Async(string workPath, string finalPath, CancellationToken cancellationToken)
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
        if (audioPipe is not null)
        {
            await audioPipe.DisposeAsync().ConfigureAwait(false);
            audioPipe = null;
        }
    }

    private async Task WriteSilenceAsync(long sampleCount, CancellationToken cancellationToken)
    {
        var channels = configuration!.AudioFormat.Channels;
        var chunkSamples = (long)configuration.AudioFormat.SampleRate;
        var silence = new byte[checked((int)(chunkSamples * channels * sizeof(float)))];
        while (sampleCount > 0)
        {
            var samples = Math.Min(sampleCount, chunkSamples);
            var bytes = silence.AsMemory(0, checked((int)(samples * channels * sizeof(float))));
            await audioPipe!.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            sampleCount -= samples;
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
        if (configuration is null || encoder is null || videoPipe is null || audioPipe is null)
            throw new InvalidOperationException("The media writer has not been initialized.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        try
        {
            await CloseInputPipesAsync().ConfigureAwait(false);
            if (encoder is { HasExited: false })
                encoder.Kill(entireProcessTree: true);
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
        }
    }
}
