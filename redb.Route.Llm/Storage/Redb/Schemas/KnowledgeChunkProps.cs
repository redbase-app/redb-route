using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Marker scheme for embedding-backed knowledge chunks. Intentionally
/// <b>property-less</b>: every chunk field lives on the indexed
/// <see cref="redb.Core.Models.Entities.RedbObject"/> base columns, which
/// means a chunk row reads / writes one table (<c>_objects</c>) with zero
/// <c>_props</c> rows.
/// <list type="bullet">
///   <item><c>key</c> — stable chunk id (business key).</item>
///   <item><c>name</c> — collection / namespace partition.</item>
///   <item><c>value_string</c> — original chunk text.</item>
///   <item><c>value_bytes</c> — embedding, encoded as a contiguous
///         <c>float[]</c> byte buffer.</item>
///   <item><c>value_long</c> — embedding dimensionality (sanity check).</item>
///   <item><c>note</c> — JSON-serialized metadata (source URL, page, tags ...).</item>
///   <item><c>date_modify</c> — last-upsert timestamp.</item>
/// </list>
/// For production-scale ANN search (millions of chunks) plug in a Qdrant /
/// pgvector backed implementation that only overrides <c>SearchAsync</c>.
/// </summary>
[RedbScheme("LLM Knowledge Chunk")]
public class KnowledgeChunkProps
{
}
