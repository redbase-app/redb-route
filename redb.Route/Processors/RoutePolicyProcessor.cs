using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Wraps a pipeline processor with <see cref="IRoutePolicy.OnExchangeBegin"/> /
/// <see cref="IRoutePolicy.OnExchangeDone"/> calls. Only created for routes that have a policy.
/// </summary>
internal sealed class RoutePolicyProcessor : IProcessor
{
    private readonly IProcessor _next;
    private readonly IRoutePolicy _policy;
    private readonly IRouteContext _context;

    public RoutePolicyProcessor(IProcessor next, IRoutePolicy policy, IRouteContext context)
    {
        _next = next;
        _policy = policy;
        _context = context;
    }

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        await _policy.OnExchangeBegin(_context, exchange, ct).ConfigureAwait(false);
        try
        {
            await _next.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            await _policy.OnExchangeDone(_context, exchange, ct).ConfigureAwait(false);
        }
    }
}
