using System.Diagnostics;
using MeetingEvidenceRecorder.Core.Abstractions;
using MeetingEvidenceRecorder.Core.Capture;
using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Core.Recording;
using MeetingEvidenceRecorder.Infrastructure.Media;
using Xunit.Sdk;

namespace MeetingEvidenceRecorder.Infrastructure.Tests;

public sealed class FfmpegMediaWriterTests
{
    [Fact]
    public async Task WriterProducesPlayableMp4WithVideoAndAudio()
    {
        if (!OperatingSystem.IsMacOS())
            throw SkipException.ForSkip("The FIFO media writer is macOS-specific.");

        var ffmpegPath = FindExecutable("MEETING_RECORDER_FFMPEG", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg");
        var ffprobePath = FindExecutable("MEETING_RECORDER_FFPROBE", "/opt/homebrew/bin/ffprobe", "/usr/local/bin/ffprobe");
        var root = Path.Combine(Path.GetTempPath(), $"meeting-recorder-media-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workDirectory = Path.Combine(root, ".work");
            var finalPath = Path.Combine(root, "recording.mp4");
            await using var writer = new FfmpegMediaWriter(ffmpegPath, ffprobePath);
            await writer.InitializeAsync(
                new MediaWriterConfiguration(
                    workDirectory,
                    Path.Combine(workDirectory, "recording.partial.mkv"),
                    finalPath,
                    new VideoFormat(2, 2, 30),
                    new AudioFormat(48000, 2)),
                CancellationToken.None);

            for (var frameIndex = 0; frameIndex < 30; frameIndex++)
            {
                var frame = new byte[16];
                frame.AsSpan().Fill((byte)(frameIndex % byte.MaxValue));
                await writer.WriteVideoAsync(
                    new TimedVideoFrame(
                        new VideoFrame(
                            frameIndex,
                            new NativeTimestamp(frameIndex, 30),
                            frame,
                            new VideoFormat(2, 2, 30)),
                        TimeSpan.FromSeconds(frameIndex / 30d)),
                    CancellationToken.None);
            }

            var samples = new float[48000 * 2];
            for (var sample = 0; sample < 48000; sample++)
            {
                var value = (float)(Math.Sin(sample * Math.PI * 440 / 48000) * 0.2);
                samples[sample * 2] = value;
                samples[sample * 2 + 1] = value;
            }

            await writer.WriteAudioAsync(
                new TimedAudioFrame(
                    new AudioFrame(
                        AudioSourceKind.System,
                        new NativeTimestamp(0, 1),
                        48000,
                        samples,
                        new AudioFormat(48000, 2)),
                    TimeSpan.Zero),
                CancellationToken.None);

            var finalized = await writer.FinalizeAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            var probe = await new FfmpegMediaProbe(ffprobePath).ProbeAsync(finalPath, CancellationToken.None);
            var candidate = new SessionManifest
            {
                SchemaVersion = "1.0",
                SessionId = Guid.NewGuid().ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
                DurationMs = Math.Max(1, (long)Math.Round(finalized.Duration.TotalMilliseconds)),
                Status = SessionStatus.Finalizing,
                Recording = new RecordingMetadata
                {
                    File = "recording.mp4",
                    Video = new VideoMetadata
                    {
                        SourceType = "display",
                        Width = finalized.VideoFormat.Width,
                        Height = finalized.VideoFormat.Height,
                        Fps = finalized.VideoFormat.FramesPerSecond
                    },
                    Audio = new AudioMetadata
                    {
                        SystemAudio = true,
                        Microphone = false,
                        MicrophoneDevice = null,
                        OutputMode = "system_audio_only",
                        SampleRate = finalized.AudioFormat!.SampleRate,
                        SystemAudioCaptureMode = "os_native",
                        SynchronizedToRecordingTimeline = true
                    }
                },
                EventsFile = "events.jsonl",
                Application = new ApplicationMetadata { Name = "test", Version = "test" },
                Platform = new PlatformMetadata { Os = "macOS", Architecture = "arm64" }
            };
            var diagnostics = new FfmpegCompletionMediaValidator(
                new FfmpegMediaProbe(ffprobePath)).Validate(finalPath, candidate);

            Assert.Equal(finalPath, finalized.Path);
            Assert.True(File.Exists(finalPath));
            Assert.True(probe.HasVideo);
            Assert.True(probe.HasAudio);
            Assert.Equal(2, probe.VideoWidth);
            Assert.Equal(2, probe.VideoHeight);
            Assert.Equal(48000, probe.AudioSampleRate);
            Assert.Equal("h264", probe.VideoCodecName);
            Assert.Equal("aac", probe.AudioCodecName);
            Assert.InRange(probe.VideoDuration!.Value.TotalSeconds, 0.8, 1.2);
            Assert.InRange(probe.AudioDuration!.Value.TotalSeconds, 0.8, 1.2);
            Assert.InRange(probe.Duration.TotalSeconds, 0.8, 1.2);
            Assert.DoesNotContain(diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriterExtendsTheLastVideoFrameToTheCanonicalRecordingEnd()
    {
        if (!OperatingSystem.IsMacOS())
            throw SkipException.ForSkip("The FIFO media writer is macOS-specific.");

        var ffmpegPath = FindExecutable("MEETING_RECORDER_FFMPEG", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg");
        var ffprobePath = FindExecutable("MEETING_RECORDER_FFPROBE", "/opt/homebrew/bin/ffprobe", "/usr/local/bin/ffprobe");
        var root = Path.Combine(Path.GetTempPath(), $"meeting-recorder-static-video-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workDirectory = Path.Combine(root, ".work");
            var finalPath = Path.Combine(root, "recording.mp4");
            await using var writer = new FfmpegMediaWriter(ffmpegPath, ffprobePath);
            await writer.InitializeAsync(
                new MediaWriterConfiguration(
                    workDirectory,
                    Path.Combine(workDirectory, "recording.partial.mkv"),
                    finalPath,
                    new VideoFormat(2, 2, 30),
                    new AudioFormat(48000, 2)),
                CancellationToken.None);

            var staticFrame = new byte[16];
            staticFrame.AsSpan().Fill(0x7f);
            await writer.WriteVideoAsync(
                new TimedVideoFrame(
                    new VideoFrame(
                        0,
                        new NativeTimestamp(0, 1),
                        staticFrame,
                        new VideoFormat(2, 2, 30)),
                    TimeSpan.Zero),
                CancellationToken.None);

            for (var second = 0; second < 5; second++)
            {
                await writer.WriteAudioAsync(
                    new TimedAudioFrame(
                        new AudioFrame(
                            AudioSourceKind.System,
                            new NativeTimestamp(second, 1),
                            48000,
                            new float[48000 * 2],
                            new AudioFormat(48000, 2)),
                        TimeSpan.FromSeconds(second)),
                    CancellationToken.None);
            }

            var finalized = await writer.FinalizeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            var probe = await new FfmpegMediaProbe(ffprobePath).ProbeAsync(finalPath, CancellationToken.None);
            var diagnostics = new FfmpegCompletionMediaValidator(
                new FfmpegMediaProbe(ffprobePath)).Validate(finalPath, CreateCandidate(finalized));

            Assert.InRange(probe.VideoDuration!.Value.TotalSeconds, 4.8, 5.2);
            Assert.InRange(probe.AudioDuration!.Value.TotalSeconds, 4.8, 5.2);
            Assert.InRange(probe.Duration.TotalSeconds, 4.8, 5.2);
            Assert.DoesNotContain(diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriterKeepsAudioTransportFlowingDuringLongStaticVideo()
    {
        if (!OperatingSystem.IsMacOS())
            throw SkipException.ForSkip("The FIFO media writer is macOS-specific.");

        var ffmpegPath = FindExecutable("MEETING_RECORDER_FFMPEG", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg");
        var ffprobePath = FindExecutable("MEETING_RECORDER_FFPROBE", "/opt/homebrew/bin/ffprobe", "/usr/local/bin/ffprobe");
        var root = Path.Combine(Path.GetTempPath(), $"meeting-recorder-static-audio-stress-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var format = new VideoFormat(2, 2, 30);
            var workDirectory = Path.Combine(root, ".work");
            var finalPath = Path.Combine(root, "recording.mp4");
            await using var writer = new FfmpegMediaWriter(ffmpegPath, ffprobePath);
            await writer.InitializeAsync(
                new MediaWriterConfiguration(
                    workDirectory,
                    Path.Combine(workDirectory, "recording.partial.mkv"),
                    finalPath,
                    format,
                    new AudioFormat(48000, 2)),
                CancellationToken.None);

            await writer.WriteVideoAsync(CreateVideoFrame(0, TimeSpan.Zero, format, 127, 127, 127), CancellationToken.None);
            var audioSamples = new float[960 * 2];
            for (var audioIndex = 0; audioIndex < 2000; audioIndex++)
            {
                var clockElapsed = TimeSpan.FromMilliseconds((audioIndex + 1) * 20);
                await writer.AdvanceVideoWatermarkAsync(
                    RecordingTimelinePolicy.GetSafeVideoCommitTime(clockElapsed),
                    CancellationToken.None);
                await WriteAudioChunkAsync(writer, audioIndex, audioSamples);
                await Task.Yield();
            }

            var finalized = await writer.FinalizeAsync(TimeSpan.FromSeconds(40), CancellationToken.None);
            var probe = await new FfmpegMediaProbe(ffprobePath).ProbeAsync(finalPath, CancellationToken.None);

            Assert.InRange(finalized.Duration.TotalSeconds, 39.5, 40.5);
            Assert.InRange(probe.VideoDuration!.Value.TotalSeconds, 39.5, 40.5);
            Assert.InRange(probe.AudioDuration!.Value.TotalSeconds, 39.5, 40.5);
            Assert.InRange(probe.Duration.TotalSeconds, 39.5, 40.5);
            Assert.InRange(writer.MaxAudioTransportQueueDepth, 1, 1024 + 2);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriterPreservesRealVideoAfterLongStaticInterval()
    {
        if (!OperatingSystem.IsMacOS())
            throw SkipException.ForSkip("The FIFO media writer is macOS-specific.");

        var ffmpegPath = FindExecutable("MEETING_RECORDER_FFMPEG", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg");
        var ffprobePath = FindExecutable("MEETING_RECORDER_FFPROBE", "/opt/homebrew/bin/ffprobe", "/usr/local/bin/ffprobe");
        var root = Path.Combine(Path.GetTempPath(), $"meeting-recorder-static-motion-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var format = new VideoFormat(16, 16, 30);
            var workDirectory = Path.Combine(root, ".work");
            var finalPath = Path.Combine(root, "recording.mp4");
            await using var writer = new FfmpegMediaWriter(ffmpegPath, ffprobePath);
            await writer.InitializeAsync(
                new MediaWriterConfiguration(
                    workDirectory,
                    Path.Combine(workDirectory, "recording.partial.mkv"),
                    finalPath,
                    format,
                    new AudioFormat(48000, 2)),
                CancellationToken.None);

            await writer.WriteVideoAsync(CreateVideoFrame(0, TimeSpan.Zero, format, 0, 0, 0), CancellationToken.None);
            var audioSamples = new float[960 * 2];
            for (var audioIndex = 0; audioIndex < 250; audioIndex++)
            {
                var clockElapsed = TimeSpan.FromMilliseconds((audioIndex + 1) * 20);
                await writer.AdvanceVideoWatermarkAsync(
                    RecordingTimelinePolicy.GetSafeVideoCommitTime(clockElapsed),
                    CancellationToken.None);
                await WriteAudioChunkAsync(writer, audioIndex, audioSamples);
                await Task.Yield();
            }

            await writer.WriteVideoAsync(
                CreateVideoFrame(1, TimeSpan.FromSeconds(5), format, 255, 0, 0),
                CancellationToken.None);
            for (var audioIndex = 250; audioIndex < 300; audioIndex++)
            {
                var clockElapsed = TimeSpan.FromMilliseconds((audioIndex + 1) * 20);
                await writer.AdvanceVideoWatermarkAsync(
                    RecordingTimelinePolicy.GetSafeVideoCommitTime(clockElapsed),
                    CancellationToken.None);
                await WriteAudioChunkAsync(writer, audioIndex, audioSamples);
                await Task.Yield();
            }

            _ = await writer.FinalizeAsync(TimeSpan.FromSeconds(6), CancellationToken.None);
            var decoded = await DecodeVideoFramesAsync(ffmpegPath, finalPath);
            var frameSize = format.Width * format.Height * 4;

            Assert.True(decoded.Length >= frameSize * 180);
            AssertAllChannelsBelow(decoded.AsSpan(0 * frameSize, frameSize), 40);
            AssertAllChannelsBelow(decoded.AsSpan(120 * frameSize, frameSize), 40);
            AssertDominantChannel(decoded.AsSpan(165 * frameSize, frameSize), 0);
            Assert.Equal(0, writer.LateVideoFrameCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriterDoesNotLetAStreamTimestampExtendPastTheCanonicalRecordingEnd()
    {
        if (!OperatingSystem.IsMacOS())
            throw SkipException.ForSkip("The FIFO media writer is macOS-specific.");

        var ffmpegPath = FindExecutable("MEETING_RECORDER_FFMPEG", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg");
        var ffprobePath = FindExecutable("MEETING_RECORDER_FFPROBE", "/opt/homebrew/bin/ffprobe", "/usr/local/bin/ffprobe");
        var root = Path.Combine(Path.GetTempPath(), $"meeting-recorder-canonical-end-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var format = new VideoFormat(2, 2, 30);
            var workDirectory = Path.Combine(root, ".work");
            var finalPath = Path.Combine(root, "recording.mp4");
            await using var writer = new FfmpegMediaWriter(ffmpegPath, ffprobePath);
            await writer.InitializeAsync(
                new MediaWriterConfiguration(
                    workDirectory,
                    Path.Combine(workDirectory, "recording.partial.mkv"),
                    finalPath,
                    format,
                    new AudioFormat(48000, 2)),
                CancellationToken.None);

            await writer.WriteVideoAsync(CreateVideoFrame(0, TimeSpan.Zero, format, 0, 0, 0), CancellationToken.None);
            await writer.WriteVideoAsync(CreateVideoFrame(1, TimeSpan.FromSeconds(1.2), format, 255, 0, 0), CancellationToken.None);
            await writer.WriteAudioAsync(
                new TimedAudioFrame(
                    new AudioFrame(
                        AudioSourceKind.System,
                        new NativeTimestamp(0, 1),
                        48000,
                        new float[48000 * 2],
                        new AudioFormat(48000, 2)),
                    TimeSpan.Zero),
                CancellationToken.None);

            _ = await writer.FinalizeAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            var probe = await new FfmpegMediaProbe(ffprobePath).ProbeAsync(finalPath, CancellationToken.None);

            Assert.InRange(probe.VideoDuration!.Value.TotalSeconds, 0.8, 1.1);
            Assert.InRange(probe.AudioDuration!.Value.TotalSeconds, 0.8, 1.1);
            Assert.InRange(probe.Duration.TotalSeconds, 0.8, 1.1);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriterPreservesRealVideoFramesWhenAudioArrivesBetweenFrames()
    {
        if (!OperatingSystem.IsMacOS())
            throw SkipException.ForSkip("The FIFO media writer is macOS-specific.");

        var ffmpegPath = FindExecutable("MEETING_RECORDER_FFMPEG", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg");
        var ffprobePath = FindExecutable("MEETING_RECORDER_FFPROBE", "/opt/homebrew/bin/ffprobe", "/usr/local/bin/ffprobe");
        var root = Path.Combine(Path.GetTempPath(), $"meeting-recorder-interleaved-media-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var format = new VideoFormat(16, 16, 30);
            var workDirectory = Path.Combine(root, ".work");
            var finalPath = Path.Combine(root, "recording.mp4");
            await using var writer = new FfmpegMediaWriter(ffmpegPath, ffprobePath);
            await writer.InitializeAsync(
                new MediaWriterConfiguration(
                    workDirectory,
                    Path.Combine(workDirectory, "recording.partial.mkv"),
                    finalPath,
                    format,
                    new AudioFormat(48000, 2)),
                CancellationToken.None);

            await writer.WriteVideoAsync(CreateVideoFrame(0, TimeSpan.Zero, format, 0, 0, 0), CancellationToken.None);
            await WriteAudioChunkAsync(writer, 0);
            await writer.WriteVideoAsync(CreateVideoFrame(1, TimeSpan.FromMilliseconds(33), format, 255, 0, 0), CancellationToken.None);
            await WriteAudioChunkAsync(writer, 1);
            await writer.WriteVideoAsync(CreateVideoFrame(2, TimeSpan.FromMilliseconds(67), format, 0, 255, 0), CancellationToken.None);
            await WriteAudioChunkAsync(writer, 2);
            await writer.WriteVideoAsync(CreateVideoFrame(3, TimeSpan.FromMilliseconds(100), format, 0, 0, 255), CancellationToken.None);
            for (var audioIndex = 3; audioIndex < 7; audioIndex++)
                await WriteAudioChunkAsync(writer, audioIndex);

            _ = await writer.FinalizeAsync(TimeSpan.FromMilliseconds(140), CancellationToken.None);
            var decoded = await DecodeVideoFramesAsync(ffmpegPath, finalPath);
            var frameSize = format.Width * format.Height * 4;

            Assert.True(decoded.Length >= frameSize * 4);
            AssertAllChannelsBelow(decoded.AsSpan(0 * frameSize, frameSize), 40);
            AssertDominantChannel(decoded.AsSpan(1 * frameSize, frameSize), 0);
            AssertDominantChannel(decoded.AsSpan(2 * frameSize, frameSize), 1);
            AssertDominantChannel(decoded.AsSpan(3 * frameSize, frameSize), 2);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompletionValidatorRejectsMateriallyTruncatedVideoStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meeting-recorder-truncated-media-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, [1]);
        try
        {
            var probe = new StubMediaProbe(new MediaProbeResult(
                TimeSpan.FromSeconds(5),
                2,
                2,
                30,
                48000,
                "h264",
                "aac",
                HasVideo: true,
                HasAudio: true,
                VideoStartTime: TimeSpan.Zero,
                VideoDuration: TimeSpan.FromMilliseconds(100),
                VideoEndTime: TimeSpan.FromMilliseconds(100),
                AudioStartTime: TimeSpan.Zero,
                AudioDuration: TimeSpan.FromSeconds(5),
                AudioEndTime: TimeSpan.FromSeconds(5)));

            var diagnostics = new FfmpegCompletionMediaValidator(probe).Validate(
                path,
                CreateCandidate(new FinalizedMedia(
                    path,
                    TimeSpan.FromSeconds(5),
                    new VideoFormat(2, 2, 30),
                    new AudioFormat(48000, 2),
                    HasVideo: true,
                    HasAudio: true)));

            Assert.Contains(diagnostics, item => item.Code == "BUNDLE_VIDEO_COVERAGE_INVALID");
            Assert.DoesNotContain(diagnostics, item => item.Code == "BUNDLE_AUDIO_COVERAGE_INVALID");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompletionValidatorRejectsMateriallyTruncatedAudioStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meeting-recorder-truncated-audio-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, [1]);
        try
        {
            var probe = new StubMediaProbe(new MediaProbeResult(
                TimeSpan.FromSeconds(5),
                2,
                2,
                30,
                48000,
                "h264",
                "aac",
                HasVideo: true,
                HasAudio: true,
                VideoStartTime: TimeSpan.Zero,
                VideoDuration: TimeSpan.FromSeconds(5),
                VideoEndTime: TimeSpan.FromSeconds(5),
                AudioStartTime: TimeSpan.Zero,
                AudioDuration: TimeSpan.FromMilliseconds(100),
                AudioEndTime: TimeSpan.FromMilliseconds(100)));

            var diagnostics = new FfmpegCompletionMediaValidator(probe).Validate(
                path,
                CreateCandidate(new FinalizedMedia(
                    path,
                    TimeSpan.FromSeconds(5),
                    new VideoFormat(2, 2, 30),
                    new AudioFormat(48000, 2),
                    HasVideo: true,
                    HasAudio: true)));

            Assert.Contains(diagnostics, item => item.Code == "BUNDLE_AUDIO_COVERAGE_INVALID");
            Assert.DoesNotContain(diagnostics, item => item.Code == "BUNDLE_VIDEO_COVERAGE_INVALID");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static TimedVideoFrame CreateVideoFrame(
        long sequence,
        TimeSpan timestamp,
        VideoFormat format,
        byte blue,
        byte green,
        byte red)
    {
        var data = new byte[format.Width * format.Height * 4];
        for (var offset = 0; offset < data.Length; offset += 4)
        {
            data[offset] = blue;
            data[offset + 1] = green;
            data[offset + 2] = red;
            data[offset + 3] = byte.MaxValue;
        }

        return new TimedVideoFrame(
            new VideoFrame(sequence, new NativeTimestamp(sequence, 30), data, format),
            timestamp);
    }

    private static async Task WriteAudioChunkAsync(
        FfmpegMediaWriter writer,
        int chunkIndex,
        float[]? samples = null)
    {
        const int sampleCount = 960;
        samples ??= new float[sampleCount * 2];
        await writer.WriteAudioAsync(
            new TimedAudioFrame(
                new AudioFrame(
                    AudioSourceKind.System,
                    new NativeTimestamp(chunkIndex * 20, 1000),
                    sampleCount,
                    samples,
                    new AudioFormat(48000, 2)),
                TimeSpan.FromMilliseconds(chunkIndex * 20)),
            CancellationToken.None);
    }

    private static async Task<byte[]> DecodeVideoFramesAsync(string ffmpegPath, string mediaPath)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(mediaPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);
        return output.ToArray();
    }

    private static void AssertAllChannelsBelow(ReadOnlySpan<byte> frame, byte maximum)
    {
        for (var offset = 0; offset < frame.Length; offset += 4)
        {
            Assert.True(frame[offset] <= maximum);
            Assert.True(frame[offset + 1] <= maximum);
            Assert.True(frame[offset + 2] <= maximum);
        }
    }

    private static void AssertDominantChannel(ReadOnlySpan<byte> frame, int channel)
    {
        var averages = new double[3];
        var pixels = frame.Length / 4;
        for (var offset = 0; offset < frame.Length; offset += 4)
        {
            averages[0] += frame[offset];
            averages[1] += frame[offset + 1];
            averages[2] += frame[offset + 2];
        }

        averages = averages.Select(value => value / pixels).ToArray();
        var otherMaximum = averages.Where((_, index) => index != channel).Max();
        Assert.True(averages[channel] > otherMaximum + 30);
    }

    private static SessionManifest CreateCandidate(FinalizedMedia finalized) =>
        new()
        {
            SchemaVersion = "1.0",
            SessionId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            DurationMs = Math.Max(1, (long)Math.Round(finalized.Duration.TotalMilliseconds)),
            Status = SessionStatus.Finalizing,
            Recording = new RecordingMetadata
            {
                File = "recording.mp4",
                Video = new VideoMetadata
                {
                    SourceType = "display",
                    Width = finalized.VideoFormat.Width,
                    Height = finalized.VideoFormat.Height,
                    Fps = finalized.VideoFormat.FramesPerSecond
                },
                Audio = new AudioMetadata
                {
                    SystemAudio = true,
                    Microphone = false,
                    MicrophoneDevice = null,
                    OutputMode = "system_audio_only",
                    SampleRate = finalized.AudioFormat!.SampleRate,
                    SystemAudioCaptureMode = "os_native",
                    SynchronizedToRecordingTimeline = true
                }
            },
            EventsFile = "events.jsonl",
            Application = new ApplicationMetadata { Name = "test", Version = "test" },
            Platform = new PlatformMetadata { Os = "macOS", Architecture = "arm64" }
        };

    private sealed class StubMediaProbe(MediaProbeResult result) : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private static string FindExecutable(string environmentVariable, params string[] candidates)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        var candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate is null)
            throw SkipException.ForSkip($"Set {environmentVariable} or install the expected executable.");
        return candidate;
    }
}
