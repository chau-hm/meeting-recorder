namespace MeetingEvidenceRecorder.Core.Recording;

public readonly record struct NativeTimestamp(long Value, int Timescale)
{
    public double TotalSeconds =>
        Timescale > 0 ? (double)Value / Timescale : throw new InvalidOperationException("Native timestamp has no valid timescale.");
}

/// <summary>Maps source media timestamps from the shared capture clock to recording time.</summary>
public sealed class RecordingTimestampMapper
{
    private readonly object gate = new();
    private decimal? sourceOriginSeconds;

    public bool HasOrigin
    {
        get
        {
            lock (gate)
            {
                return sourceOriginSeconds.HasValue;
            }
        }
    }

    public void SetOrigin(NativeTimestamp sourceTimestamp)
    {
        var seconds = ToSeconds(sourceTimestamp);
        lock (gate)
            sourceOriginSeconds ??= seconds;
    }

    public TimeSpan Map(NativeTimestamp sourceTimestamp)
    {
        var seconds = ToSeconds(sourceTimestamp);
        lock (gate)
        {
            sourceOriginSeconds ??= seconds;
            var normalized = seconds - sourceOriginSeconds.Value;
            if (normalized < 0)
                throw new InvalidOperationException("A source timestamp preceded the established recording origin.");

            var ticks = decimal.Round(
                normalized * TimeSpan.TicksPerSecond,
                0,
                MidpointRounding.AwayFromZero);
            if (ticks >= TimeSpan.MaxValue.Ticks)
                return TimeSpan.MaxValue;
            return TimeSpan.FromTicks((long)ticks);
        }
    }

    private static decimal ToSeconds(NativeTimestamp sourceTimestamp)
    {
        if (sourceTimestamp.Timescale <= 0)
            throw new InvalidOperationException("Native timestamp has no valid timescale.");
        return (decimal)sourceTimestamp.Value / sourceTimestamp.Timescale;
    }
}
