using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine;
using redb.Route.Llm.Providers;

namespace redb.Route.Llm.Extensions;

/// <summary>
/// Inline LLM step for <see cref="IRouteDefinition"/>. Pure syntactic sugar over
/// <c>.Process(Func&lt;IExchange, CancellationToken, Task&gt;)</c> — every option here
/// translates into a single in-memory call to <see cref="IAgentEngine.RunAsync"/>.
/// <para>
/// Use this when the LLM step is an inline transformation in a route, instead of
/// going through a <c>To("llm://...")</c> endpoint. The endpoint form is preferred
/// for routes that mix transports (e.g. <c>kafka → llm → sql</c>); the inline form
/// is preferred for one-off prompts that don't need a registered factory.
/// </para>
/// </summary>
public static class LlmRouteDefinitionExtensions
{
    /// <summary>
    /// Inline LLM step. Reads the user message from <c>exchange.In.Body</c>, runs the
    /// agent engine, writes the assistant text to <c>exchange.Out.Body</c>.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="connectionFactoryName">Name of the registered <see cref="LlmConnectionFactory"/>.</param>
    /// <param name="configure">Optional per-call options.</param>
    public static IRouteDefinition Llm(
        this IRouteDefinition route,
        string connectionFactoryName,
        Action<LlmCallBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionFactoryName);

        var builder = new LlmCallBuilder();
        configure?.Invoke(builder);

        return route.Process(async (exchange, ct) =>
        {
            var sp = exchange.ServiceProvider
                ?? throw new InvalidOperationException(
                    "Inline .Llm(...) requires an IServiceProvider on the exchange. " +
                    "Ensure the route is hosted by RouteHostedService or call AddRedbRouteLlm() in DI.");

            var context = sp.GetService<IRouteContext>()
                ?? throw new InvalidOperationException(
                    "Inline .Llm(...) requires an IRouteContext in DI.");

            var factory = context.GetFromRegistry<LlmConnectionFactory>(connectionFactoryName)
                ?? throw new InvalidOperationException(
                    $"LlmConnectionFactory '{connectionFactoryName}' is not registered. " +
                    "Register via context.AddToRegistry(name, factory).");

            var engine = context.GetService<IAgentEngine>() ?? new AgentEngine();
            var registry = context.GetService<IToolDescriptorRegistry>();

            var userContent = builder.UserContentFactory is { } ucf
                ? ucf(exchange)
                : DefaultUserContent(exchange);

            var tools = builder.ResolveTools(registry);

            var conversationId = builder.ConversationId
                ?? (builder.ConversationHeaderName is { } hdr
                    ? exchange.In.Headers.TryGetValue(hdr, out var v) ? v?.ToString() : null
                    : null);

            var request = new AgentRequest
            {
                Factory = factory,
                Exchange = exchange,
                UserContent = userContent,
                SystemPrompt = builder.SystemPrompt,
                Tools = tools,
                ConversationId = conversationId,
                MaxIterations = builder.MaxIterations,
                Temperature = builder.Temperature,
                MaxTokens = builder.MaxTokens
            };

            var response = await engine.RunAsync(request, ct).ConfigureAwait(false);

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = response.Text;
            exchange.Out.Headers[LlmHeaders.ProviderId] = factory.Provider;
            exchange.Out.Headers[LlmHeaders.ModelId] = factory.ModelId;
            exchange.Out.Headers[LlmHeaders.TokensIn] = response.Usage.InputTokens;
            exchange.Out.Headers[LlmHeaders.TokensOut] = response.Usage.OutputTokens;
            exchange.Out.Headers[LlmHeaders.ToolIterations] = response.Iterations;
            exchange.Out.Headers[LlmHeaders.StopReason] = response.StopReason.ToString();
        });
    }

    private static IReadOnlyList<LlmContentBlock> DefaultUserContent(IExchange exchange)
    {
        var text = exchange.In.Body switch
        {
            null => string.Empty,
            string s => s,
            var x => x.ToString() ?? string.Empty
        };
        return [new LlmTextBlock(text)];
    }
}

