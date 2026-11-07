namespace redb.Route.Core;

/// <summary>
/// A processor that holds exchanges of its own across calls (aggregation groups) and settles them when the context
/// stops: after the consumers have stopped — nothing more arrives — and before the producers stop, so what it sends on
/// still goes out.
/// </summary>
internal interface IStopAwareProcessor
{
    /// <summary>Completes or releases what the processor still holds.</summary>
    /// <param name="ct">The shutdown token.</param>
    Task OnConsumersStoppedAsync(CancellationToken ct);
}
