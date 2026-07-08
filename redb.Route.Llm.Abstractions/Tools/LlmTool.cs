using redb.Route.Llm.Abstractions.Tools;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Homeless static builder for <see cref="ILlmToolDescriptor"/>. Use when the
/// tool descriptor is authored outside of an <see cref="redb.Route.Abstractions.IRouteDefinition"/>
/// (e.g. registered directly in DI via <c>services.AddLlmTool(LlmTool.Define(...).Build())</c>),
/// or when the same descriptor needs to be shared across multiple routes.
/// <para>
/// Pairs with the fluent route-level DSL <c>.AsLlmTool(name)</c> (see
/// <see cref="Extensions.LlmToolDsl"/>) and the attribute-driven
/// <see cref="ExposeAsLlmToolAttribute"/> form. All three styles produce the
/// same <see cref="ILlmToolDescriptor"/> contract.
/// </para>
/// <example>
/// <code>
/// var tool = LlmTool.Define("get_order")
///     .EndpointUri("direct:order-lookup")
///     .Description("Returns order details by id.")
///     .Input("""{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}""")
///     .SideEffect(ToolSideEffect.ReadOnly)
///     .Cost(ToolCostClass.Cheap)
///     .Build();
///
/// services.AddLlmTool(tool);
/// </code>
/// </example>
/// </summary>
public static class LlmTool
{
    /// <summary>Starts a fluent definition for a tool descriptor with the given name.</summary>
    /// <param name="name">Tool name exposed to the model (must be unique per registry).</param>
    public static LlmToolStaticBuilder Define(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LlmToolStaticBuilder(name);
    }
}

/// <summary>
/// Fluent builder produced by <see cref="LlmTool.Define"/>. Call
/// <see cref="Build"/> once all metadata has been set to materialise an
/// <see cref="ILlmToolDescriptor"/>.
/// </summary>
public sealed class LlmToolStaticBuilder
{
    private readonly string _name;
    private string? _endpointUri;
    private string _description = string.Empty;
    private string _inputSchema = RouteToolBridge.DefaultInputSchema;
    private ToolSideEffect _sideEffect = ToolSideEffect.ReadOnly;
    private ToolCachingPolicy _caching = ToolCachingPolicy.None;
    private ToolCostClass _cost = ToolCostClass.Cheap;
    private bool _requiresApproval;
    private List<string>? _requiredClaims;

    internal LlmToolStaticBuilder(string name)
    {
        _name = name;
    }

    /// <summary>Sets the target redb.Route endpoint URI (e.g. <c>direct:order-lookup</c>).</summary>
    public LlmToolStaticBuilder EndpointUri(string endpointUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUri);
        _endpointUri = endpointUri;
        return this;
    }

    /// <summary>Sets the human-readable description shown to the model.</summary>
    public LlmToolStaticBuilder Description(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _description = description;
        return this;
    }

    /// <summary>Sets the JSON Schema describing the tool's input arguments.</summary>
    public LlmToolStaticBuilder Input(string jsonSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonSchema);
        _inputSchema = jsonSchema;
        return this;
    }

    /// <summary>Declares the tool's side-effect class.</summary>
    public LlmToolStaticBuilder SideEffect(ToolSideEffect sideEffect)
    {
        _sideEffect = sideEffect;
        return this;
    }

    /// <summary>Declares the caching policy.</summary>
    public LlmToolStaticBuilder Caching(ToolCachingPolicy caching)
    {
        _caching = caching;
        return this;
    }

    /// <summary>Declares the cost class \u2014 drives budget enforcement.</summary>
    public LlmToolStaticBuilder Cost(ToolCostClass cost)
    {
        _cost = cost;
        return this;
    }

    /// <summary>Marks the tool as requiring explicit approval before each call.</summary>
    public LlmToolStaticBuilder RequiresApproval(bool requiresApproval = true)
    {
        _requiresApproval = requiresApproval;
        return this;
    }

    /// <summary>Adds a claim that the calling principal must carry for the tool to fire.</summary>
    public LlmToolStaticBuilder RequireClaim(string claim)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claim);
        (_requiredClaims ??= []).Add(claim);
        return this;
    }

    /// <summary>
    /// Materialises the descriptor. Throws if <see cref="EndpointUri"/> was
    /// not set \u2014 the bridge has nothing to dispatch to without a target URI.
    /// </summary>
    public ILlmToolDescriptor Build()
    {
        if (string.IsNullOrWhiteSpace(_endpointUri))
            throw new InvalidOperationException(
                $"LlmTool.Define('{_name}'): .EndpointUri(uri) must be called before .Build().");

        var capability = new LlmToolCapability
        {
            Name = _name,
            Description = _description,
            InputSchema = _inputSchema,
            Safety = new LlmToolSafety
            {
                SideEffect = _sideEffect,
                Caching = _caching,
                Cost = _cost,
                RequiresApproval = _requiresApproval,
                RequiredClaims = _requiredClaims is { Count: > 0 } cs ? [.. cs] : []
            }
        };

        return new RouteToolBridge(capability, _endpointUri);
    }
}
