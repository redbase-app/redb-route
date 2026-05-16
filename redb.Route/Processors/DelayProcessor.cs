using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Delays processing by a specified duration. Supports cancellation.
/// </summary>
public class DelayProcessor : IProcessor
{
    private readonly TimeSpan _delay;

    /// <summary>Gets the delay duration.</summary>
    public TimeSpan Delay => _delay;

    /// <summary>Creates a delay processor with the specified duration.</summary>
    /// <param name="delay">Time to delay before continuing.</param>
    public DelayProcessor(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be non-negative.");

        _delay = delay;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, ct).ConfigureAwait(false);
        }
    }
}
