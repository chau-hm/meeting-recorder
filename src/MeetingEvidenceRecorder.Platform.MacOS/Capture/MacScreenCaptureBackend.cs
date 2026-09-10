using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using MeetingEvidenceRecorder.Core.Abstractions;
using MeetingEvidenceRecorder.Core.Capture;
using MeetingEvidenceRecorder.Core.Recording;

namespace MeetingEvidenceRecorder.Platform.MacOS.Capture;

public sealed class MacScreenCaptureBackend : IDisplaySystemAudioCaptureBackend
{
    private const int VideoQueueCapacity = 8;
    private const int AudioQueueCapacity = 32;
    private readonly NativeMethods.SampleCallback sampleCallback;
    private readonly NativeMethods.ErrorCallback errorCallback;
    private readonly IntPtr nativeHandle;
    private BoundedCaptureQueue<VideoFrame>? videoQueue;
    private BoundedCaptureQueue<AudioFrame>? audioQueue;
    private long sequence;
    private bool started;
    private bool disposed;
    private double framesPerSecond = 30;
    private readonly object timestampGate = new();
    private NativeTimestamp? sourceTimestampOrigin;

    public MacScreenCaptureBackend()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(15, 0))
            throw new PlatformNotSupportedException("ScreenCaptureKit display plus system-audio recording requires macOS 15.0 or later for this slice.");

        sampleCallback = OnSample;
        errorCallback = OnNativeError;
        nativeHandle = NativeMethods.Create(sampleCallback, errorCallback, IntPtr.Zero);
        if (nativeHandle == IntPtr.Zero)
            throw new InvalidOperationException("ScreenCaptureKit native shim could not be initialized.");
    }

    public event EventHandler<RecorderErrorEventArgs>? Error;

    public long DroppedVideoFrames => videoQueue?.DroppedCount ?? 0;

    public NativeTimestamp? SourceTimestampOrigin
    {
        get
        {
            lock (timestampGate)
                return sourceTimestampOrigin;
        }
    }

    public Task<PermissionStatus> GetScreenCaptureStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOSVersionAtLeast(13, 0))
            return Task.FromResult(PermissionStatus.Unsupported);

        var nativeStatus = NativeMethods.ScreenPermission(request: false);
        return Task.FromResult(nativeStatus switch
        {
            NativeMethods.Ok => PermissionStatus.Granted,
            NativeMethods.PermissionDenied or NativeMethods.Unavailable => PermissionStatus.NotDetermined,
            _ => PermissionStatus.Unavailable
        });
    }

    public Task<PermissionStatus> RequestScreenCaptureAccessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOSVersionAtLeast(13, 0))
            return Task.FromResult(PermissionStatus.Unsupported);

        var nativeStatus = NativeMethods.ScreenPermission(request: true);
        return Task.FromResult(nativeStatus switch
        {
            NativeMethods.Ok => PermissionStatus.Granted,
            NativeMethods.PermissionDenied => PermissionStatus.Denied,
            _ => PermissionStatus.Unavailable
        });
    }

    public Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        var capacity = 16;
        var size = Marshal.SizeOf<NativeMethods.DisplayInfo>();
        var buffer = Marshal.AllocHGlobal(size * capacity);
        var error = new StringBuilder(512);
        try
        {
            var count = NativeMethods.ListDisplays(buffer, capacity, error, error.Capacity);
            if (count < 0)
                throw CreateNativeException(-count, error.ToString());
            if (count > capacity)
                throw CreateException("CAPTURE_SCREEN_UNAVAILABLE", "Native display enumeration exceeded its bounded output capacity.");

            var sources = new List<CaptureSource>(count);
            for (var index = 0; index < count; index++)
            {
                var native = Marshal.PtrToStructure<NativeMethods.DisplayInfo>(buffer + size * index);
                sources.Add(new CaptureSource(
                    native.DisplayId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    native.Name,
                    CaptureSourceKind.Display,
                    checked((int)native.Width),
                    checked((int)native.Height),
                    native.DisplayId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            return Task.FromResult<IReadOnlyList<CaptureSource>>(sources);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public Task StartAsync(CaptureSource source, VideoCaptureOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        if (source.Kind != CaptureSourceKind.Display)
            throw new ArgumentException("macOS PR2 supports display sources only.", nameof(source));
        if (started)
            throw new InvalidOperationException("ScreenCaptureKit is already running.");

        videoQueue = new BoundedCaptureQueue<VideoFrame>("video", VideoQueueCapacity);
        audioQueue = new BoundedCaptureQueue<AudioFrame>("system-audio", AudioQueueCapacity);
        lock (timestampGate)
            sourceTimestampOrigin = null;
        framesPerSecond = options.FramesPerSecond;
        var error = new StringBuilder(512);
        var result = NativeMethods.Start(
            nativeHandle,
            uint.Parse(source.Id, System.Globalization.CultureInfo.InvariantCulture),
            checked((uint)source.Width),
            checked((uint)source.Height),
            options.FramesPerSecond,
            options.ShowCursor ? 1 : 0,
            error,
            error.Capacity);
        if (result != NativeMethods.Ok)
        {
            videoQueue.Complete();
            audioQueue.Complete();
            throw CreateNativeException(result, error.ToString());
        }

        started = true;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureQueues();
        await foreach (var frame in videoQueue!.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    public async IAsyncEnumerable<AudioFrame> ReadSystemAudioAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureQueues();
        await foreach (var frame in audioQueue!.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!started)
            return Task.CompletedTask;

        var error = new StringBuilder(512);
        var result = NativeMethods.Stop(nativeHandle, error, error.Capacity);
        started = false;
        videoQueue?.Complete();
        audioQueue?.Complete();
        if (result != NativeMethods.Ok)
            throw CreateNativeException(result, error.ToString());
        return Task.CompletedTask;
    }

    private void OnSample(
        int kind,
        long timestampValue,
        int timestampScale,
        long durationValue,
        int durationScale,
        uint sampleRate,
        IntPtr data,
        nuint dataSize,
        uint width,
        uint height,
        uint rowBytes,
        uint sampleCount,
        uint channels,
        IntPtr context)
    {
        try
        {
            if (data == IntPtr.Zero || dataSize == 0 || timestampScale <= 0)
                return;

            lock (timestampGate)
                sourceTimestampOrigin ??= new NativeTimestamp(timestampValue, timestampScale);

            if (kind == NativeMethods.VideoSample)
            {
                var bytes = new byte[checked((int)dataSize)];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                var frame = new VideoFrame(
                    Interlocked.Increment(ref sequence),
                    new NativeTimestamp(timestampValue, timestampScale),
                    bytes,
                    new VideoFormat(checked((int)width), checked((int)height), framesPerSecond));
                if (videoQueue?.TryEnqueue(frame, allowDrop: true) == QueueEnqueueResult.Dropped)
                {
                    // Drop accounting is exposed by the queue; capture callback remains non-blocking.
                }
            }
            else if (kind == NativeMethods.SystemAudioSample)
            {
                if (sampleRate == 0 || channels == 0)
                    throw new InvalidDataException("ScreenCaptureKit returned an audio buffer without a valid format.");

                var bytes = new byte[checked((int)dataSize)];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                if (bytes.Length % sizeof(float) != 0)
                    throw new InvalidDataException("ScreenCaptureKit returned audio data that is not float32-aligned.");
                var samples = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
                var audio = new AudioFrame(
                    AudioSourceKind.System,
                    new NativeTimestamp(timestampValue, timestampScale),
                    checked((int)sampleCount),
                    samples,
                    new AudioFormat(
                        checked((int)sampleRate),
                        checked((int)channels)));
                if (audioQueue?.TryEnqueue(audio, allowDrop: false) == QueueEnqueueResult.Closed)
                {
                    RaiseError(new RecorderError(
                        "AUDIO_SYSTEM_UNAVAILABLE",
                        RecorderErrorSeverity.Fatal,
                        "System-audio processing could not keep up; recording was stopped to protect audio integrity.",
                        "The bounded system-audio queue was full."));
                }
            }
        }
        catch (Exception exception)
        {
            RaiseError(new RecorderError(
                kind == NativeMethods.SystemAudioSample
                    ? "AUDIO_SYSTEM_UNAVAILABLE"
                    : "CAPTURE_SCREEN_UNAVAILABLE",
                RecorderErrorSeverity.Fatal,
                kind == NativeMethods.SystemAudioSample
                    ? "The native macOS system-audio callback could not be processed safely."
                    : "The native macOS capture callback could not be processed safely.",
                exception.Message));
        }
        finally
        {
            NativeMethods.ReleasePayload(data);
        }
    }

    private void OnNativeError(int code, IntPtr message, IntPtr context)
    {
        var text = message == IntPtr.Zero ? "ScreenCaptureKit reported an unspecified native error." : Marshal.PtrToStringUTF8(message);
        RaiseError(code switch
        {
            NativeMethods.PermissionDenied => new RecorderError(
                "CAPTURE_SCREEN_PERMISSION_DENIED",
                RecorderErrorSeverity.Fatal,
                "Screen Recording permission was denied.",
                text ?? "ScreenCaptureKit permission failure."),
            NativeMethods.SourceLost => new RecorderError(
                "CAPTURE_SOURCE_LOST",
                RecorderErrorSeverity.Fatal,
                "The selected display is no longer available.",
                text ?? "ScreenCaptureKit source became inactive."),
            NativeMethods.AudioUnavailable => new RecorderError(
                "AUDIO_SYSTEM_UNAVAILABLE",
                RecorderErrorSeverity.Fatal,
                "The macOS system-audio capture stream is unavailable.",
                text ?? "ScreenCaptureKit returned an unusable audio buffer."),
            _ => new RecorderError(
                "CAPTURE_SCREEN_UNAVAILABLE",
                RecorderErrorSeverity.Fatal,
                "The macOS display capture stream stopped unexpectedly.",
                text ?? "ScreenCaptureKit reported an error.")
        });
    }

    private void RaiseError(RecorderError error) => Error?.Invoke(this, new RecorderErrorEventArgs(error));

    private static RecorderException CreateException(string code, string detail) => new(new RecorderError(
        code,
        RecorderErrorSeverity.Fatal,
        code == "CAPTURE_SCREEN_PERMISSION_DENIED"
            ? "Screen Recording permission is required before recording can start."
            : "macOS display capture is unavailable.",
        detail));

    private static RecorderException CreateNativeException(int code, string detail) =>
        code == NativeMethods.PermissionDenied
            ? CreateException("CAPTURE_SCREEN_PERMISSION_DENIED", detail)
            : code == NativeMethods.SourceLost
                ? CreateException("CAPTURE_SOURCE_LOST", detail)
                : CreateException("CAPTURE_SCREEN_UNAVAILABLE", detail);

    private void EnsureQueues()
    {
        EnsureNotDisposed();
        if (videoQueue is null || audioQueue is null)
            throw new InvalidOperationException("ScreenCaptureKit has not been started.");
    }

    private void EnsureNotDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MacScreenCaptureBackend));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        if (started)
        {
            var error = new StringBuilder(512);
            _ = NativeMethods.Stop(nativeHandle, error, error.Capacity);
            started = false;
        }
        videoQueue?.Complete();
        audioQueue?.Complete();
        NativeMethods.Destroy(nativeHandle);
        await Task.CompletedTask;
    }

    private static class NativeMethods
    {
        private const string Library = "MeetingEvidenceRecorderMacCapture";
        public const int Ok = 0;
        public const int PermissionDenied = 1;
        public const int Unavailable = 2;
        public const int SourceLost = 3;
        public const int AudioUnavailable = 5;
        public const int VideoSample = 1;
        public const int SystemAudioSample = 2;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SampleCallback(
            int kind,
            long timestampValue,
            int timestampScale,
            long durationValue,
            int durationScale,
            uint sampleRate,
            IntPtr data,
            nuint dataSize,
            uint width,
            uint height,
            uint rowBytes,
            uint sampleCount,
            uint channels,
            IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ErrorCallback(int code, IntPtr message, IntPtr context);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DisplayInfo
        {
            public uint DisplayId;
            public uint Width;
            public uint Height;
            public double PointPixelScale;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Name;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mer_capture_create(SampleCallback sampleCallback, ErrorCallback errorCallback, IntPtr context);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mer_capture_screen_permission(int request);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mer_capture_list_displays(
            IntPtr displays,
            int capacity,
            StringBuilder errorMessage,
            int errorMessageSize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mer_capture_start(
            IntPtr handle,
            uint displayId,
            uint width,
            uint height,
            double framesPerSecond,
            int showCursor,
            StringBuilder errorMessage,
            int errorMessageSize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mer_capture_stop(IntPtr handle, StringBuilder errorMessage, int errorMessageSize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mer_capture_destroy(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mer_capture_release_payload(IntPtr payload);

        public static IntPtr Create(SampleCallback sampleCallback, ErrorCallback errorCallback, IntPtr context) =>
            mer_capture_create(sampleCallback, errorCallback, context);

        public static int ScreenPermission(bool request) => mer_capture_screen_permission(request ? 1 : 0);

        public static int ListDisplays(IntPtr displays, int capacity, StringBuilder errorMessage, int errorMessageSize) =>
            mer_capture_list_displays(displays, capacity, errorMessage, errorMessageSize);

        public static int Start(
            IntPtr handle,
            uint displayId,
            uint width,
            uint height,
            double framesPerSecond,
            int showCursor,
            StringBuilder errorMessage,
            int errorMessageSize) =>
            mer_capture_start(handle, displayId, width, height, framesPerSecond, showCursor, errorMessage, errorMessageSize);

        public static int Stop(IntPtr handle, StringBuilder errorMessage, int errorMessageSize) =>
            mer_capture_stop(handle, errorMessage, errorMessageSize);

        public static void Destroy(IntPtr handle) => mer_capture_destroy(handle);

        public static void ReleasePayload(IntPtr payload)
        {
            if (payload != IntPtr.Zero)
                mer_capture_release_payload(payload);
        }
    }
}
