using System.Text;

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Knowledge;

/// <summary>Options for the <see cref="KnowledgeRetrievalDsl.Knowledge(IRouteDefinition, KnowledgeRetrievalOptions)"/> step.</summary>
public sealed class KnowledgeRetrievalOptions
{
    /// <summary>Collection to search (null = across all collections).</summary>
    public string? Collection { get; set; }

    /// <summary>How many chunks to inject.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Explicit store. When null, resolved from the exchange's <see cref="IServiceProvider"/>.</summary>
    public IKnowledgeStore? Store { get; set; }

    /// <summary>
    /// When set (or resolvable from DI), retrieval is <b>semantic</b> (embed query → cosine
    /// <see cref="IKnowledgeStore.SearchAsync"/>); otherwise <b>keyword</b>
    /// (<see cref="IKnowledgeStore.SearchTextAsync"/>).
    /// </summary>
    public IEmbeddingProvider? EmbeddingProvider { get; set; }

    /// <summary>Chooses the query text. Default: the exchange body as string.</summary>
    public Func<IExchange, string?>? Query { get; set; }

    /// <summary>Max chars of each chunk injected (0 = full).</summary>
    public int MaxCharsPerChunk { get; set; } = 1000;

    /// <summary>Line placed above the retrieved chunks in the system prompt.</summary>
    public string Preamble { get; set; } =
        "Relevant knowledge (ground your answer on it; do not quote it verbatim to the user):";
}

/// <summary>
/// DSL step that retrieves top-K knowledge chunks for the current message and
/// <b>injects them into the system prompt</b> (<c>LlmHeaders.SystemPrompt</c>),
/// so a following <c>.To("llm://...")</c> answers grounded on them. A no-op when
/// no store is available or nothing is retrieved — never breaks the pipeline.
/// <example>
/// <code>
/// From("kafka://questions")
///     .Knowledge("handbook", k: 5)          // retrieve + inject
///     .To("llm://claude");                  // answer, grounded
/// </code>
/// </example>
/// </summary>
public static class KnowledgeRetrievalDsl
{
    /// <summary>Retrieve + inject, shorthand.</summary>
    public static IRouteDefinition Knowledge(this IRouteDefinition self, string? collection, int k = 5) =>
        Knowledge(self, new KnowledgeRetrievalOptions { Collection = collection, TopK = k });

    /// <summary>Retrieve + inject with explicit options.</summary>
    public static IRouteDefinition Knowledge(this IRouteDefinition self, KnowledgeRetrievalOptions options)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(options);

        return self.Process(async (exchange, ct) =>
        {
            var store = options.Store
                ?? exchange.ServiceProvider?.GetService(typeof(IKnowledgeStore)) as IKnowledgeStore;
            if (store is null) return;   // no store wired → no-op

            var query = (options.Query?.Invoke(exchange) ?? exchange.In.Body?.ToString())?.Trim();
            if (string.IsNullOrEmpty(query)) return;

            var topK = Math.Clamp(options.TopK, 1, 50);
            var embedder = options.EmbeddingProvider
                ?? exchange.ServiceProvider?.GetService(typeof(IEmbeddingProvider)) as IEmbeddingProvider;

            IReadOnlyList<KnowledgeSearchResult> hits;
            if (embedder is not null)
            {
                var vector = await embedder.EmbedOneAsync(query, ct).ConfigureAwait(false);
                hits = await store.SearchAsync(vector, topK, options.Collection, exchange, ct).ConfigureAwait(false);
            }
            else
            {
                hits = await store.SearchTextAsync(query, topK, options.Collection, exchange, ct).ConfigureAwait(false);
            }

            if (hits.Count == 0) return;

            var block = BuildBlock(hits, options);
            var existing = exchange.In.Headers.TryGetValue(LlmHeaders.SystemPrompt, out var sp) ? sp?.ToString() : null;
            exchange.In.Headers[LlmHeaders.SystemPrompt] =
                string.IsNullOrEmpty(existing) ? block : existing + "\n\n" + block;
            exchange.In.Headers["llm.knowledge.injected"] = hits.Count;
        });
    }

    private static string BuildBlock(IReadOnlyList<KnowledgeSearchResult> hits, KnowledgeRetrievalOptions options)
    {
        var sb = new StringBuilder(options.Preamble);
        foreach (var hit in hits)
        {
            var text = hit.Chunk.Text;
            if (options.MaxCharsPerChunk > 0 && text.Length > options.MaxCharsPerChunk)
                text = text[..options.MaxCharsPerChunk] + "…";
            sb.Append("\n- ").Append(text.Replace("\r\n", " ").Replace('\n', ' '));
        }
        return sb.ToString();
    }
}
