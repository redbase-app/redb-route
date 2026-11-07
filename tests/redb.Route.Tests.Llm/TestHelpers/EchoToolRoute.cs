using redb.Route.Configuration;
using redb.Route.Llm.Extensions;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Test helper that mounts a <c>direct:</c> route with <c>.AsLlmTool(...)</c>.
/// The route's processor captures every input passed by the agent engine and
/// returns a fixed JSON reply, letting tests assert that:
/// <list type="bullet">
///   <item>The model called the tool (input list non-empty).</item>
///   <item>The model passed the right arguments (input contains expected keys).</item>
///   <item>The agent surfaced the tool result back to the model (final reply mentions the payload).</item>
/// </list>
/// <para>
/// The descriptor lands in <see cref="IToolDescriptorRegistry"/> via the DSL,
/// so the agent picks it up by name from <c>?tools=...</c> or
/// <see cref="LlmCallBuilder.UseTools"/>.
/// </para>
/// </summary>
public sealed class EchoToolRoute : RouteBuilder
{
    private readonly string _toolName;
    private readonly string _description;
    private readonly string _inputSchema;
    private readonly Func<string, string> _replyFactory;
    private readonly ToolSideEffect _sideEffect;
    private readonly ToolCachingPolicy _caching;
    private readonly IReadOnlyList<string> _requiredClaims;
    private readonly List<string> _capturedInputs = new();
    private readonly List<Dictionary<string, object?>> _capturedHeaders = new();
    private readonly List<Dictionary<string, object?>> _capturedProperties = new();
    private readonly List<IServiceProvider?> _capturedServiceProviders = new();
    private readonly List<string?> _capturedRouteIds = new();

    /// <summary>Inputs (raw JSON) the agent has passed to this tool, in call order.</summary>
    public IReadOnlyList<string> CapturedInputs => _capturedInputs;

    /// <summary>Header snapshot of every tool exchange, in call order.</summary>
    public IReadOnlyList<Dictionary<string, object?>> CapturedHeaders => _capturedHeaders;

    /// <summary>Exchange-property snapshot of every tool exchange, in call order.</summary>
    public IReadOnlyList<Dictionary<string, object?>> CapturedProperties => _capturedProperties;

    /// <summary>DI scope provider seen by every tool exchange — reference-compared against the agent exchange's.</summary>
    public IReadOnlyList<IServiceProvider?> CapturedServiceProviders => _capturedServiceProviders;

    /// <summary>Route id seen by every tool exchange, in call order.</summary>
    public IReadOnlyList<string?> CapturedRouteIds => _capturedRouteIds;

    /// <summary>The endpoint URI the descriptor dispatches to (<c>direct:tool-{name}</c>).</summary>
    public string EndpointUri => $"direct:tool-{_toolName}";

    /// <summary>
    /// Creates a tool route that returns <paramref name="replyJson"/> for every call, with the
    /// same optional safety metadata as the delegate overload.
    /// </summary>
    public EchoToolRoute(string toolName, string description, string inputSchema, string replyJson,
        ToolSideEffect sideEffect = ToolSideEffect.ReadOnly,
        ToolCachingPolicy caching = ToolCachingPolicy.None,
        params string[] requiredClaims)
        : this(toolName, description, inputSchema, _ => replyJson, sideEffect, caching, requiredClaims) { }

    /// <summary>
    /// Creates a tool route whose reply depends on the input JSON. The trailing optional
    /// parameters declare the tool's safety metadata — governance tests use them to express
    /// claim requirements, side-effect class and caching policy without a second helper type.
    /// </summary>
    /// <param name="toolName">Tool name exposed to the model.</param>
    /// <param name="description">Human-readable description.</param>
    /// <param name="inputSchema">JSON Schema for the input arguments.</param>
    /// <param name="replyFactory">Builds the reply JSON from the raw input JSON.</param>
    /// <param name="sideEffect">Side-effect class (default <see cref="ToolSideEffect.ReadOnly"/>).</param>
    /// <param name="caching">Caching policy (default <see cref="ToolCachingPolicy.None"/>).</param>
    /// <param name="requiredClaims">Claims the calling principal must carry (default: none).</param>
    public EchoToolRoute(string toolName, string description, string inputSchema, Func<string, string> replyFactory,
        ToolSideEffect sideEffect = ToolSideEffect.ReadOnly,
        ToolCachingPolicy caching = ToolCachingPolicy.None,
        params string[] requiredClaims)
    {
        _toolName = toolName;
        _description = description;
        _inputSchema = inputSchema;
        _replyFactory = replyFactory;
        _sideEffect = sideEffect;
        _caching = caching;
        _requiredClaims = requiredClaims ?? [];
    }

    /// <inheritdoc />
    protected override void Configure()
    {
        var tool = From(EndpointUri)
            .AsLlmTool(_toolName)
                .Description(_description)
                .Input(_inputSchema)
                .SideEffect(_sideEffect)
                .Cost(ToolCostClass.Cheap);

        if (_caching != ToolCachingPolicy.None) tool = tool.Caching(_caching);
        foreach (var claim in _requiredClaims) tool = tool.RequireClaim(claim);

        tool.Then()
            .Process(e =>
            {
                var input = e.In.Body switch
                {
                    string s => s,
                    null => "{}",
                    var x => x.ToString() ?? "{}"
                };
                lock (_capturedInputs)
                {
                    _capturedInputs.Add(input);
                    _capturedHeaders.Add(new Dictionary<string, object?>(e.In.Headers, StringComparer.OrdinalIgnoreCase));
                    _capturedProperties.Add(new Dictionary<string, object?>(e.Properties, StringComparer.OrdinalIgnoreCase));
                    _capturedServiceProviders.Add(e.ServiceProvider);
                    _capturedRouteIds.Add(e.RouteId);
                }

                e.Out ??= e.In.Clone();
                e.Out.Body = _replyFactory(input);
                e.Out.Headers["Content-Type"] = "application/json";
            });
    }
}
