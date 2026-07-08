using Microsoft.Extensions.DependencyInjection;
using redb.Route.Components;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Minimal hand-rolled wiring for live DSL tests. Avoids the
/// <c>RouteHostedService</c> bootstrap (which requires a host) so the test
/// body stays close to a Camel-style example a human can read top-down:
/// build, register factory, declare routes, hit <c>direct:</c>, assert.
/// <para>
/// All public registrations match what <c>services.AddRedbRoute().AddRedbRouteLlm()</c>
/// would do in a real host. The single difference is that we drive
/// <see cref="RouteContext.Start"/> ourselves.
/// </para>
/// </summary>
public sealed class LiveLlmHost : IAsyncDisposable
{
    /// <summary>The shared route context.</summary>
    public RouteContext Context { get; }

    /// <summary>The producer template — used by the agent engine to dispatch tool routes.</summary>
    public IProducerTemplate ProducerTemplate { get; }

    /// <summary>The agent engine wired with the producer template.</summary>
    public IAgentEngine Engine { get; }

    /// <summary>The tool registry shared by every LLM endpoint and inline <c>.Llm()</c> step.</summary>
    public IToolDescriptorRegistry ToolRegistry { get; }

    private LiveLlmHost(RouteContext ctx, IProducerTemplate pt, IAgentEngine engine, IToolDescriptorRegistry toolRegistry)
    {
        Context = ctx;
        ProducerTemplate = pt;
        Engine = engine;
        ToolRegistry = toolRegistry;
    }

    /// <summary>Builds a host with the LLM component, agent engine and tool registry pre-wired.</summary>
    /// <param name="observer">Optional observer — defaults to <see cref="NoopAgentObserver"/>. Tests pass a spy
    /// when they need to assert on tool invocations the model performed.</param>
    public static LiveLlmHost Build(IAgentObserver? observer = null)
    {
        // Closure trick: register IRouteContext as a factory that returns the
        // context we are about to construct. This lets the inline `.Llm(...)`
        // step resolve IRouteContext from exchange.ServiceProvider at runtime.
        RouteContext ctx = null!;
        var services = new ServiceCollection();
        services.AddSingleton<IRouteContext>(_ => ctx);
        var sp = services.BuildServiceProvider();

        ctx = new RouteContext(sp, contextId: "llm-test-ctx");

        ctx.AddComponent(new LlmComponent());

        var toolRegistry = new ToolDescriptorRegistry();
        ctx.AddService(typeof(IToolDescriptorRegistry), toolRegistry);

        var pt = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), pt);

        var engine = new AgentEngine(
            logger: null,
            producerTemplate: pt,
            observer: observer ?? new NoopAgentObserver(),
            budget: new NoopBudgetEnforcer(),
            approval: new AutoApproveGate(),
            redaction: new NoopRedactionFilter(),
            shadow: new NoopShadowRunner(),
            conversation: null,
            idempotency: null,
            approvalStore: null);
        ctx.AddService(typeof(IAgentEngine), engine);

        return new LiveLlmHost(ctx, pt, engine, toolRegistry);
    }

    /// <summary>Registers a connection factory keyed by <paramref name="name"/>.</summary>
    public LiveLlmHost AddFactory(string name, LlmConnectionFactory factory)
    {
        factory.Name = name;
        Context.AddToRegistry(name, factory);
        return this;
    }

    /// <summary>
    /// Declares routes inline and starts the context. The producer template is
    /// started as part of this call so subsequent tool dispatches just work.
    /// </summary>
    public async Task<LiveLlmHost> StartAsync(Action<InlineRouteBuilder> configure)
    {
        Context.AddRoutes(configure);
        await Context.Start().ConfigureAwait(false);
        ((ProducerTemplate)ProducerTemplate).Start();
        return this;
    }

    /// <summary>
    /// Mounts a <see cref="RouteBuilder"/> (e.g. an <see cref="EchoToolRoute"/>)
    /// and starts the context. Use this overload when the builder owns the route
    /// so its descriptor lands in the registry via <c>.AsLlmTool</c>.
    /// </summary>
    public async Task<LiveLlmHost> StartAsync(RouteBuilder routes, Action<InlineRouteBuilder>? extraInline = null)
    {
        Context.AddRoutes(routes);
        if (extraInline is not null) Context.AddRoutes(extraInline);
        await Context.Start().ConfigureAwait(false);
        ((ProducerTemplate)ProducerTemplate).Start();
        return this;
    }

    /// <summary>Sends one message into <paramref name="endpointUri"/> and returns the resulting exchange.</summary>
    public async Task<IExchange> SendAsync(string endpointUri, string body, IDictionary<string, object?>? headers = null)
    {
        var endpoint = Context.GetEndpoint(endpointUri);
        var producer = endpoint.CreateProducer();
        await producer.Start().ConfigureAwait(false);

        var msg = new Message(body);
        if (headers is not null)
            foreach (var kv in headers) msg.Headers[kv.Key] = kv.Value;

        // Construct the exchange with a per-call DI scope so inline .Llm()
        // can resolve IRouteContext from exchange.ServiceProvider.
        var scopeFactory = endpoint.ScopeFactory;
        var ex = Exchange.Create(msg, scopeFactory);
        await producer.Process(ex).ConfigureAwait(false);
        return ex;
    }

    /// <summary>Convenience cast for the mock sink so tests can read its received exchanges.</summary>
    public MockEndpoint Mock(string uri) => (MockEndpoint)Context.GetEndpoint(uri);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync().ConfigureAwait(false);
        if (ProducerTemplate is IDisposable d) d.Dispose();
    }
}
