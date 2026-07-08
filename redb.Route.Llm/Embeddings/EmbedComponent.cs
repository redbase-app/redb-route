using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Embeddings;

/// <summary>
/// Embedding transport. Scheme: <c>embed</c>. Producer-only, symmetric with
/// <c>llm://</c>: <c>To("embed://&lt;connectionFactoryName&gt;")</c> turns the
/// exchange body (a text, or a collection of texts) into an embedding vector
/// (<c>float[]</c>) or vectors (<c>float[][]</c>) written to <c>Out.Body</c>.
/// <para>
/// The URI host names an <see cref="LlmConnectionFactory"/> (Provider / BaseUrl /
/// ApiKey / ModelId — set <c>ModelId</c> to the embedding model), so different
/// routes can pick different embedding models by name — the same lever as
/// <c>llm://&lt;factory&gt;</c>.
/// </para>
/// <example>
/// <code>
/// From("kafka://texts").To("embed://openai").To("vector://sink");
/// </code>
/// </example>
/// </summary>
public sealed class EmbedComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "embed";

    /// <summary>
    /// Builds the <see cref="IEmbeddingProvider"/> from a resolved connection
    /// factory. Defaults to <see cref="OpenAiEmbeddingProvider.Create"/>; override
    /// for a custom provider or in tests.
    /// </summary>
    public Func<LlmConnectionFactory, IEmbeddingProvider> ProviderFactory { get; set; } =
        OpenAiEmbeddingProvider.Create;

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new EmbedEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new EmbedEndpoint(uri, this, options);
    }
}

/// <summary>Options for an <c>embed://</c> endpoint.</summary>
public sealed class EmbedEndpointOptions : EndpointOptions
{
    /// <summary>Alternative to the URI host for the connection-factory name.</summary>
    public string? ConnectionFactory { get; set; }

    /// <inheritdoc />
    public override void Validate() { }
}

/// <summary>Embedding endpoint. URI path = connection-factory name.</summary>
public sealed class EmbedEndpoint : EndpointBase<EmbedEndpointOptions>
{
    /// <summary>Connection-factory name (URI path or <c>?connectionFactory=</c>).</summary>
    public string FactoryName { get; }

    /// <summary>Factory resolved from the route context (null when wired later).</summary>
    internal LlmConnectionFactory? ResolvedFactory { get; }

    /// <summary>Creates the endpoint.</summary>
    public EmbedEndpoint(EndpointUri uri, EmbedComponent component, EmbedEndpointOptions options)
        : base(uri, component, options)
    {
        FactoryName = !string.IsNullOrWhiteSpace(uri.Path) ? uri.Path : (options.ConnectionFactory ?? string.Empty);
        if (string.IsNullOrWhiteSpace(FactoryName))
            throw new InvalidOperationException(
                "embed:// requires a connection factory — use 'embed://<factory>' or '?connectionFactory=<name>'.");

        if (component.Context is not null)
            ResolvedFactory = component.Context.GetFromRegistry<LlmConnectionFactory>(FactoryName);
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new EmbedProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) =>
        throw new NotSupportedException("embed:// is producer-only. Use To(\"embed://<factory>\").");
}
