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

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var producer = await _resolver.ResolveProducerAsync(exchange, ct).ConfigureAwait(false);
        await producer.Process(exchange, ct).ConfigureAwait(false);
    }
}
