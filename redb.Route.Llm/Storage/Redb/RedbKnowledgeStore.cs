using System.Runtime.InteropServices;
using System.Text.Json;
using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Llm.Storage.Redb;

/// <summary>
/// REDB-backed <see cref="IKnowledgeStore"/>. One row per chunk; every chunk
/// field is stored on the indexed <see cref="RedbObject"/> base columns and
/// the <see cref="KnowledgeChunkProps"/> scheme is intentionally
/// <b>property-less</b> — chunks read / write a single <c>_objects</c> row
/// with zero <c>_props</c> rows. Column layout:
/// <list type="bullet">
///   <item><c>value_string</c> — stable chunk id (business key, indexed).</item>
///   <item><c>name</c> — collection / namespace.</item>
///   <item><c>note</c> — JSON envelope <c>{"text":"...","meta":"..."}</c>.</item>
///   <item><c>value_bytes</c> — embedding, written as the raw byte view of
///         the <c>float[]</c> via <see cref="MemoryMarshal"/>.</item>
///   <item><c>value_long</c> — embedding dimensionality (sanity check).</item>
/// </list>
/// <para>
/// <b>Search strategy:</b> the MVP loads candidate chunks (filtered by
/// collection) and computes cosine similarity in process. Acceptable up to a
/// few thousand chunks. For production scale plug in a pgvector / Qdrant
/// adapter that overrides only <see cref="SearchAsync"/>.
/// </para>
/// <para>
/// The store does not own an <see cref="IRedbService"/> instance — each call
/// resolves one through <c>IRouteContext.GetRedbService(name, exchange)</c>,
/// which honours the per-exchange scope cache. The redb name is read from
/// <c>exchange.Properties[LlmKeys.RedbName]</c> (set by the LLM endpoint URI),
/// falling back to the constructor-supplied default name and then to the host's
/// default unnamed instance.
/// </para>
/// </summary>
public sealed class RedbKnowledgeStore : IKnowledgeStore
{
    private readonly IRouteContext _context;
    private readonly string? _defaultRedbName;

    /// <summary>Creates the store. Scheme is synced by the host's redb.InitializeAsync().</summary>
    public RedbKnowledgeStore(IRouteContext context, string? defaultRedbName = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _defaultRedbName = defaultRedbName;
    }

