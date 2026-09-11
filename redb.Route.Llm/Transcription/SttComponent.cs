using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Transcription;

/// <summary>
/// Speech-to-text transport. Scheme: <c>stt</c>. Producer-only, symmetric with
/// <c>llm://</c> and <c>embed://</c>: <c>To("stt://&lt;connectionFactoryName&gt;")</c>
/// turns the exchange body (recorded audio as <c>byte[]</c>, <c>ReadOnlyMemory&lt;byte&gt;</c>
/// or a <c>Stream</c>) into the recognised text written to <c>Out.Body</c>.
/// <para>
/// The URI host names an <see cref="LlmConnectionFactory"/> (Provider / BaseUrl /
/// ApiKey / ModelId — set <c>ModelId</c> to the speech model), which is where local
/// and paid recognition part company: point <c>BaseUrl</c> at a whisper server on
/// <c>127.0.0.1</c> and the route costs nothing to run, point it at a paid endpoint
/// and the same route bills. Neither is visible from the route.
/// </para>
/// <example>
/// <code>
/// From("direct://voice")
///     .To(Tg.Download(token))   // attachment file_id → bytes
///     .To("stt://local")        // bytes → text
///     .To("direct://chat");     // and on as an ordinary reply
/// </code>
/// </example>
/// </summary>
public sealed class SttComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "stt";

    /// <summary>
    /// Builds the <see cref="ITranscriptionProvider"/> from a resolved connection
    /// factory. Defaults to <see cref="OpenAiTranscriptionProvider.Create"/>; override
    /// for a custom provider or in tests.
    /// </summary>
    public Func<LlmConnectionFactory, ITranscriptionProvider> ProviderFactory { get; set; } =
        OpenAiTranscriptionProvider.Create;

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new SttEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new SttEndpoint(uri, this, options);
    }
}

/// <summary>Options for an <c>stt://</c> endpoint.</summary>
public sealed class SttEndpointOptions : EndpointOptions
{
    /// <summary>Alternative to the URI host for the connection-factory name.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>
    /// ISO-639-1 language hint passed to the model (<c>ru</c>). Empty lets the model
    /// detect the language, which costs accuracy on short recordings — a few words are
    /// routinely detected as the wrong language. Overridable per exchange with the
    /// <see cref="LlmHeaders.TranscriptionLanguage"/> header.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Decoding hint: names and terms the model would not otherwise spell right.
    /// Off by default, and deliberately so — a hint can also displace what was said.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// Name to upload the audio under. Speech endpoints pick a demuxer by extension, so
    /// this decides whether an Ogg/Opus voice note is decoded or rejected. When unset the
    /// producer derives it from the exchange <c>ContentType</c>, falling back to
    /// <c>audio.ogg</c>.
    /// </summary>
    public string? FileName { get; set; }

    /// <inheritdoc />
    public override void Validate() { }
}

/// <summary>Speech-to-text endpoint. URI path = connection-factory name.</summary>
public sealed class SttEndpoint : EndpointBase<SttEndpointOptions>
{
    /// <summary>Connection-factory name (URI path or <c>?connectionFactory=</c>).</summary>
    public string FactoryName { get; }

    /// <summary>Factory resolved from the route context (null when wired later).</summary>
    internal LlmConnectionFactory? ResolvedFactory { get; }

    /// <summary>Creates the endpoint.</summary>
    public SttEndpoint(EndpointUri uri, SttComponent component, SttEndpointOptions options)
        : base(uri, component, options)
    {
        FactoryName = !string.IsNullOrWhiteSpace(uri.Path) ? uri.Path : (options.ConnectionFactory ?? string.Empty);
        if (string.IsNullOrWhiteSpace(FactoryName))
            throw new InvalidOperationException(
                "stt:// requires a connection factory — use 'stt://<factory>' or '?connectionFactory=<name>'.");

        if (component.Context is not null)
            ResolvedFactory = component.Context.GetFromRegistry<LlmConnectionFactory>(FactoryName);
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new SttProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor) =>
        throw new NotSupportedException("stt:// is producer-only. Use To(\"stt://<factory>\").");
}
