using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Samples messages from the stream, allowing only a subset to pass through.
/// Supports two modes: count-based (every Nth message) and time-based (at most one per period).
/// Non-sampled messages are stopped via <see cref="IExchange.Stop"/>.
/// Thread-safe via lock-free atomic operations.
/// </summary>
public class SamplingProcessor : IProcessor
{
    private readonly long _messageFrequency;
    private readonly long _periodTicks;
    private long _counter;
    private long _lastSampledTicks;

    /// <summary>Creates a count-based sampler that passes every Nth message.</summary>
    /// <param name="messageFrequency">Pass every Nth message (1 = all, 5 = every 5th).</param>
    public SamplingProcessor(long messageFrequency)
    {
        if (messageFrequency < 1)
            throw new ArgumentOutOfRangeException(nameof(messageFrequency), "Must be >= 1.");
        _messageFrequency = messageFrequency;
    }

    /// <summary>Creates a time-based sampler that passes at most one message per period.</summary>
    /// <param name="period">Minimum interval between sampled messages.</param>
    public SamplingProcessor(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), "Must be positive.");
        _periodTicks = period.Ticks;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (_messageFrequency > 0)
            ProcessCountBased(exchange);
        else
            ProcessTimeBased(exchange);

        return Task.CompletedTask;
    }

    private void ProcessCountBased(IExchange exchange)
    {
        var count = Interlocked.Increment(ref _counter);
        // Pass first message and every Nth after that: 1, 1+N, 1+2N, ...
        if ((count - 1) % _messageFrequency != 0)
            exchange.Stop();
    }

    private void ProcessTimeBased(IExchange exchange)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastSampledTicks);

        if (now - last < _periodTicks)
        {
            exchange.Stop();
            return;
        }

        // CAS: only one thread wins the race to sample
        if (Interlocked.CompareExchange(ref _lastSampledTicks, now, last) != last)
            exchange.Stop();
    }
}