    private IRedbService Resolve(IExchange? exchange)
    {
        var name = _defaultRedbName;
        if (exchange is not null
            && exchange.Properties.TryGetValue(LlmKeys.RedbName, out var raw)
            && raw is string s && s.Length > 0)
            name = s;
        return _context.GetRedbService(name ?? string.Empty, exchange);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(KnowledgeChunk chunk, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.Text);

        var redb = Resolve(exchange);

        var existing = await redb.Query<KnowledgeChunkProps>()
            .WhereRedb(o => o.ValueString == chunk.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        Populate(existing ?? new RedbObject<KnowledgeChunkProps>(), chunk, out var row);
        await redb.SaveAsync(row).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertManyAsync(IEnumerable<KnowledgeChunk> chunks, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var input = chunks
            .Where(c => c is not null
                        && !string.IsNullOrWhiteSpace(c.Id)
                        && !string.IsNullOrWhiteSpace(c.Text))
            .ToList();
        if (input.Count == 0) return;

        var redb = Resolve(exchange);

        // ONE indexed IN-clause lookup for all keys, then ONE bulk SaveAsync —
        // SaveAsync internally batches inserts/updates as separate streams.
        var keys = input.Select(c => c.Id).ToArray();
        var existingByKey = (await redb.Query<KnowledgeChunkProps>()
                .WhereRedb(o => keys.Contains(o.ValueString))
                .ToListAsync()
                .ConfigureAwait(false))
            .Where(o => o.value_string is not null)
            .ToDictionary(o => o.value_string!, StringComparer.Ordinal);

        var rowsToSave = new List<IRedbObject>(input.Count);

        foreach (var chunk in input)
        {
            ct.ThrowIfCancellationRequested();

            if (existingByKey.TryGetValue(chunk.Id, out var existing))
            {
                // No Props-hash pre-check here: KnowledgeChunkProps is a
                // property-less marker (text/id/embedding live on the base
                // _objects columns, not Props), so ComputeHash() is always empty
                // and would treat every re-upsert as "unchanged" — silently
                // dropping real text/embedding updates. Always save the row;
                // SaveAsync's own change-tracking still diffs the base columns.
                Populate(existing, chunk, out var same);
                rowsToSave.Add(same);
            }
            else
            {
                Populate(new RedbObject<KnowledgeChunkProps>(), chunk, out var fresh);
                rowsToSave.Add(fresh);
            }
        }

        if (rowsToSave.Count == 0) return;
        await redb.SaveAsync(rowsToSave).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string chunkId, IExchange? exchange = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);

        var redb = Resolve(exchange);
        var row = await redb.Query<KnowledgeChunkProps>()
            .WhereRedb(o => o.ValueString == chunkId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (row is not null) await redb.SoftDeleteAsync([row]).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        string? collection = null,
        IExchange? exchange = null,
        CancellationToken ct = default)
    {
        if (queryEmbedding.Length == 0) return [];

        var redb = Resolve(exchange);

        // Pre-filter by collection on the indexed _objects.name column at the
        // SQL layer; the cosine pass is in-process.
        var rows = collection is null
            ? await redb.Query<KnowledgeChunkProps>().ToListAsync().ConfigureAwait(false)
            : await redb.Query<KnowledgeChunkProps>()
                .WhereRedb(o => o.Name == collection)
                .ToListAsync()
                .ConfigureAwait(false);

        if (rows.Count == 0) return [];

        var ranked = new List<KnowledgeSearchResult>(rows.Count);
        var query = queryEmbedding.Span;
        foreach (var row in rows)
        {
            var embedding = DecodeEmbedding(row.value_bytes);
            var score = CosineSimilarity(query, embedding);
            ranked.Add(new KnowledgeSearchResult(MaterializeChunk(row, embedding), score));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .Take(Math.Max(1, topK))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeSearchResult>> SearchTextAsync(
        string query,
        int topK,
        string? collection = null,
        IExchange? exchange = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var redb = Resolve(exchange);

        // Push the substring filter to SQL (LIKE) on the indexed _objects.note
        // column — which holds the {text,meta} envelope. Only matching rows come
        // back; we never load the whole table. Collection stays an indexed
        // _objects.name equality in the same WHERE.
        // Case-insensitive substring → the parser maps Contains(.., OrdinalIgnoreCase)
        // to a server-side ILIKE (ComparisonOperator.ContainsIgnoreCase), so "Mask"
        // matches "mask"/"MASK" in SQL, not just in the in-process re-score below.
        var matches = collection is null
            ? await redb.Query<KnowledgeChunkProps>()
                .WhereRedb(o => o.Note != null && o.Note.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToListAsync()
                .ConfigureAwait(false)
            : await redb.Query<KnowledgeChunkProps>()
                .WhereRedb(o => o.Note != null && o.Note.Contains(query, StringComparison.OrdinalIgnoreCase) && o.Name == collection)
                .ToListAsync()
                .ConfigureAwait(false);

        if (matches.Count == 0) return [];

        // Re-score the (already small) matched set against the DECODED text, not
        // the raw envelope: this ranks by real occurrence count and drops rows
        // where the substring only hit the JSON structure / meta, never the text.
        var ranked = new List<KnowledgeSearchResult>(matches.Count);
        foreach (var row in matches)
        {
            var embedding = DecodeEmbedding(row.value_bytes);
            var chunk = MaterializeChunk(row, embedding);
            var score = KnowledgeTextMatch.CountOccurrences(chunk.Text, query);
            if (score == 0) continue;
            ranked.Add(new KnowledgeSearchResult(chunk, score));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .Take(Math.Max(1, topK))
            .ToArray();
    }

    private static void Populate(RedbObject<KnowledgeChunkProps> row, KnowledgeChunk chunk, out RedbObject<KnowledgeChunkProps> result)
    {
        var embedding = chunk.Embedding.Span;
        // float[] -> raw bytes via MemoryMarshal — zero-copy reinterpret + ToArray()
        // produces the exact little-endian byte sequence we can decode later
        // with MemoryMarshal.Cast.
        var bytes = MemoryMarshal.AsBytes(embedding).ToArray();

        var envelope = new ChunkEnvelope(chunk.Text, chunk.MetadataJson);

        row.value_string = chunk.Id;
        row.name = chunk.Collection ?? string.Empty;
        row.note = JsonSerializer.Serialize(envelope, EnvelopeJsonOptions);
        row.value_bytes = bytes;
        row.value_long = embedding.Length;
        row.date_modify = new DateTimeOffset(DateTime.SpecifyKind(chunk.UpdatedAtUtc, DateTimeKind.Utc));
        result = row;
    }

    private static KnowledgeChunk MaterializeChunk(RedbObject<KnowledgeChunkProps> row, float[] embedding)
    {
        var envelope = TryParseEnvelope(row.note);
        return new KnowledgeChunk
        {
            Id = row.value_string ?? string.Empty,
            Collection = string.IsNullOrEmpty(row.name) ? null : row.name,
            Text = envelope.Text,
            Embedding = embedding,
            MetadataJson = envelope.Meta,
            UpdatedAtUtc = row.date_modify.UtcDateTime
        };
    }

    private static float[] DecodeEmbedding(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return [];
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    private static ChunkEnvelope TryParseEnvelope(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ChunkEnvelope(string.Empty, null);
        try { return JsonSerializer.Deserialize<ChunkEnvelope>(json, EnvelopeJsonOptions) ?? new ChunkEnvelope(string.Empty, null); }
        catch { return new ChunkEnvelope(json, null); }
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

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Store the text as-is (UTF-8), NOT \uXXXX-escaped. The default encoder
        // escapes every non-ASCII char, which would bury Cyrillic / CJK text as
        // "Ма..." in the note column — making the keyword LIKE in
        // SearchTextAsync unable to match a raw non-ASCII query. Relaxed escaping
        // is safe for DB storage (it only skips HTML-sensitive escaping).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record ChunkEnvelope(string Text, string? Meta);
}
