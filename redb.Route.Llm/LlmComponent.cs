using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Llm;

/// <summary>
/// LLM transport component for redb.Route.
/// Scheme: <c>llm</c>.
/// <para>
/// URI format: <c>llm://&lt;connectionFactoryName&gt;?temperature=0.2&amp;maxTokens=1024</c>.
/// Producer/consumer behaviour is selected by <c>From</c>/<c>To</c> as in any other
/// connector — there is no separate <c>llm-agent://</c> or <c>llm-stream://</c> scheme.
/// </para>
/// </summary>
public sealed class LlmComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "llm";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new LlmEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new LlmEndpoint(uri, this, options);
    }
}
