using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Bridges a fixed redb.Route endpoint into the LLM tool surface as an
/// <see cref="ILlmToolDescriptor"/>. The engine forwards the model's JSON
/// input to <see cref="EndpointUri"/> via the host's producer template,
/// inheriting the parent agent route's transaction scope, headers, principal
/// and DI scope.
/// </summary>
public sealed class RouteToolBridge : ILlmToolDescriptor
{
    /// <summary>Permissive default JSON Schema used when no explicit schema is supplied.</summary>
    public const string DefaultInputSchema =
        """{"type":"object","additionalProperties":true}""";

    private readonly string _endpointUri;

    /// <inheritdoc />
    public LlmToolCapability Capability { get; }

    /// <summary>The fixed target endpoint URI this descriptor dispatches to (e.g. <c>direct:order-lookup</c>).</summary>
    public string EndpointUri => _endpointUri;

    /// <summary>Creates a bridge descriptor that forwards calls to <paramref name="endpointUri"/>.</summary>
    /// <param name="capability">Tool capability metadata exposed to the model.</param>
    /// <param name="endpointUri">Target endpoint URI (typically a <c>direct:</c> route).</param>
    public RouteToolBridge(LlmToolCapability capability, string endpointUri)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUri);

        Capability = capability;
        _endpointUri = endpointUri;
    }

    /// <summary>Creates a bridge descriptor from a class decorated with <see cref="ExposeAsLlmToolAttribute"/>.</summary>
    /// <typeparam name="THandler">Handler type carrying the attribute.</typeparam>
    /// <param name="endpointUri">Target endpoint URI that the handler is mounted on.</param>
    public static RouteToolBridge FromAttribute<THandler>(string endpointUri)
    {
        var attr = typeof(THandler).GetCustomAttributes(typeof(ExposeAsLlmToolAttribute), inherit: false)
            .OfType<ExposeAsLlmToolAttribute>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Type {typeof(THandler).FullName} is not decorated with [ExposeAsLlmTool].");

        return new RouteToolBridge(attr.ToCapability(DefaultInputSchema), endpointUri);
    }

    /// <inheritdoc />
    public string BuildEndpointUri(string inputJson, IExchange parentExchange) => _endpointUri;
}
