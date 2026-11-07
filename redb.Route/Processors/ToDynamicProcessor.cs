using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Camel-style <c>toD()</c>: sends the exchange to an endpoint whose URI is
/// computed per message from a <c>${...}</c> template, an <see cref="IExpression"/>,
/// or a factory delegate. Producers are cached per resolved URI and reused.
/// </summary>
public sealed class ToDynamicProcessor : IProcessor
{
    private readonly DynamicEndpointResolver _resolver;

    /// <summary>Creates a dynamic-to processor with the given resolver.</summary>
    public ToDynamicProcessor(DynamicEndpointResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>The resolver this node sends through; shared with an interception wrapper so the target is evaluated once per message.</summary>
    internal DynamicEndpointResolver Resolver => _resolver;

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var (endpoint, producer) = await _resolver.ResolvePairAsync(exchange, ct).ConfigureAwait(false);
        await Core.CountedSend.Process(endpoint, producer, exchange, ct).ConfigureAwait(false);
    }
}
