using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Wraps a <c>To</c> / <c>ToD</c>: when the target URI matches the mask, publishes it in the
/// <see cref="TargetHeader"/> / <see cref="CamelTargetHeader"/> headers, runs the intercept steps, and
/// then sends to the original endpoint — unless the intercept asked to skip it (dry run, test double)
/// or stopped the exchange. A non-matching URI (dynamic targets) is sent untouched.
/// </summary>
internal sealed class InterceptSendProcessor : IProcessor
{
    /// <summary>Header carrying the intercepted target URI.</summary>
    public const string TargetHeader = "redb.toEndpoint";

    /// <summary>Read alias of <see cref="TargetHeader"/> for people coming from Apache Camel.</summary>
    public const string CamelTargetHeader = "CamelToEndpoint";

    private readonly IProcessor _intercept;
    private readonly IPredicate? _condition;
    private readonly bool _skipOriginal;
    private readonly string _uriPattern;
    private readonly Func<IExchange, string> _targetUri;
    private readonly IProcessor _send;
    private readonly DynamicEndpointResolver? _resolver;

    /// <param name="resolver">For a dynamic target: the node's own resolver, so the URI resolved here is the one the send uses (evaluated once per message).</param>
    public InterceptSendProcessor(IProcessor intercept, IPredicate? condition, bool skipOriginal, string uriPattern,
        Func<IExchange, string> targetUri, IProcessor send, DynamicEndpointResolver? resolver = null)
    {
        _intercept = intercept ?? throw new ArgumentNullException(nameof(intercept));
        _condition = condition;
        _skipOriginal = skipOriginal;
        _uriPattern = uriPattern ?? throw new ArgumentNullException(nameof(uriPattern));
        _targetUri = targetUri ?? throw new ArgumentNullException(nameof(targetUri));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _resolver = resolver;
    }

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var parked = false;
        var uri = _resolver is null ? _targetUri(exchange) : _resolver.ResolveAndPark(exchange, out parked);
        try
        {
            if (!UriMask.IsMatch(_uriPattern, uri))
            {
                await _send.Process(exchange, ct).ConfigureAwait(false);
                return;
            }

            // A header travels with the message (replies, logs, the dashboard): publish the target without its secrets.
            var published = EndpointUri.Sanitize(uri);
            exchange.In.Headers[TargetHeader] = published;
            exchange.In.Headers[CamelTargetHeader] = published;

            if (_condition is not null && !await _condition.MatchesAsync(exchange).ConfigureAwait(false))
            {
                await _send.Process(exchange, ct).ConfigureAwait(false);
                return;
            }

            await _intercept.Process(exchange, ct).ConfigureAwait(false);
            if (exchange.IsStopped || _skipOriginal)
                return;
            await _send.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            if (parked) _resolver!.Unpark(exchange);
        }
    }
}
