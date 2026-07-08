using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm.Knowledge;

/// <summary>
/// Ingest transport for the knowledge base. Scheme: <c>knowledge</c>.
/// <para>
/// <c>To("knowledge://&lt;collection&gt;?chunkChars=1000&amp;overlap=100&amp;embed=true")</c> —
/// takes the inbound exchange body as a document, splits it into chunks, optionally
/// embeds them (when an <see cref="Providers.IEmbeddingProvider"/> is registered),
/// and upserts into the <see cref="Engine.Storage.IKnowledgeStore"/>. Producer-only.
/// </para>
/// <example>
/// <code>
/// From("file://docs?include=*.md").To("knowledge://handbook");
/// </code>
/// </example>
/// </summary>
public sealed class KnowledgeComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "knowledge";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new KnowledgeEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new KnowledgeEndpoint(uri, this, options);
    }
}

/// <summary>Options for a <c>knowledge://</c> endpoint (URI query auto-binds by name).</summary>
public sealed class KnowledgeEndpointOptions : EndpointOptions
{
    /// <summary>Target chunk size in characters.</summary>
    public int ChunkChars { get; set; } = 1000;

    /// <summary>Overlap (chars) between consecutive chunks. Must be in <c>[0, ChunkChars)</c>.</summary>
    public int Overlap { get; set; } = 0;

    /// <summary>
    /// Embed chunks on ingest when an <see cref="Providers.IEmbeddingProvider"/> is
    /// available. Set <c>?embed=false</c> to force keyword-only ingest (empty vectors).
    /// </summary>
    public bool Embed { get; set; } = true;

    /// <summary>
    /// Stable document id base — chunk ids are <c>{DocId}#{index}</c>, so a re-ingest
    /// of the same document replaces its chunks in place. Falls back to the
    /// <c>knowledge.doc.id</c> header, then to <c>"doc"</c>.
    /// </summary>
    public string? DocId { get; set; }

    /// <summary>Collection fallback when the URI has no path (<c>knowledge://?collection=...</c>).</summary>
    public string? Collection { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (ChunkChars < 1)
            throw new InvalidOperationException("knowledge://: chunkChars must be >= 1.");
        if (Overlap < 0 || Overlap >= ChunkChars)
            throw new InvalidOperationException("knowledge://: overlap must be in [0, chunkChars).");
    }
}

/// <summary>Knowledge ingest endpoint. URI path = collection name.</summary>
public sealed class KnowledgeEndpoint : EndpointBase<KnowledgeEndpointOptions>
{
    /// <summary>Target collection (URI path, or <c>?collection=</c>).</summary>
    public string Collection { get; }

    /// <summary>Creates the endpoint.</summary>
    public KnowledgeEndpoint(EndpointUri uri, KnowledgeComponent component, KnowledgeEndpointOptions options)
        : base(uri, component, options)
    {
        Collection = !string.IsNullOrWhiteSpace(uri.Path) ? uri.Path : (options.Collection ?? string.Empty);
        if (string.IsNullOrWhiteSpace(Collection))
            throw new InvalidOperationException(
                "knowledge:// requires a collection — use 'knowledge://<collection>' or '?collection=<name>'.");
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new KnowledgeProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) =>
        throw new NotSupportedException(
            "knowledge:// is producer-only (ingest). Use To(\"knowledge://<collection>\").");
}
