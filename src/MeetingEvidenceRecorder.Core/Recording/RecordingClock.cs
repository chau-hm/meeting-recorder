namespace MeetingEvidenceRecorder.Core.Recording;

public interface IMonotonicTimeSource
{
    long Frequency { get; }
    long GetTimestamp();
}

public sealed class StopwatchTimeSource : IMonotonicTimeSource
{
    public long Frequency => System.Diagnostics.Stopwatch.Frequency;

    public long GetTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
}

public interface IRecordingClock
{
    TimeSpan Elapsed { get; }
    bool IsRunning { get; }
    bool IsPaused { get; }

    void Start();
    void Pause();
    void Resume();
    void Stop();
}

/// <summary>
/// Process-local media timeline. Wall-clock time is deliberately not part of the clock.
/// </summary>
public sealed class RecordingClock : IRecordingClock
{
    private readonly IMonotonicTimeSource timeSource;
    private readonly object gate = new();
    private long activeTicks;
    private long segmentStartedAt;
    private bool started;
    private bool paused;
    private bool stopped;

    public RecordingClock(IMonotonicTimeSource? timeSource = null)
    {
        this.timeSource = timeSource ?? new StopwatchTimeSource();
        if (this.timeSource.Frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeSource), "A monotonic clock frequency must be positive.");
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (gate)
            {
                return ToTimeSpan(GetElapsedTicks());
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return started && !paused && !stopped;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (gate)
            {
                return started && paused && !stopped;
            }
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (started)
                throw new InvalidOperationException("RecordingClock can only be started once.");

            segmentStartedAt = ReadTimestamp();
            activeTicks = 0;
            started = true;
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            EnsureStarted();
            if (stopped)
                throw new InvalidOperationException("A stopped RecordingClock cannot be paused.");
            if (paused)
                throw new InvalidOperationException("RecordingClock is already paused.");

            activeTicks = checked(activeTicks + ReadTimestamp() - segmentStartedAt);
            paused = true;
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            EnsureStarted();
            if (stopped)
                throw new InvalidOperationException("A stopped RecordingClock cannot be resumed.");
            if (!paused)
                throw new InvalidOperationException("RecordingClock is not paused.");

            segmentStartedAt = ReadTimestamp();
            paused = false;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            EnsureStarted();
            if (stopped)
                throw new InvalidOperationException("RecordingClock is already stopped.");

            if (!paused)
                activeTicks = checked(activeTicks + ReadTimestamp() - segmentStartedAt);
            stopped = true;
            paused = false;
        }
    }

    private long GetElapsedTicks()
    {
        if (!started)
            return 0;
        if (paused || stopped)
            return activeTicks;

        return checked(activeTicks + ReadTimestamp() - segmentStartedAt);
    }

    private long ReadTimestamp()
    {
        var current = timeSource.GetTimestamp();
        if (started && current < segmentStartedAt)
            throw new InvalidOperationException("The monotonic time source moved backwards.");
        return current;
    }

    private void EnsureStarted()
    {
        if (!started)
            throw new InvalidOperationException("RecordingClock has not been started.");
    }

    private TimeSpan ToTimeSpan(long ticks)
    {
        var timeSpanTicks = ticks * (double)TimeSpan.TicksPerSecond / timeSource.Frequency;
        if (timeSpanTicks >= TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks(Math.Max(0, (long)Math.Round(timeSpanTicks, MidpointRounding.AwayFromZero)));
    }
}
