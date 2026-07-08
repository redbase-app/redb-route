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
    private readonly ConcurrentDictionary<string, Lazy<Task<IProducer>>> _producers = new();

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

    /// <summary>Resolves the URI for <paramref name="exchange"/> and returns a (cached) started producer.</summary>
    public async Task<IProducer> ResolveProducerAsync(IExchange exchange, CancellationToken ct)
    {
        var resolved = _uriFactory(exchange);
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException(
                "Dynamic endpoint URI resolved to an empty value.");

        // Cache key is the fully-resolved URI; one producer per distinct URI.
        var lazy = _producers.GetOrAdd(resolved, uri => new Lazy<Task<IProducer>>(() => CreateAsync(uri, ct)));
        return await lazy.Value.ConfigureAwait(false);
    }

    private async Task<IProducer> CreateAsync(string resolvedUri, CancellationToken ct)
    {
        var endpoint = _context.GetEndpoint(resolvedUri);
        var producer = endpoint.CreateProducer();
        await producer.Start(ct).ConfigureAwait(false);
        (_context as RouteContext)?.TrackProducer(producer);
        return producer;
    }
}