/// <summary>Builder for the inline <see cref="LlmRouteDefinitionExtensions.Llm"/> step.</summary>
public sealed class LlmCallBuilder
{
    /// <summary>System prompt for this call.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Per-call temperature override.</summary>
    public double? Temperature { get; set; }

    /// <summary>Per-call max-tokens override.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Conversation id (when conversation tracking is on).</summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// When set, conversation id is read from this header on the incoming
    /// exchange (only used when <see cref="ConversationId"/> is null).
    /// </summary>
    public string? ConversationHeaderName { get; set; }

    /// <summary>Maximum tool-loop iterations.</summary>
    public int MaxIterations { get; set; } = 8;

    /// <summary>Tools available for this call. Empty when no opt-in was made.</summary>
    public List<ILlmToolDescriptor> Tools { get; } = [];

    /// <summary>Tool names to resolve from the registry at dispatch time.</summary>
    public List<string> ToolNames { get; } = [];

    /// <summary>When true, every descriptor in the registry is exposed for this call.</summary>
    public bool UseAllTools { get; set; }

    /// <summary>
    /// Optional factory for the initial user content. When null, the body of
    /// <c>exchange.In</c> is wrapped in a single <see cref="LlmTextBlock"/>.
    /// </summary>
    public Func<IExchange, IReadOnlyList<LlmContentBlock>>? UserContentFactory { get; set; }

    /// <summary>Sets the system prompt.</summary>
    public LlmCallBuilder WithSystemPrompt(string prompt) { SystemPrompt = prompt; return this; }

    /// <summary>Sets per-call temperature.</summary>
    public LlmCallBuilder WithTemperature(double t) { Temperature = t; return this; }

    /// <summary>Sets per-call max-tokens.</summary>
    public LlmCallBuilder WithMaxTokens(int n) { MaxTokens = n; return this; }

    /// <summary>Sets max tool-loop iterations.</summary>
    public LlmCallBuilder WithMaxIterations(int n) { MaxIterations = n; return this; }

    /// <summary>Sets the conversation id explicitly.</summary>
    public LlmCallBuilder WithConversation(string id) { ConversationId = id; return this; }

    /// <summary>Reads the conversation id from the named header on the inbound exchange.</summary>
    public LlmCallBuilder WithConversationFromHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        ConversationHeaderName = headerName;
        return this;
    }

    /// <summary>Adds a tool to this call by descriptor instance.</summary>
    public LlmCallBuilder WithTool(ILlmToolDescriptor tool) { Tools.Add(tool); return this; }

    /// <summary>Adds multiple tools at once by descriptor instance.</summary>
    public LlmCallBuilder WithTools(params ILlmToolDescriptor[] tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Tools.AddRange(tools);
        return this;
    }

    /// <summary>Resolves tools by name from the global registry at dispatch time.</summary>
    public LlmCallBuilder UseTools(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        ToolNames.AddRange(names);
        return this;
    }

    /// <summary>Exposes every descriptor in the global registry to this call.</summary>
    public LlmCallBuilder UseAllRegisteredTools()
    {
        UseAllTools = true;
        return this;
    }

    internal IReadOnlyList<ILlmToolDescriptor> ResolveTools(IToolDescriptorRegistry? registry)
    {
        if (UseAllTools) return registry?.All() ?? [];

        if (Tools.Count == 0 && ToolNames.Count == 0) return [];

        if (ToolNames.Count == 0) return Tools;

        var result = new List<ILlmToolDescriptor>(Tools.Count + ToolNames.Count);
        result.AddRange(Tools);
        if (registry is not null)
        {
            foreach (var name in ToolNames)
            {
                var d = registry.Get(name);
                if (d is not null) result.Add(d);
            }
        }
        return result;
    }

    /// <summary>
    /// Overrides the user content with a custom factory. Use for multi-block
    /// inputs (e.g. text + tool-result continuations).
    /// </summary>
    public LlmCallBuilder WithUserContent(Func<IExchange, IReadOnlyList<LlmContentBlock>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        UserContentFactory = factory;
        return this;
    }
}
