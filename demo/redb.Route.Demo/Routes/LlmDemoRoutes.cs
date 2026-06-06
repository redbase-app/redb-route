using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Demo.Routes;

/// <summary>
/// LLM connector showcase. All demos run against the deterministic <c>stub</c>
/// provider so they require no API keys and no network. Each route shows a
/// different way to wire the connector:
///
/// <list type="bullet">
///   <item><c>llm-inline-demo</c> — <c>.Llm("demo-stub")</c> as an inline step.</item>
///   <item><c>llm-endpoint-demo</c> — <c>.To("llm://demo-stub")</c> through the component.</item>
///   <item><c>llm-tool-demo</c> — <c>.AsLlmTool(...)</c> registers a route as a tool descriptor.</item>
/// </list>
///
/// Wiring lives in <see cref="InitRoute.main"/>: the <c>LlmComponent</c> is
/// added to the context, a stub <c>LlmConnectionFactory</c> is placed in the
/// registry, an <c>IAgentEngine</c> and a <c>IToolDescriptorRegistry</c> are
/// registered as services so the inline DSL and the producer can resolve them.
/// </summary>
internal sealed class LlmDemoRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public LlmDemoRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigureInlineCallRoute();
        ConfigureEndpointRoute();
        ConfigureToolRoute();
    }

    /// <summary>
    /// Inline step — timer ticks, the body becomes a user message, the stub
    /// provider echoes it back into <c>exchange.Out.Body</c>. Showcases per-call
    /// configuration via <see cref="LlmCallBuilder"/>.
    /// </summary>
    private void ConfigureInlineCallRoute()
    {
        From("timer://llm-inline?period=30000&delay=15000")
            .RouteId("llm-inline-demo")
            .AutoStart(false)
            .SetBody(_ => "Summarise: redb.Route is an Apache-Camel-class .NET ESB.")
            .Log("[LLM-IL] ▶ user=${body}")
            .Llm("demo-stub", call => call
                .WithSystemPrompt("Be terse.")
                .WithMaxIterations(1)
                .WithTemperature(0.0))
            .Log("[LLM-IL] ◀ provider=${header.llm.provider.id} model=${header.llm.model.id}")
            .Log("[LLM-IL] ◀ tokensIn=${header.llm.tokens.in} tokensOut=${header.llm.tokens.out} stop=${header.llm.stop_reason}")
            .Log("[LLM-IL] ◀ reply=${body}");
    }

    /// <summary>
    /// Endpoint step — same call, but routed through the LLM component. Endpoint
    /// options live in the URI: <c>maxIterations</c>, <c>temperature</c>, etc.
    /// </summary>
    private void ConfigureEndpointRoute()
    {
        From("timer://llm-endpoint?period=30000&delay=20000")
            .RouteId("llm-endpoint-demo")
            .AutoStart(false)
            .SetBody(_ => "What is an EIP wire-tap?")
            .Log("[LLM-EP] ▶ user=${body}")
            .To("llm://demo-stub?maxIterations=1&temperature=0")
            .Log("[LLM-EP] ◀ stop=${header.llm.stop_reason} iters=${header.llm.tool.iterations}")
            .Log("[LLM-EP] ◀ reply=${body}");
    }

    /// <summary>
    /// Tool route — exposes <c>direct:demo-tool</c> as the LLM tool
    /// <c>echo_tool</c>. With a real provider, the agent could call this via
    /// <c>.UseTools("echo_tool")</c>; the stub provider never invokes tools, so
    /// the route just demonstrates the registration shape — calling the
    /// endpoint directly still works as a normal redb.Route route.
    /// </summary>
    private void ConfigureToolRoute()
    {
        From("direct:demo-tool")
            .AsLlmTool("echo_tool")
                .Description("Returns the supplied text unchanged.")
                .Input("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""")
                .SideEffect(ToolSideEffect.ReadOnly)
                .Caching(ToolCachingPolicy.Memoize)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .Log("[LLM-TOOL] ▶ in=${body}")
            .Process(e =>
            {
                e.Out ??= e.In.Clone();
                e.Out.Body = e.In.Body?.ToString() ?? string.Empty;
            })
            .Log("[LLM-TOOL] ◀ out=${body}");
    }
}
