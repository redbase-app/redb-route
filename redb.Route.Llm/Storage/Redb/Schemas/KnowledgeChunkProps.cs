using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// Marker scheme for embedding-backed knowledge chunks. Intentionally
/// <b>property-less</b>: every chunk field lives on the indexed
/// <see cref="redb.Core.Models.Entities.RedbObject"/> base columns, which
/// means a chunk row reads / writes one table (<c>_objects</c>) with zero
/// <c>_props</c> rows.
/// <list type="bullet">
///   <item><c>value_string</c> — stable chunk id (business key, indexed).</item>
///   <item><c>name</c> — collection / namespace partition.</item>
///   <item><c>note</c> — JSON envelope <c>{"text":"...","meta":"..."}</c>
///         (chunk text plus optional metadata). Keyword search
///         (<c>SearchTextAsync</c>) pushes a <c>LIKE</c> over this column.</item>
///   <item><c>value_bytes</c> — embedding, encoded as a contiguous
///         <c>float[]</c> byte buffer.</item>
///   <item><c>value_long</c> — embedding dimensionality (sanity check).</item>
///   <item><c>date_modify</c> — last-upsert timestamp.</item>
/// </list>
/// For production-scale ANN search (millions of chunks) plug in a Qdrant /
/// pgvector backed implementation that only overrides <c>SearchAsync</c>.
/// </summary>
[RedbScheme("LLM Knowledge Chunk")]
public class KnowledgeChunkProps
{
}
