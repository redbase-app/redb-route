using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Runs the intercept steps before the intercepted processor (a route step for <c>Intercept()</c>,
/// the whole route for <c>InterceptFrom()</c>). A condition that does not hold leaves the step
/// untouched; a <c>Stop()</c> inside the intercept stops the exchange.
/// </summary>
internal sealed class InterceptProcessor : IProcessor
{
    private readonly IProcessor _intercept;
    private readonly IPredicate? _condition;
    private readonly IProcessor _step;

    public InterceptProcessor(IProcessor intercept, IPredicate? condition, IProcessor step)
    {
        _intercept = intercept ?? throw new ArgumentNullException(nameof(intercept));
        _condition = condition;
        _step = step ?? throw new ArgumentNullException(nameof(step));
    }

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (_condition is null || await _condition.MatchesAsync(exchange).ConfigureAwait(false))
        {
            await _intercept.Process(exchange, ct).ConfigureAwait(false);
            if (exchange.IsStopped) return;
        }
        await _step.Process(exchange, ct).ConfigureAwait(false);
    }
}
