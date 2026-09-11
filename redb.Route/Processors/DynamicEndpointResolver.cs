using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Processors;

/// <summary>
/// Per-instance cache that resolves an endpoint URI per exchange (via a
/// <c>${...}</c> template, an <see cref="IExpression"/>, or a factory delegate),
/// then looks up the matching component, creates a producer for the resolved URI,
/// starts it, caches it by resolved URI string, and reuses it on subsequent calls.
/// <para>
/// Used by dynamic-endpoint processors (<see cref="ToDynamicProcessor"/>, dynamic
/// WireTap/Enrich/PollEnrich/RecipientList) so producers are not recreated on every
/// message. Producers are also registered with <see cref="RouteContext"/> so they
/// participate in the standard graceful-shutdown flow.
/// </para>
/// </summary>
public sealed class DynamicEndpointResolver
{
    private readonly IRouteContext _context;
    private readonly Func<IExchange, string> _uriFactory;
    // Endpoint rides along so the sender can record producer-side statistics (CountedSend).
    private readonly ConcurrentDictionary<string, Lazy<Task<(IEndpoint Endpoint, IProducer Producer)>>> _producers = new();

    /// <summary>Creates a resolver that calls <paramref name="uriFactory"/> per exchange.</summary>
    public DynamicEndpointResolver(IRouteContext context, Func<IExchange, string> uriFactory)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _uriFactory = uriFactory ?? throw new ArgumentNullException(nameof(uriFactory));
    }

    /// <summary>Creates a resolver that resolves a <c>${...}</c> template per exchange.</summary>
    public static DynamicEndpointResolver FromTemplate(IRouteContext context, string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        return new DynamicEndpointResolver(context,
            ex => ExpressionResolver.ProcessTemplate(template, ex));
    }

    /// <summary>Creates a resolver that evaluates an <see cref="IExpression"/> per exchange.</summary>
    public static DynamicEndpointResolver FromExpression(IRouteContext context, IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new DynamicEndpointResolver(context,
            ex => expression.Evaluate<string>(ex)
                  ?? throw new InvalidOperationException(
                      "Dynamic endpoint expression evaluated to null."));
    }

    // One evaluation per message even when interception wraps the send: the wrapper resolves the URI
    // and parks it on the exchange under a key private to this resolver; the send that follows takes
    // it from there instead of evaluating the template again.
    private readonly string _parkedUriKey = "__redb_tod:" + Guid.NewGuid().ToString("N");

    /// <summary>Resolves the URI for <paramref name="exchange"/> and returns a (cached) started producer.</summary>
    public async Task<IProducer> ResolveProducerAsync(IExchange exchange, CancellationToken ct)
        => (await ResolvePairAsync(exchange, ct).ConfigureAwait(false)).Producer;

    /// <summary>Same as <see cref="ResolveProducerAsync"/>, with the endpoint for statistics recording.</summary>
    internal async Task<(IEndpoint Endpoint, IProducer Producer)> ResolvePairAsync(IExchange exchange, CancellationToken ct)
    {
        string resolved;
        if (exchange.Properties.TryGetValue(_parkedUriKey, out var parked) && parked is string parkedUri)
        {
            resolved = parkedUri;
            exchange.Properties.Remove(_parkedUriKey);
        }
        else
        {
            resolved = ResolveUri(exchange);
        }

        return await GetOrCreatePairAsync(resolved, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the started producer for an already-resolved URI, creating and caching it on first use.</summary>
    public async Task<IProducer> GetOrCreateProducerAsync(string resolvedUri, CancellationToken ct)
        => (await GetOrCreatePairAsync(resolvedUri, ct).ConfigureAwait(false)).Producer;

    /// <summary>Same as <see cref="GetOrCreateProducerAsync"/>, with the endpoint for statistics recording.</summary>
    internal async Task<(IEndpoint Endpoint, IProducer Producer)> GetOrCreatePairAsync(string resolvedUri, CancellationToken ct)
    {
        // Cache key is the fully-resolved URI; one producer per distinct URI.
        var lazy = _producers.GetOrAdd(resolvedUri,
            uri => new Lazy<Task<(IEndpoint, IProducer)>>(() => CreateAsync(uri, ct)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The first caller's token was cancelled while its producer started; drop the entry so the
            // next message creates the producer instead of awaiting a cancelled task forever.
            _producers.TryRemove(new KeyValuePair<string, Lazy<Task<(IEndpoint, IProducer)>>>(resolvedUri, lazy));
            throw;
        }
    }

    /// <summary>
    /// Resolves the URI and parks it for the send that follows on this exchange; <paramref name="parked"/>
    /// tells whether this call parked it (the owner clears it with <see cref="Unpark"/>, so a skipped
    /// send never leaves a stale value for a later pass through the same node).
    /// </summary>
    internal string ResolveAndPark(IExchange exchange, out bool parked)
    {
        if (exchange.Properties.TryGetValue(_parkedUriKey, out var existing) && existing is string existingUri)
        {
            parked = false;
            return existingUri;
        }

        var resolved = ResolveUri(exchange);
        exchange.Properties[_parkedUriKey] = resolved;
        parked = true;
        return resolved;
    }

    /// <summary>Removes the parked URI (see <see cref="ResolveAndPark"/>).</summary>
    internal void Unpark(IExchange exchange) => exchange.Properties.Remove(_parkedUriKey);

    /// <summary>Resolves the target URI for this exchange without creating a producer (used by <c>InterceptSendToEndpoint</c>).</summary>
    public string ResolveUri(IExchange exchange)
    {
        var resolved = _uriFactory(exchange);
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException(
                "Dynamic endpoint URI resolved to an empty value.");
        return resolved;
    }

    private async Task<(IEndpoint, IProducer)> CreateAsync(string resolvedUri, CancellationToken ct)
    {
        var endpoint = _context.GetEndpoint(resolvedUri);
        var producer = endpoint.CreateProducer();
        await producer.Start(ct).ConfigureAwait(false);
        (_context as RouteContext)?.TrackProducer(producer);
        return (endpoint, producer);
    }
}
