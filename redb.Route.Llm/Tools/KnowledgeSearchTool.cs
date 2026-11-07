using System.Text.Json;
using System.Text.Json.Nodes;

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Extensions;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Options for the <c>knowledge_search</c> LLM tool / <see cref="KnowledgeSearchDsl.KnowledgeSearch"/> step.
/// </summary>
public sealed class KnowledgeSearchOptions
{
    /// <summary><c>From(...)</c> URI the tool route binds to.</summary>
    public string EndpointUri { get; set; } = "direct:llm.knowledge_search";

    /// <summary>Model-facing tool name.</summary>
    public string ToolName { get; set; } = "knowledge_search";

    /// <summary>
    /// Explicit store. When null, resolved from <c>exchange.ServiceProvider</c>
    /// at call time — so <c>AddRedbLlmStorage()</c>'s <see cref="IKnowledgeStore"/>
    /// (or the in-memory default) is picked up automatically.
    /// </summary>
    public IKnowledgeStore? Store { get; set; }

    /// <summary>Top-K when the model does not specify <c>top_k</c>.</summary>
    public int DefaultTopK { get; set; } = 5;

    /// <summary>
    /// Pin the tool to one collection. When set, the model's <c>collection</c>
    /// argument is ignored (the tool can only ever see this collection) — the
    /// lever for scoping an agent to a single tenant / document set. Null lets
    /// the model choose (or search across all collections).
    /// </summary>
    public string? Collection { get; set; }

    /// <summary>Max chars of chunk text returned per hit (0 = full text).</summary>
    public int MaxTextChars { get; set; } = 2000;

    /// <summary>
    /// When set, the tool runs a <b>semantic</b> search: it embeds the query with
    /// this provider and ranks chunks by cosine similarity
    /// (<see cref="IKnowledgeStore.SearchAsync"/>). When null it runs a
    /// <b>keyword</b> substring search (<see cref="IKnowledgeStore.SearchTextAsync"/>).
    /// Semantic needs the chunks to have been ingested with embeddings.
    /// </summary>
    public IEmbeddingProvider? EmbeddingProvider { get; set; }
}

/// <summary>
/// DSL step that answers a <c>knowledge_search</c> tool call: reads
/// <c>{"query":"...","top_k":int?,"collection":string?}</c> from the exchange
/// body, runs <see cref="IKnowledgeStore.SearchTextAsync"/> (keyword / substring),
/// and writes <c>{"results":[{"id","collection","score","text"}]}</c> to the body.
/// </summary>
public static class KnowledgeSearchDsl
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Adds a knowledge keyword-search step. See <see cref="KnowledgeSearchOptions"/>.</summary>
    public static IRouteDefinition KnowledgeSearch(this IRouteDefinition self, KnowledgeSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(options);

        return self.Process(async (exchange, ct) =>
        {
            var store = options.Store
                ?? exchange.ServiceProvider?.GetService(typeof(IKnowledgeStore)) as IKnowledgeStore
                ?? throw new InvalidOperationException(
                    "knowledge_search: no IKnowledgeStore available. Set KnowledgeSearchOptions.Store " +
                    "or register one (e.g. AddRedbLlmStorage()).");

            var (query, topK, collection) = ParseInput(exchange.In.Body);
            var effectiveTopK = Math.Clamp(topK ?? options.DefaultTopK, 1, 50);
            var effectiveCollection = options.Collection ?? collection;   // pinned collection wins

            // Semantic when an embedding provider is configured, else keyword.
            IReadOnlyList<KnowledgeSearchResult> hits;
            if (options.EmbeddingProvider is { } embedding)
            {
                var vector = await embedding.EmbedOneAsync(query, ct).ConfigureAwait(false);
                hits = await store
                    .SearchAsync(vector, effectiveTopK, effectiveCollection, exchange, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                hits = await store
                    .SearchTextAsync(query, effectiveTopK, effectiveCollection, exchange, ct)
                    .ConfigureAwait(false);
            }

            var results = new JsonArray();
            foreach (var hit in hits)
            {
                var text = hit.Chunk.Text;
                if (options.MaxTextChars > 0 && text.Length > options.MaxTextChars)
                    text = text[..options.MaxTextChars] + "…";

                results.Add(new JsonObject
                {
                    ["id"] = hit.Chunk.Id,
                    ["collection"] = hit.Chunk.Collection,
                    ["score"] = hit.Score,
                    ["text"] = text
                });
            }

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = new JsonObject { ["results"] = results }.ToJsonString(JsonOpts);
            exchange.Out.Headers["llm.knowledge_search.hits"] = results.Count;
        });
    }

    private static (string Query, int? TopK, string? Collection) ParseInput(object? body)
    {
        var json = body as string ?? body?.ToString();
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("knowledge_search: empty tool input.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(q.GetString()))
            throw new ArgumentException("knowledge_search: 'query' (string) is required.");

        int? topK = root.TryGetProperty("top_k", out var tk) && tk.ValueKind == JsonValueKind.Number
            ? tk.GetInt32() : null;
        string? collection = root.TryGetProperty("collection", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;

        return (q.GetString()!, topK, collection);
    }
}

/// <summary>
/// Self-contained <c>RouteBuilder</c> that mounts <c>knowledge_search</c> as an
/// LLM tool. Prefer the DSL <c>.KnowledgeSearch(opts)</c> on an existing route;
/// this shim is for handing a ready route to <c>context.AddRoutes(...)</c>.
/// <example>
/// <code>
/// context.AddRoutes(new KnowledgeSearchTool(new KnowledgeSearchOptions
/// {
///     Collection = "rules",   // pin the agent to one collection
/// }));
/// // ...then expose it: Llm.Factory("claude").Tools("knowledge_search")
/// </code>
/// </example>
/// </summary>
public sealed class KnowledgeSearchTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "query":      { "type": "string",  "description": "Keyword / substring to find in the knowledge base." },
            "top_k":      { "type": "integer", "description": "Max hits to return (default 5, capped at 50)." },
            "collection": { "type": "string",  "description": "Restrict to this collection (ignored when the tool is pinned to one)." }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    private readonly KnowledgeSearchOptions _options;

    /// <summary>Creates the tool with the given options.</summary>
    public KnowledgeSearchTool(KnowledgeSearchOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    protected override void Configure() =>
        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Search the knowledge base for chunks whose text contains the query " +
                             "(case-insensitive keyword / substring). Returns " +
                             "{\"results\":[{\"id\",\"collection\",\"score\",\"text\"}]}, most relevant first.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .KnowledgeSearch(_options);
}
