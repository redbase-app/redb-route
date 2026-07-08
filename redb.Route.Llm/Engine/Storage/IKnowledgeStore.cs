using System.Collections.Concurrent;
using redb.Route.Abstractions;

namespace redb.Route.Llm.Engine.Storage;

/// <summary>
/// Persists embedding-backed knowledge chunks for retrieval-augmented agent
/// runs. The MVP contract is intentionally narrow — upsert + cosine-similarity
/// query over an opaque vector. Providers that index the chunks separately
/// (e.g. pgvector, Qdrant) wrap this interface.
/// <para>
/// The optional <c>exchange</c> parameter on every method carries the route
/// pipeline's current exchange; REDB-backed implementations resolve a
/// per-exchange <see cref="redb.Core.IRedbService"/> through
/// <c>IRouteContext.GetRedbService(name, exchange)</c>. In-memory
/// implementations ignore it.
/// </para>
/// </summary>
public interface IKnowledgeStore
{
    /// <summary>Persists or replaces a chunk by its <see cref="KnowledgeChunk.Id"/>.</summary>
    Task UpsertAsync(KnowledgeChunk chunk, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>
    /// Persists or replaces many chunks at once. Bulk-friendly stores commit
    /// the whole set in a single transaction — orders of magnitude faster than
    /// looping <see cref="UpsertAsync"/>. The default implementation falls back
    /// to a sequential loop for stores that have no bulk path.
    /// </summary>
    async Task UpsertManyAsync(IEnumerable<KnowledgeChunk> chunks, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            await UpsertAsync(chunk, exchange, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Drops a chunk by id; no-op when not found.</summary>
    Task DeleteAsync(string chunkId, IExchange? exchange = null, CancellationToken ct = default);

    /// <summary>Returns the <paramref name="topK"/> most-similar chunks for the query embedding.</summary>
    Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        string? collection = null,
        IExchange? exchange = null,
        CancellationToken ct = default);

    /// <summary>
    /// Keyword fallback — returns up to <paramref name="topK"/> chunks whose text
    /// contains <paramref name="query"/> (case-insensitive substring), ranked by
    /// occurrence count. For corpora with no embeddings, or callers with no query
    /// vector (e.g. a small structured rule-set where exact terms beat semantic
    /// similarity). Backends push the substring filter down (SQL <c>LIKE</c>) and
    /// rank only the matched set — no full-table scan.
    /// <para>
    /// The default implementation throws <see cref="NotSupportedException"/>;
    /// stores that can express a substring filter override it. Adding this as a
    /// default member keeps the interface source-compatible for existing
    /// implementers.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<KnowledgeSearchResult>> SearchTextAsync(
        string query,
        int topK,
        string? collection = null,
        IExchange? exchange = null,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support keyword search; supply a query embedding and call SearchAsync.");
}

/// <summary>A single embedded knowledge chunk.</summary>
public sealed class KnowledgeChunk
{
    /// <summary>Stable identifier — caller-supplied so updates can replace a chunk in place.</summary>
    public required string Id { get; init; }

    /// <summary>Optional collection / namespace partition (e.g. tenant id, document set).</summary>
    public string? Collection { get; init; }

    /// <summary>Original chunk text shown to the model when the chunk is retrieved.</summary>
    public required string Text { get; init; }

    /// <summary>Embedding vector; dimensionality is implementation-defined.</summary>
    public required ReadOnlyMemory<float> Embedding { get; init; }

    /// <summary>Optional structured metadata (JSON) — e.g. source URL, page number.</summary>
    public string? MetadataJson { get; init; }

    /// <summary>Wall-clock timestamp the chunk was last upserted.</summary>
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>A chunk returned by <see cref="IKnowledgeStore.SearchAsync"/>, with score.</summary>
public sealed record KnowledgeSearchResult(KnowledgeChunk Chunk, double Score);

/// <summary>
/// In-memory knowledge store — does an O(N) cosine-similarity scan on every
/// query. Suitable for unit tests and small fixtures only; swap in the
/// production store for anything larger than a few thousand chunks.
/// </summary>
public sealed class InMemoryKnowledgeStore : IKnowledgeStore
{
    private readonly ConcurrentDictionary<string, KnowledgeChunk> _chunks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task UpsertAsync(KnowledgeChunk chunk, IExchange? exchange = null, CancellationToken ct = default)
    {
        _chunks[chunk.Id] = chunk;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string chunkId, IExchange? exchange = null, CancellationToken ct = default)
    {
        _chunks.TryRemove(chunkId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding, int topK, string? collection = null, IExchange? exchange = null, CancellationToken ct = default)
    {
        var query = queryEmbedding.Span;
        var ranked = new List<KnowledgeSearchResult>();

        foreach (var chunk in _chunks.Values)
        {
            if (collection is not null && !string.Equals(chunk.Collection, collection, StringComparison.Ordinal))
                continue;
            var score = CosineSimilarity(query, chunk.Embedding.Span);
            ranked.Add(new KnowledgeSearchResult(chunk, score));
        }

        var top = ranked.OrderByDescending(r => r.Score).Take(Math.Max(1, topK)).ToArray();
        return Task.FromResult<IReadOnlyList<KnowledgeSearchResult>>(top);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeSearchResult>> SearchTextAsync(
        string query, int topK, string? collection = null, IExchange? exchange = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<KnowledgeSearchResult>>([]);

        var ranked = new List<KnowledgeSearchResult>();
        foreach (var chunk in _chunks.Values)
        {
            if (collection is not null && !string.Equals(chunk.Collection, collection, StringComparison.Ordinal))
                continue;
            var score = KnowledgeTextMatch.CountOccurrences(chunk.Text, query);
            if (score == 0) continue;
            ranked.Add(new KnowledgeSearchResult(chunk, score));
        }

        var top = ranked.OrderByDescending(r => r.Score).Take(Math.Max(1, topK)).ToArray();
        return Task.FromResult<IReadOnlyList<KnowledgeSearchResult>>(top);
    }

    private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0d;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom <= 0 ? 0d : dot / denom;
    }
}

/// <summary>Shared keyword-match scoring for <see cref="IKnowledgeStore.SearchTextAsync"/> implementations.</summary>
internal static class KnowledgeTextMatch
{
    /// <summary>Case-insensitive count of non-overlapping <paramref name="needle"/> occurrences in <paramref name="haystack"/>.</summary>
    public static int CountOccurrences(string? haystack, string? needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
