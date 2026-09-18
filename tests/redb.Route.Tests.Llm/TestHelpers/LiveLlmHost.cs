using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Components;
using redb.Route.Http;
using redb.Route.Llm.Extensions;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Engine.Storage;

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

    private readonly SharedHttpServerManager? _httpManager;

    private LiveLlmHost(RouteContext ctx, IProducerTemplate pt, IAgentEngine engine,
        IToolDescriptorRegistry toolRegistry, SharedHttpServerManager? httpManager = null)
    {
        Context = ctx;
        ProducerTemplate = pt;
        Engine = engine;
        ToolRegistry = toolRegistry;
        _httpManager = httpManager;
    }

    /// <summary>Builds a host with the LLM component, agent engine and tool registry pre-wired.</summary>
    /// <param name="observer">Optional observer — defaults to <see cref="NoopAgentObserver"/>. Tests pass a spy
    /// when they need to assert on tool invocations the model performed.</param>
    /// <param name="idempotency">Optional idempotency store — governance tests pass a spy to assert reservations.</param>
    /// <param name="budget">Optional budget enforcer — defaults to the no-op one.</param>
    /// <param name="approval">Optional approval gate — defaults to <see cref="AutoApproveGate"/> (today's shipped default).</param>
    /// <param name="redaction">Optional redaction filter — defaults to the no-op one.</param>
    /// <param name="conversation">Optional conversation store — pass <see cref="InMemoryConversationStore"/> to inspect persisted metadata.</param>
    /// <param name="claimsSource">Optional claims source. Absent means a claim-declaring tool cannot be built at all (fail closed).</param>
    /// <param name="toolCache">Optional cross-run cache store, consulted for <c>ToolCachingPolicy.Persist</c> tools.</param>
    /// <param name="costCalculator">Optional price list. Absent means "this deployment cannot price a call".</param>
    /// <param name="redb">
    /// Optional redb instance registered in the host's container. Needed by integration tests: the redb-backed
    /// stores resolve their service from the exchange's DI scope, so without it they would look for a redb the
    /// host does not have.
    /// </param>
    /// <param name="engineFromDi">
    /// When true the engine is resolved from the container built by <c>AddRedbRouteLlm()</c> instead of being
    /// hand-wired. That is the path every real host takes, and the only one that proves the package wires its
    /// own governance seams.
    /// </param>
    /// <param name="httpHosting">
    /// Optional HTTP hosting options. With <paramref name="httpPort"/> set, the host also carries an
    /// <c>HttpComponent</c>, so a route can start at <c>http:127.0.0.1:{port}/…</c> — the only way to prove
    /// that a caller identified by the transport reaches the engine's governance without a test hook.
    /// </param>
    /// <param name="httpPort">Port for the HTTP entry point; required together with <paramref name="httpHosting"/>.</param>
    public static LiveLlmHost Build(
        IAgentObserver? observer = null,
        IToolIdempotencyStore? idempotency = null,
        IBudgetEnforcer? budget = null,
        IApprovalGate? approval = null,
        IRedactionFilter? redaction = null,
        IConversationStore? conversation = null,
        IToolClaimsSource? claimsSource = null,
        IToolCacheStore? toolCache = null,
        ICostCalculator? costCalculator = null,
        IRedbService? redb = null,
        bool engineFromDi = false,
        HttpHostingOptions? httpHosting = null,
        int? httpPort = null)
    {
        // Closure trick: register IRouteContext as a factory that returns the
        // context we are about to construct. This lets the inline `.Llm(...)`
        // step resolve IRouteContext from exchange.ServiceProvider at runtime.
        RouteContext ctx = null!;
        var services = new ServiceCollection();
        services.AddSingleton<IRouteContext>(_ => ctx);
        if (redb is not null) services.AddSingleton(redb);
        if (engineFromDi) services.AddRedbRouteLlm();
        var sp = services.BuildServiceProvider();

        ctx = new RouteContext(sp, contextId: "llm-test-ctx");

        ctx.AddComponent(new LlmComponent());

        // Optional HTTP entry point: the transport identifies the caller and puts the principal on the
        // exchange, exactly as a real deployment does — no test-side hook touches the properties.
        SharedHttpServerManager? httpManager = null;
        if (httpPort is { } port)
        {
            httpManager = new SharedHttpServerManager(httpHosting);
            ctx.AddComponent(new HttpComponent { ServerManager = httpManager });
        }

        // One registry must serve the route DSL and the endpoint that projects the tools. With the engine
        // coming from DI, the LLM extension already registered one — using the host's own instance as well
        // would give the DSL and the endpoint different registries, and the model would see no tools at all.
        var toolRegistry = engineFromDi
            ? sp.GetRequiredService<IToolDescriptorRegistry>()
            : new ToolDescriptorRegistry();
        ctx.AddService(typeof(IToolDescriptorRegistry), toolRegistry);

        // The claims source must be visible to the route context, not only to the engine: the DSL
        // refuses to build a claim-declaring tool when no source is registered (fail closed).
        if (claimsSource is not null)
            ctx.AddService(typeof(IToolClaimsSource), claimsSource);

        var pt = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), pt);

        var engine = engineFromDi
            ? sp.GetRequiredService<IAgentEngine>()
            : new AgentEngine(
                logger: null,
                producerTemplate: pt,
                observer: observer ?? new NoopAgentObserver(),
                budget: budget ?? new NoopBudgetEnforcer(),
                approval: approval ?? new AutoApproveGate(),
                redaction: redaction ?? new NoopRedactionFilter(),
                shadow: new NoopShadowRunner(),
                conversation: conversation,
                idempotency: idempotency,
                approvalStore: null,
                claimsSource: claimsSource,
                toolCache: toolCache,
                costCalculator: costCalculator);
        // A real host gets its engine from the container; putting it in the context locator as well would
        // mask whether the endpoint can find what the package registered — the very gap these tests exist for.
        if (!engineFromDi) ctx.AddService(typeof(IAgentEngine), engine);

        return new LiveLlmHost(ctx, pt, engine, toolRegistry, httpManager);
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
    /// <param name="endpointUri">Endpoint to send to.</param>
    /// <param name="body">Message body.</param>
    /// <param name="headers">Optional inbound headers.</param>
    /// <param name="configure">
    /// Optional hook running on the exchange before it enters the route. Used by tests that need state a
    /// real transport would have produced — the caller's principal in the exchange properties, for
    /// instance.
    /// </param>
    public async Task<IExchange> SendAsync(string endpointUri, string body,
        IDictionary<string, object?>? headers = null, Action<IExchange>? configure = null)
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
        configure?.Invoke(ex);
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
        if (_httpManager is not null) await _httpManager.DisposeAsync().ConfigureAwait(false);
    }
}
