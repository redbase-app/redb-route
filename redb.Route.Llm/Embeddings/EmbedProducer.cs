using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Embeddings;

/// <summary>
/// Embedding producer: embeds the exchange body and writes the vector(s) to
/// <c>Out.Body</c>. A single text → <c>float[]</c>; a collection of texts →
/// <c>float[][]</c> (order preserved).
/// </summary>
public sealed class EmbedProducer : ConnectableProducer
{
    private readonly EmbedEndpoint _endpoint;
    private readonly EmbedEndpointOptions _options;
    private IEmbeddingProvider? _provider;

    /// <summary>Creates the producer.</summary>
    public EmbedProducer(EmbedEndpoint endpoint, EmbedEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"embed:{_endpoint.FactoryName}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(exchange);

        var provider = ResolveProvider();
        var body = exchange.In.Body;
        exchange.Out ??= exchange.In.Clone();

        if (body is IEnumerable<string> texts)   // a string is IEnumerable<char>, not <string>, so single texts fall through
        {
            var list = texts.ToList();
            var vectors = await provider.EmbedAsync(list, ct).ConfigureAwait(false);
            exchange.Out.Body = vectors is float[][] arr ? arr : vectors.ToArray();
            exchange.Out.Headers["llm.embedding.count"] = vectors.Count;
            exchange.Out.Headers["llm.embedding.dims"] = vectors.Count > 0 ? vectors[0].Length : 0;
        }
        else
        {
            var text = body?.ToString() ?? string.Empty;
            var vector = await provider.EmbedOneAsync(text, ct).ConfigureAwait(false);
            exchange.Out.Body = vector;
            exchange.Out.Headers["llm.embedding.dims"] = vector.Length;
        }

        exchange.Out.Headers["llm.embedding.model"] = provider.ModelId;
        exchange.Out.Headers["llm.embedding.provider"] = provider.ProviderId;
    }

    private IEmbeddingProvider ResolveProvider()
    {
        if (_provider is not null) return _provider;

        var component = (EmbedComponent)_endpoint.Component;
        var factory = _endpoint.ResolvedFactory
            ?? component.Context?.GetFromRegistry<LlmConnectionFactory>(_endpoint.FactoryName)
            ?? throw new InvalidOperationException(
                $"embed://: connection factory '{_endpoint.FactoryName}' is not registered in the route context.");

        _provider = component.ProviderFactory(factory);
        return _provider;
    }
}
