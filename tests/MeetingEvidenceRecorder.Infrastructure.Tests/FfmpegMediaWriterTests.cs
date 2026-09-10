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

            var finalized = await writer.FinalizeAsync(CancellationToken.None);
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
            Assert.InRange(probe.Duration.TotalSeconds, 0.8, 1.2);
            Assert.DoesNotContain(diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
