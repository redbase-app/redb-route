using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Tools;

namespace redb.Route.Llm.Extensions;

/// <summary>
/// Apache-Camel-style DSL aspect that registers the current route as an
/// <see cref="ILlmToolDescriptor"/> in the global <see cref="IToolDescriptorRegistry"/>.
/// Place <c>.AsLlmTool(name)</c> immediately after <c>.From(uri)</c> — it is route-level
/// metadata, not a step in the pipeline. Close the metadata block with <c>.End()</c>
/// or <c>.Then()</c> to return to the parent <see cref="IRouteDefinition"/>.
/// <example>
/// <code>
/// route.From("direct:order-lookup")
///      .AsLlmTool("get_order")
///          .Description("Returns order details by id.")
///          .Input("""{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}""")
///          .SideEffect(ToolSideEffect.ReadOnly)
///          .Cost(ToolCostClass.Cheap)
///      .Then()
///      .Bean&lt;IOrderService&gt;((svc, ex) =&gt; svc.HandleAsync(ex));
/// </code>
/// </example>
/// </summary>
public static class LlmToolDsl
{
    /// <summary>
    /// Marks the current route's source endpoint as an LLM tool. Returns a metadata
    /// builder; call <c>.End()</c> or <c>.Then()</c> to return to the route.
    /// </summary>
    /// <param name="route">The owning route — must already have <c>.From(uri)</c> set.</param>
    /// <param name="name">Tool name exposed to the model (must be unique per registry).</param>
    public static LlmToolBuilderDefinition AsLlmTool(this IRouteDefinition route, string name)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var fromUri = route.GetFromUri()
            ?? throw new InvalidOperationException(
                $".AsLlmTool('{name}') must follow .From(uri) — the route has no source endpoint yet.");

        return new LlmToolBuilderDefinition(route, name, fromUri);
    }
}

/// <summary>
/// Metadata builder for <see cref="LlmToolDsl.AsLlmTool"/>. Collects the tool capability
/// fields and registers a <see cref="RouteToolBridge"/> in the
/// <see cref="IToolDescriptorRegistry"/> when <see cref="End"/> or <see cref="Then"/> is called.
/// </summary>
public sealed class LlmToolBuilderDefinition
{
    private readonly IRouteDefinition _route;
    private readonly string _name;
    private readonly string _endpointUri;

    private string _description = string.Empty;
    private string _inputSchema = RouteToolBridge.DefaultInputSchema;
    private ToolSideEffect _sideEffect = ToolSideEffect.ReadOnly;
    private ToolCachingPolicy _caching = ToolCachingPolicy.None;
    private ToolCostClass _cost = ToolCostClass.Cheap;
    private bool _requiresApproval;
    private List<string>? _requiredClaims;
    private bool _registered;

    internal LlmToolBuilderDefinition(IRouteDefinition route, string name, string endpointUri)
    {
        _route = route;
        _name = name;
        _endpointUri = endpointUri;
    }

    /// <summary>Sets the human-readable description shown to the model.</summary>
    public LlmToolBuilderDefinition Description(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _description = description;
        return this;
    }

    /// <summary>Sets the JSON Schema describing the tool's input arguments.</summary>
    public LlmToolBuilderDefinition Input(string jsonSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonSchema);
        _inputSchema = jsonSchema;
        return this;
    }

    /// <summary>Declares the tool's side-effect class (read-only / mutating / external).</summary>
    public LlmToolBuilderDefinition SideEffect(ToolSideEffect sideEffect)
    {
        _sideEffect = sideEffect;
        return this;
    }

    /// <summary>Declares the caching policy.</summary>
    public LlmToolBuilderDefinition Caching(ToolCachingPolicy caching)
    {
        _caching = caching;
        return this;
    }

    /// <summary>Declares the cost class — drives budget enforcement.</summary>
    public LlmToolBuilderDefinition Cost(ToolCostClass cost)
    {
        _cost = cost;
        return this;
    }

    /// <summary>
    /// Marks the tool as requiring explicit approval before each call.
    /// The configured <c>IApprovalGate</c> is consulted at dispatch time.
    /// </summary>
    public LlmToolBuilderDefinition RequiresApproval(bool requiresApproval = true)
    {
        _requiresApproval = requiresApproval;
        return this;
    }

    /// <summary>Adds a claim that the calling principal must carry for the tool to fire.</summary>
    public LlmToolBuilderDefinition RequireClaim(string claim)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claim);
        (_requiredClaims ??= []).Add(claim);
        return this;
    }

    /// <summary>
    /// Closes the metadata block, registers the descriptor in the
    /// <see cref="IToolDescriptorRegistry"/> and returns the parent
    /// <see cref="IRouteDefinition"/> so the route pipeline can continue.
    /// </summary>
    public IRouteDefinition End() => Register();

    /// <summary>
    /// Alias for <see cref="End"/> — reads more naturally when chaining the rest
    /// of the route as a continuation (e.g. <c>.AsLlmTool(...).Then().Bean&lt;...&gt;()</c>).
    /// </summary>
    public IRouteDefinition Then() => Register();

    private IRouteDefinition Register()
    {
        if (_registered) return _route;
        _registered = true;

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

        var descriptor = new RouteToolBridge(capability, _endpointUri);

        var registry = ResolveRegistry();
        registry.Register(descriptor);

        return _route;
    }

    private IToolDescriptorRegistry ResolveRegistry()
    {
        if (_route is RouteDefinitionBase<RouteDefinition> rdef && rdef.Context is { } ctx)
        {
            var sp = ctx.GetServiceProvider();
            var fromSp = sp?.GetService<IToolDescriptorRegistry>();
            if (fromSp is not null) return fromSp;

            var fromCtx = ctx.GetService<IToolDescriptorRegistry>();
            if (fromCtx is not null) return fromCtx;
        }

        throw new InvalidOperationException(
            $".AsLlmTool('{_name}') could not resolve IToolDescriptorRegistry. " +
            "Call services.AddRedbRouteLlm() before building the routes.");
    }
}
