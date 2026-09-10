using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MeetingEvidenceRecorder.Infrastructure.Media;

public sealed record MediaProbeResult(
    TimeSpan Duration,
    int? VideoWidth,
    int? VideoHeight,
    double? VideoFramesPerSecond,
    int? AudioSampleRate,
    string? VideoCodecName,
    string? AudioCodecName,
    bool HasVideo,
    bool HasAudio,
    TimeSpan? VideoStartTime,
    TimeSpan? VideoDuration,
    TimeSpan? VideoEndTime,
    TimeSpan? AudioStartTime,
    TimeSpan? AudioDuration,
    TimeSpan? AudioEndTime);

public sealed class MediaProbeException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public interface IMediaProbe
{
    Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken);
}

public sealed class FfmpegMediaProbe : IMediaProbe
{
    private readonly string executablePath;

    public FfmpegMediaProbe(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("An ffprobe executable path is required.", nameof(executablePath));
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("The configured ffprobe executable does not exist.", executablePath);
        this.executablePath = executablePath;
    }

    public async Task<MediaProbeResult> ProbeAsync(string mediaPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(mediaPath))
            throw new MediaProbeException($"Media file does not exist: {mediaPath}");

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(mediaPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new MediaProbeException("ffprobe did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new MediaProbeException("The configured ffprobe executable could not be started.", ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new MediaProbeException($"ffprobe failed with exit code {process.ExitCode}: {error.Trim()}");

        try
        {
            using var document = JsonDocument.Parse(output);
            return Parse(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new MediaProbeException("ffprobe returned invalid JSON.", ex);
        }
    }

    private static MediaProbeResult Parse(JsonElement root)
    {
        var hasVideo = false;
        var hasAudio = false;
        int? width = null;
        int? height = null;
        double? fps = null;
        int? sampleRate = null;
        string? videoCodec = null;
        string? audioCodec = null;
        TimeSpan? videoStart = null;
        TimeSpan? videoDuration = null;
        TimeSpan? audioStart = null;
        TimeSpan? audioDuration = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecType) || codecType.ValueKind != JsonValueKind.String)
                    continue;

                if (codecType.GetString() == "video" && !hasVideo)
                {
                    hasVideo = true;
                    width = ReadPositiveInt(stream, "width");
                    height = ReadPositiveInt(stream, "height");
                    fps = ReadRate(stream, "avg_frame_rate") ?? ReadRate(stream, "r_frame_rate");
                    videoCodec = ReadString(stream, "codec_name");
                    videoStart = ReadTime(stream, "start_time");
                    videoDuration = ReadTime(stream, "duration");
                }
                else if (codecType.GetString() == "audio" && !hasAudio)
                {
                    hasAudio = true;
                    sampleRate = ReadPositiveInt(stream, "sample_rate");
                    audioCodec = ReadString(stream, "codec_name");
                    audioStart = ReadTime(stream, "start_time");
                    audioDuration = ReadTime(stream, "duration");
                }
            }
        }

        var durationSeconds = 0d;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var duration) &&
            duration.ValueKind == JsonValueKind.String)
        {
            _ = double.TryParse(
                duration.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out durationSeconds);
        }

        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new MediaProbeException("ffprobe did not report a positive media duration.");

        return new MediaProbeResult(
            TimeSpan.FromSeconds(durationSeconds),
            width,
            height,
            fps,
            sampleRate,
            videoCodec,
            audioCodec,
            hasVideo,
            hasAudio,
            videoStart,
            videoDuration,
            AddIfKnown(videoStart, videoDuration),
            audioStart,
            audioDuration,
            AddIfKnown(audioStart, audioDuration));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadPositiveInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0)
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) &&
            number > 0)
            return number;
        return null;
    }

    private static double? ReadRate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var parts = value.GetString()!.Split('/');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            denominator == 0)
            return null;

        var rate = numerator / denominator;
        return double.IsFinite(rate) && rate > 0 ? rate : null;
    }

    private static TimeSpan? ReadTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds))
            return null;

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static TimeSpan? AddIfKnown(TimeSpan? start, TimeSpan? duration)
    {
        if (start is not TimeSpan startValue || duration is not TimeSpan durationValue)
            return null;

        try
        {
            return startValue + durationValue;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
