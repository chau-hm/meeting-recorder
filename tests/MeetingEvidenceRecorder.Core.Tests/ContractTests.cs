using System.Threading.Channels;
using MeetingEvidenceRecorder.Core.Evidence;
using MeetingEvidenceRecorder.Core.Recording;

namespace MeetingEvidenceRecorder.Core.Tests;

public class ContractTests
{
    [Theory]
    [InlineData(0L)] [InlineData(1L)] [InlineData(999L)] [InlineData(1000L)]
    [InlineData(60000L)] [InlineData(3600000L)] [InlineData(7200000L)] [InlineData(long.MaxValue)]
    public void CanonicalPlaybackMillisecondsAreIntegerAndIndependentOfIdentity(long timestamp)
    {
        var shot = new ScreenshotEvent { EventId = "evt-first", TimestampMs = timestamp, Asset = "screenshots/arbitrary.png" };
        Assert.Empty(ContractValidation.Validate(shot));
        Assert.Empty(ContractValidation.Validate(shot with { EventId = "evt-second" }));
    }

    [Theory]
    [InlineData(-1L, true)] [InlineData(1000L, false)] [InlineData(2000L, false)] [InlineData(2001L, true)]
    public void DurationBoundaryIncludesExactlyOneSecondTolerance(long timestamp, bool error)
    {
        var shot = new ScreenshotEvent { EventId = "evt", TimestampMs = timestamp, Asset = "a.png" };
        Assert.Equal(error, ContractValidation.Validate(shot, 1000).Any(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("1.0", true)] [InlineData("1.99", true)] [InlineData("2.0", false)]
    [InlineData("1", false)] [InlineData("1.0.0", false)] [InlineData("v1.0", false)]
    public void SchemaMajorIsExplicit(string version, bool supported) => Assert.Equal(supported, ContractValidation.SupportsSchema(version));
}

public sealed class RecordingClockTests
{
    [Fact]
    public void StartsAtZeroAndUsesMonotonicElapsedTime()
    {
        var time = new FakeTimeSource();
        var clock = new RecordingClock(time);

        clock.Start();
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);

        time.Advance(25);
        Assert.Equal(TimeSpan.FromMilliseconds(25), clock.Elapsed);
    }

    [Fact]
    public void StopFreezesElapsedTime()
    {
        var time = new FakeTimeSource();
        var clock = new RecordingClock(time);

        clock.Start();
        time.Advance(40);
        clock.Stop();
        time.Advance(100);

        Assert.Equal(TimeSpan.FromMilliseconds(40), clock.Elapsed);
        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void DuplicateStartIsRejected()
    {
        var clock = new RecordingClock(new FakeTimeSource());

        clock.Start();

        Assert.Throws<InvalidOperationException>(clock.Start);
    }

    [Fact]
    public void PauseAndResumeExcludePausedTime()
    {
        var time = new FakeTimeSource();
        var clock = new RecordingClock(time);

        clock.Start();
        time.Advance(20);
        clock.Pause();
        time.Advance(100);
        clock.Resume();
        time.Advance(30);
        clock.Stop();

        Assert.Equal(TimeSpan.FromMilliseconds(50), clock.Elapsed);
    }

    [Fact]
    public void ConcurrentReadsAreSafe()
    {
        var time = new FakeTimeSource();
        var clock = new RecordingClock(time);
        clock.Start();
        time.Advance(10);

        Parallel.For(0, 1000, _ => Assert.True(clock.Elapsed >= TimeSpan.Zero));
    }

    private sealed class FakeTimeSource : IMonotonicTimeSource
    {
        public long Frequency => 1000;
        private long timestamp;

        public long GetTimestamp() => Interlocked.Read(ref timestamp);

        public void Advance(long milliseconds) => Interlocked.Add(ref timestamp, milliseconds);
    }
}

public sealed class RecordingTimestampMapperTests
{
    [Fact]
    public void UsesOneOriginForVideoAndAudioTimestamps()
    {
        var mapper = new RecordingTimestampMapper();

        Assert.Equal(TimeSpan.Zero, mapper.Map(new NativeTimestamp(1000, 1000)));
        Assert.Equal(TimeSpan.FromMilliseconds(25), mapper.Map(new NativeTimestamp(1025, 1000)));
        Assert.True(mapper.HasOrigin);
    }

    [Fact]
    public void RejectsAnEarlierTimestampAfterOriginIsEstablished()
    {
        var mapper = new RecordingTimestampMapper();
        _ = mapper.Map(new NativeTimestamp(1000, 1000));

        Assert.Throws<InvalidOperationException>(() =>
            mapper.Map(new NativeTimestamp(999, 1000)));
    }

    [Fact]
    public void ExplicitOriginIsUsedBeforeConsumersReadFrames()
    {
        var mapper = new RecordingTimestampMapper();
        mapper.SetOrigin(new NativeTimestamp(1000, 1000));

        Assert.Equal(TimeSpan.FromMilliseconds(25), mapper.Map(new NativeTimestamp(1025, 1000)));
    }
}

public sealed class BoundedCaptureQueueTests
{
    [Fact]
    public async Task VideoQueueDropsOnlyWhenExplicitlyAllowed()
    {
        await using var queue = new BoundedCaptureQueue<int>("video", 1);

        Assert.Equal(QueueEnqueueResult.Accepted, queue.TryEnqueue(1, allowDrop: true));
        Assert.Equal(QueueEnqueueResult.Dropped, queue.TryEnqueue(2, allowDrop: true));
        Assert.Equal(1, queue.DroppedCount);
        queue.Complete();

        var values = new List<int>();
        await foreach (var value in queue.ReadAllAsync())
            values.Add(value);
        Assert.Equal([1], values);
    }

    [Fact]
    public async Task AudioQueueReportsFullInsteadOfSilentlyDropping()
    {
        await using var queue = new BoundedCaptureQueue<int>("audio", 1);

        Assert.Equal(QueueEnqueueResult.Accepted, queue.TryEnqueue(1, allowDrop: false));
        Assert.Equal(QueueEnqueueResult.Closed, queue.TryEnqueue(2, allowDrop: false));
        Assert.Equal(0, queue.DroppedCount);
        await Assert.ThrowsAsync<CaptureQueueFullException>(async () =>
        {
            await foreach (var _ in queue.ReadAllAsync())
            {
            }
        });
    }

    [Fact]
    public async Task CompletingQueueDrainsAcceptedItems()
    {
        await using var queue = new BoundedCaptureQueue<int>("video", 2);
        await queue.EnqueueAsync(1, CancellationToken.None);
        await queue.EnqueueAsync(2, CancellationToken.None);
        queue.Complete();

        var values = new List<int>();
        await foreach (var value in queue.ReadAllAsync())
            values.Add(value);

        Assert.Equal([1, 2], values);
    }

    [Fact]
    public async Task CancellationTerminatesAWaitingReader()
    {
        await using var queue = new BoundedCaptureQueue<int>("video", 1);
        using var cancellation = new CancellationTokenSource();
        var readTask = ConsumeAsync(queue, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    private static async Task ConsumeAsync(
        BoundedCaptureQueue<int> queue,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in queue.ReadAllAsync(cancellationToken))
        {
        }
    }
}
