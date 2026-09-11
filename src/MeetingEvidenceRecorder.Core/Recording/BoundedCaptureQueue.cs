using System.Threading.Channels;

namespace MeetingEvidenceRecorder.Core.Recording;

public enum QueueEnqueueResult
{
    Accepted,
    Dropped,
    Closed
}

public sealed class CaptureQueueFullException(string queueName)
    : InvalidOperationException($"{queueName} queue is full; recording integrity cannot be maintained.");

/// <summary>
/// Bounded queue used between native capture callbacks and processing workers.
/// </summary>
public sealed class BoundedCaptureQueue<T> : IAsyncDisposable
{
    private readonly Channel<T> channel;
    private readonly string name;
    private long accepted;
    private long dropped;

    public BoundedCaptureQueue(string name, int capacity)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A queue name is required.", nameof(name));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.name = name;
        channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        Capacity = capacity;
    }

    public int Capacity { get; }
    public long AcceptedCount => Interlocked.Read(ref accepted);
    public long DroppedCount => Interlocked.Read(ref dropped);

    public QueueEnqueueResult TryEnqueue(T item, bool allowDrop)
    {
        if (channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref accepted);
            return QueueEnqueueResult.Accepted;
        }

        if (allowDrop)
        {
            Interlocked.Increment(ref dropped);
            return QueueEnqueueResult.Dropped;
        }

        var exception = new CaptureQueueFullException(name);
        channel.Writer.TryComplete(exception);
        return QueueEnqueueResult.Closed;
    }

    public async ValueTask EnqueueAsync(T item, CancellationToken cancellationToken)
    {
        await channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref accepted);
    }

    public IAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken = default) =>
        channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete(Exception? error = null) => channel.Writer.TryComplete(error);

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
