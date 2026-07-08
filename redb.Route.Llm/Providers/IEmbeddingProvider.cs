namespace redb.Route.Llm.Providers;

/// <summary>
/// Pure transport over an embeddings API — turns text into vectors. The sibling
/// of <see cref="ILlmProvider"/> for the retrieval half of RAG: chunks are
/// embedded on ingest, the query is embedded on search, and
/// <see cref="Engine.Storage.IKnowledgeStore.SearchAsync"/> ranks by cosine
/// similarity. Implementations do protocol work only (serialise, HTTP,
/// deserialise); chunking, storage and ranking live elsewhere.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Provider identifier ("openai", "mistral", "together", ...).</summary>
    string ProviderId { get; }

    /// <summary>Embedding model id this provider was configured with.</summary>
    string ModelId { get; }

    /// <summary>
    /// Embeds each input text and returns one vector per input, in the same order.
    /// An empty <paramref name="inputs"/> returns an empty list.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);

    /// <summary>Convenience single-text embed. Returns an empty array when the provider returns nothing.</summary>
    async Task<float[]> EmbedOneAsync(string input, CancellationToken ct = default)
    {
        var vectors = await EmbedAsync(new[] { input }, ct).ConfigureAwait(false);
        return vectors.Count > 0 ? vectors[0] : Array.Empty<float>();
    }
}
