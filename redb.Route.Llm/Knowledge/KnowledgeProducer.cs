using System.Text.Json.Nodes;

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Knowledge;

/// <summary>
/// Ingest producer: chunks the exchange body, (optionally) embeds, and upserts
/// into the resolved <see cref="IKnowledgeStore"/>. Writes a small summary
/// (<c>{"collection","docId","chunks"}</c>) to <c>Out.Body</c>.
/// </summary>
public sealed class KnowledgeProducer : ConnectableProducer
{
    /// <summary>Header carrying a per-document id base (chunk ids become <c>{docId}#{i}</c>).</summary>
    public const string DocIdHeader = "knowledge.doc.id";

    private readonly KnowledgeEndpoint _endpoint;
    private readonly KnowledgeEndpointOptions _options;

    /// <summary>Creates the producer.</summary>
    public KnowledgeProducer(KnowledgeEndpoint endpoint, KnowledgeEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"knowledge:{_endpoint.Collection}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(exchange);

        var store = ResolveStore(exchange)
            ?? throw new InvalidOperationException(
                "knowledge://: no IKnowledgeStore is registered. Call AddRedbLlmStorage() or register one.");

        var embedder = _options.Embed ? ResolveEmbedder(exchange) : null;

        var text = exchange.In.Body?.ToString() ?? string.Empty;
        var docId = exchange.In.Headers.TryGetValue(DocIdHeader, out var h) && h is string s && s.Length > 0
            ? s
            : (_options.DocId ?? "doc");

        var pieces = KnowledgeChunker.Split(text, _options.ChunkChars, _options.Overlap);

        IReadOnlyList<float[]> vectors = Array.Empty<float[]>();
        if (embedder is not null && pieces.Count > 0)
            vectors = await embedder.EmbedAsync(pieces, ct).ConfigureAwait(false);

        var chunks = new List<KnowledgeChunk>(pieces.Count);
        for (var i = 0; i < pieces.Count; i++)
        {
            chunks.Add(new KnowledgeChunk
            {
                Id = $"{docId}#{i}",
                Text = pieces[i],
                Collection = _endpoint.Collection,
                Embedding = i < vectors.Count ? vectors[i] : Array.Empty<float>()
            });
        }

        if (chunks.Count > 0)
            await store.UpsertManyAsync(chunks, exchange, ct).ConfigureAwait(false);

        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = new JsonObject
        {
            ["collection"] = _endpoint.Collection,
            ["docId"] = docId,
            ["chunks"] = chunks.Count
        }.ToJsonString();
        exchange.Out.Headers["llm.knowledge.ingested"] = chunks.Count;
    }

    private IKnowledgeStore? ResolveStore(IExchange exchange)
    {
        var ctx = (_endpoint.Component as ComponentBase)?.Context;
        return ctx?.GetService<IKnowledgeStore>()
            ?? ctx?.GetServiceProvider()?.GetService(typeof(IKnowledgeStore)) as IKnowledgeStore
            ?? exchange.ServiceProvider?.GetService(typeof(IKnowledgeStore)) as IKnowledgeStore;
    }

    private IEmbeddingProvider? ResolveEmbedder(IExchange exchange)
    {
        var ctx = (_endpoint.Component as ComponentBase)?.Context;
        return ctx?.GetService<IEmbeddingProvider>()
            ?? ctx?.GetServiceProvider()?.GetService(typeof(IEmbeddingProvider)) as IEmbeddingProvider
            ?? exchange.ServiceProvider?.GetService(typeof(IEmbeddingProvider)) as IEmbeddingProvider;
    }
}

/// <summary>
/// Deterministic character-window chunker: fixed <paramref name="chunkChars"/>
/// windows with <paramref name="overlap"/> chars shared between neighbours.
/// </summary>
public static class KnowledgeChunker
{
    /// <summary>Splits <paramref name="text"/> into overlapping windows.</summary>
    public static IReadOnlyList<string> Split(string? text, int chunkChars, int overlap)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return Array.Empty<string>();
        if (chunkChars < 1) chunkChars = 1;
        if (overlap < 0 || overlap >= chunkChars) overlap = 0;
        if (text.Length <= chunkChars) return new[] { text };

        var step = chunkChars - overlap;
        var chunks = new List<string>();
        for (var start = 0; start < text.Length; start += step)
        {
            var len = Math.Min(chunkChars, text.Length - start);
            chunks.Add(text.Substring(start, len));
            if (start + len >= text.Length) break;
        }
        return chunks;
    }
}
