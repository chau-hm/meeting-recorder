using MeetingEvidenceRecorder.Application.Recording;
using MeetingEvidenceRecorder.Core.Abstractions;
using MeetingEvidenceRecorder.Infrastructure.Media;
using MeetingEvidenceRecorder.Infrastructure.Recording;
using MeetingEvidenceRecorder.Platform.MacOS.Capture;

return await MacSmokeProgram.RunAsync(args);

internal static class MacSmokeProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("MacSmoke requires macOS.");
            return 2;
        }

        var command = args.Length > 0 ? args[0] : "";
        if (command is "--help" or "-h" or "")
        {
            PrintUsage();
            return command == "" ? 2 : 0;
        }

        MacScreenCaptureBackend capture;
        try
        {
            capture = new MacScreenCaptureBackend();
        }
        catch (PlatformNotSupportedException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }
        catch (DllNotFoundException exception)
        {
            Console.Error.WriteLine($"CAPTURE_SCREEN_UNAVAILABLE: {exception.Message}");
            return 3;
        }
        await using var captureLifetime = capture;
        if (command == "--list-displays")
        {
            try
            {
                return await ListDisplaysAsync(capture).ConfigureAwait(false);
            }
            catch (RecorderException exception)
            {
                PrintRecorderException(exception);
                return 3;
            }
        }
        if (command != "--record")
        {
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintUsage();
            return 2;
        }

        var displayId = GetOption(args, "--display");
        var outputDirectory = GetOption(args, "--output");
        var durationText = GetOption(args, "--duration");
        string ffmpegPath;
        string ffprobePath;
        try
        {
            ffmpegPath = GetConfiguredExecutable(
                GetOption(args, "--ffmpeg"),
                "MEETING_RECORDER_FFMPEG",
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg");
            ffprobePath = GetConfiguredExecutable(
                GetOption(args, "--ffprobe"),
                "MEETING_RECORDER_FFPROBE",
                "/opt/homebrew/bin/ffprobe",
                "/usr/local/bin/ffprobe");
        }
        catch (FileNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        if (displayId is null || outputDirectory is null || durationText is null ||
            !double.TryParse(durationText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var durationSeconds) ||
            durationSeconds <= 0)
        {
            Console.Error.WriteLine("--display, --output and a positive --duration are required.");
            PrintUsage();
            return 2;
        }

        var permission = await EnsurePermissionAsync(capture).ConfigureAwait(false);
        if (permission != PermissionStatus.Granted)
            return 3;

        IReadOnlyList<MeetingEvidenceRecorder.Core.Capture.CaptureSource> sources;
        try
        {
            sources = await capture.GetSourcesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (RecorderException exception)
        {
            PrintRecorderException(exception);
            return 3;
        }
        var source = sources.SingleOrDefault(item => item.Id == displayId);
        if (source is null)
        {
            Console.Error.WriteLine($"Display '{displayId}' was not found.");
            PrintSources(sources);
            return 2;
        }

        await using var coordinator = new RecordingSessionCoordinator(
            capture,
            new FfmpegMediaWriter(ffmpegPath, ffprobePath),
            new FileRecordingBundleStoreFactory(ffprobePath));

        Console.WriteLine($"Selected {source.Name} ({source.Width}x{source.Height})");
        Console.WriteLine("Starting ScreenCaptureKit display + system-audio recording...");
        try
        {
            await coordinator.StartAsync(
                new RecordingSessionOptions(
                    outputDirectory,
                    source,
                    new(30, ShowCursor: true)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (RecorderException exception)
        {
            Console.Error.WriteLine($"{exception.Error.Code}: {exception.Error.UserMessage}");
            Console.Error.WriteLine(exception.Error.DiagnosticMessage);
            return 4;
        }

        Console.WriteLine("Recording: system audio ON, microphone OFF.");
        Console.WriteLine($"Recording for {durationSeconds:0.###} seconds. Press Ctrl+C to stop early.");
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var requestedDuration = Task.Delay(TimeSpan.FromSeconds(durationSeconds), stop.Token);
            var terminalCompletion = coordinator.Completion;
            var firstTerminal = await Task.WhenAny(
                requestedDuration,
                terminalCompletion).ConfigureAwait(false);
            if (firstTerminal == terminalCompletion)
            {
                var failureCompletion = await terminalCompletion.ConfigureAwait(false);
                PrintCompletion(failureCompletion);
                return failureCompletion.State == RecordingState.Completed ? 0 : 5;
            }

            await requestedDuration.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        Console.WriteLine("Stopping and validating media...");
        var completion = await coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
        PrintCompletion(completion);
        return completion.State == RecordingState.Completed ? 0 : 5;
    }

    private static void PrintCompletion(RecordingCompletion completion)
    {
        Console.WriteLine($"State: {completion.State}");
        Console.WriteLine($"Bundle: {completion.BundlePath}");
        if (completion.Error is RecorderError error)
        {
            Console.Error.WriteLine($"{error.Code}: {error.UserMessage}");
            Console.Error.WriteLine(error.DiagnosticMessage);
        }
        if (completion.Diagnostics.Count > 0)
            foreach (var diagnostic in completion.Diagnostics)
                Console.WriteLine($"Diagnostic: {diagnostic}");
    }

    private static async Task<int> ListDisplaysAsync(MacScreenCaptureBackend capture)
    {
        var permission = await EnsurePermissionAsync(capture).ConfigureAwait(false);
        if (permission != PermissionStatus.Granted)
            return 3;

        var sources = await capture.GetSourcesAsync(CancellationToken.None).ConfigureAwait(false);
        PrintSources(sources);
        return 0;
    }

    private static async Task<PermissionStatus> EnsurePermissionAsync(MacScreenCaptureBackend capture)
    {
        var status = await capture.GetScreenCaptureStatusAsync(CancellationToken.None).ConfigureAwait(false);
        if (status == PermissionStatus.NotDetermined)
        {
            Console.WriteLine("Screen Recording permission is not determined; requesting it now.");
            status = await capture.RequestScreenCaptureAccessAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (status != PermissionStatus.Granted)
        {
            var code = status is PermissionStatus.Unsupported or PermissionStatus.Unavailable
                ? "CAPTURE_SCREEN_UNAVAILABLE"
                : "CAPTURE_SCREEN_PERMISSION_DENIED";
            Console.Error.WriteLine(
                $"{code}: grant Screen Recording permission in System Settings and retry.");
        }
        return status;
    }

    private static void PrintRecorderException(RecorderException exception)
    {
        Console.Error.WriteLine($"{exception.Error.Code}: {exception.Error.UserMessage}");
        Console.Error.WriteLine(exception.Error.DiagnosticMessage);
    }

    private static void PrintSources(IReadOnlyList<MeetingEvidenceRecorder.Core.Capture.CaptureSource> sources)
    {
        if (sources.Count == 0)
        {
            Console.WriteLine("No capturable displays were found.");
            return;
        }

        foreach (var source in sources)
            Console.WriteLine($"{source.Id}\t{source.Name}\t{source.Width}x{source.Height}");
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string FindExecutable(string environmentVariable, params string[] candidates)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        var candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate is null)
            throw new FileNotFoundException($"Set {environmentVariable} or pass the executable path explicitly.");
        return candidate;
    }

    private static string GetConfiguredExecutable(
        string? commandLinePath,
        string environmentVariable,
        params string[] candidates) =>
        commandLinePath is null
            ? FindExecutable(environmentVariable, candidates)
            : !Path.IsPathRooted(commandLinePath) || !File.Exists(commandLinePath)
                ? throw new FileNotFoundException($"The configured executable path does not exist: {commandLinePath}")
                : commandLinePath;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            MacSmoke --list-displays
            MacSmoke --record --display <id> --output <directory> --duration <seconds>
                     [--ffmpeg <absolute-path>] [--ffprobe <absolute-path>]
            """);
    }
}
