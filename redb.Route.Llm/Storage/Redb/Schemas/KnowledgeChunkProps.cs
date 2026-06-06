using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// A single embedding-backed knowledge chunk. (<see cref="ChunkId"/>,
/// <see cref="Collection"/>) form the business key; the embedding is stored
/// in <see cref="Embedding"/> as a raw float array — vector search at
/// production scale should swap in a pgvector / Qdrant backend that
/// indexes the array column externally.
/// </summary>
[RedbScheme("LLM Knowledge Chunk")]
public class KnowledgeChunkProps
{
    /// <summary>Stable chunk identifier supplied by the caller — business key.</summary>
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>Optional collection / namespace partition.</summary>
    public string? Collection { get; set; }

    /// <summary>Original chunk text shown to the model when retrieved.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Embedding vector — dimensionality matches the producing model.</summary>
    public float[] Embedding { get; set; } = [];

    /// <summary>Embedding dimensionality (cached at top level for quick sanity checks).</summary>
    public int Dimension { get; set; }

    /// <summary>JSON-serialized metadata (source URL, page, tags ...).</summary>
    public string? MetadataJson { get; set; }

    /// <summary>When the chunk was last upserted.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
