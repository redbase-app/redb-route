using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for IRoutePolicy lifecycle, RoutePolicyFactory, and DSL integration.
/// </summary>
public sealed class RoutePolicyTests
{
    /// <summary>Records all lifecycle method calls in order.</summary>
    private sealed class RecordingPolicy : IRoutePolicy
    {
        public List<string> Calls { get; } = [];

        public Task OnInit(IRouteContext context, CompiledRoute route, CancellationToken ct)
        { Calls.Add("OnInit"); return Task.CompletedTask; }

        public Task OnStart(IRouteContext context, CompiledRoute route, CancellationToken ct)
        { Calls.Add("OnStart"); return Task.CompletedTask; }

        public Task OnStop(IRouteContext context, CompiledRoute route, CancellationToken ct)
        { Calls.Add("OnStop"); return Task.CompletedTask; }

        public Task OnRemove(IRouteContext context, CompiledRoute route, CancellationToken ct)
        { Calls.Add("OnRemove"); return Task.CompletedTask; }

        public Task OnSuspend(IRouteContext context, CompiledRoute route, CancellationToken ct)
        { Calls.Add("OnSuspend"); return Task.CompletedTask; }

        public Task OnResume(IRouteContext context, CompiledRoute route, CancellationToken ct)
        { Calls.Add("OnResume"); return Task.CompletedTask; }

        public Task OnExchangeBegin(IRouteContext context, IExchange exchange, CancellationToken ct)
        { Calls.Add("OnExchangeBegin"); return Task.CompletedTask; }

        public Task OnExchangeDone(IRouteContext context, IExchange exchange, CancellationToken ct)
        { Calls.Add("OnExchangeDone"); return Task.CompletedTask; }
    }

    private sealed class ThrowingOnStartPolicy : IRoutePolicy
    {
        public Task OnInit(IRouteContext context, CompiledRoute route, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnStart(IRouteContext context, CompiledRoute route, CancellationToken ct)
            => throw new InvalidOperationException("OnStart failure");

        public Task OnStop(IRouteContext context, CompiledRoute route, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnRemove(IRouteContext context, CompiledRoute route, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnSuspend(IRouteContext context, CompiledRoute route, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnResume(IRouteContext context, CompiledRoute route, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnExchangeBegin(IRouteContext context, IExchange exchange, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnExchangeDone(IRouteContext context, IExchange exchange, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class TestFactory : IRoutePolicyFactory
    {
        private readonly Func<IRouteContext, string, RouteDefinition, IRoutePolicy?> _creator;
        public int CallCount { get; private set; }

        public TestFactory(Func<IRouteContext, string, RouteDefinition, IRoutePolicy?> creator)
            => _creator = creator;

        public IRoutePolicy? CreateRoutePolicy(IRouteContext context, string routeId, RouteDefinition definition)
        {
            CallCount++;
            return _creator(context, routeId, definition);
        }
    }

    private static RouteContext CreateContext() => new(
        options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

    // ── Lifecycle order ──

    [Fact]
    public async Task Policy_FullLifecycle_CallsInCorrectOrder()
    {
        var policy = new RecordingPolicy();
        var ctx = CreateContext();

        ctx.AddRoutes(r =>
        {
            r.From("direct://policy-lifecycle")
             .RouteId("pol-lc")
             .RoutePolicy(policy)
             .Process(e => { });
        });

        await ctx.Start();

        // Send one exchange to trigger OnExchangeBegin/OnExchangeDone
        var producer = ctx.GetEndpoint("direct://policy-lifecycle").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")), CancellationToken.None);

        await ctx.Stop();

        policy.Calls.Should().ContainInOrder(
            "OnInit", "OnStart",
            "OnExchangeBegin", "OnExchangeDone",
            "OnSuspend", "OnStop", "OnRemove");
    }

    [Fact]
    public async Task Policy_NoExchange_SkipsExchangeHooks()
    {
        var policy = new RecordingPolicy();
        var ctx = CreateContext();

        ctx.AddRoutes(r =>
        {
            r.From("direct://no-exchange")
             .RouteId("no-ex")
             .RoutePolicy(policy)
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        policy.Calls.Should().ContainInOrder("OnInit", "OnStart", "OnSuspend", "OnStop", "OnRemove");
        policy.Calls.Should().NotContain("OnExchangeBegin");
        policy.Calls.Should().NotContain("OnExchangeDone");
    }

    // ── OnStart failure ──

    [Fact]
    public async Task Policy_OnStartThrows_RouteStaysStoppedOnManualStart()
    {
        var policy = new ThrowingOnStartPolicy();
        var ctx = CreateContext();

        ctx.AddRoutes(r =>
        {
            r.From("direct://start-fail")
             .RouteId("sf-route")
             .RoutePolicy(policy)
             .AutoStart(false) // don't auto-start
             .Process(e => { });
        });

        await ctx.Start();

        // Manual start should throw because OnStart throws
        var act = () => ctx.StartRoute("sf-route");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("OnStart failure");

        var route = ctx.GetRoute("sf-route");
        route.Should().NotBeNull();
        route!.Status.Should().Be(RouteStatus.Errored);

        await ctx.Stop();
    }

    // ── DSL ──

    [Fact]
    public void DSL_Cluster_SetsFlag()
    {
        var def = new RouteDefinition();
        def.GetCluster().Should().BeFalse();

        def.Cluster();
        def.GetCluster().Should().BeTrue();

        def.Cluster(false);
        def.GetCluster().Should().BeFalse();
    }

    [Fact]
    public void DSL_RoutePolicy_SetsPolicy()
    {
        var policy = new RecordingPolicy();
        var def = new RouteDefinition();

        def.GetRoutePolicy().Should().BeNull();

        def.RoutePolicy(policy);
        def.GetRoutePolicy().Should().BeSameAs(policy);
    }

    [Fact]
    public void DSL_ExplicitPolicy_OverridesCluster()
    {
        var policy = new RecordingPolicy();
        var def = new RouteDefinition();

        def.Cluster(true);
        def.RoutePolicy(policy);

        // Explicit policy should be used even if Cluster(true) is set
        def.GetRoutePolicy().Should().BeSameAs(policy);
        def.GetCluster().Should().BeTrue();
    }

    // ── Factory chain ──

    [Fact]
    public async Task Factory_FirstNonNullWins()
    {
        var policy = new RecordingPolicy();
        var factory1 = new TestFactory((_, _, _) => null); // returns null
        var factory2 = new TestFactory((_, _, _) => policy); // returns policy
        var factory3 = new TestFactory((_, _, _) => new RecordingPolicy()); // should not be called

        var ctx = CreateContext();
        ctx.AddRoutePolicyFactory(factory1);
        ctx.AddRoutePolicyFactory(factory2);
        ctx.AddRoutePolicyFactory(factory3);

        ctx.AddRoutes(r =>
        {
            r.From("direct://factory-chain")
             .RouteId("fc-route")
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        factory1.CallCount.Should().Be(1);
        factory2.CallCount.Should().Be(1);
        factory3.CallCount.Should().Be(0, "third factory should not be called when second returned non-null");

        // Verify the winning policy received lifecycle calls
        policy.Calls.Should().Contain("OnInit");
    }

    [Fact]
    public async Task ExplicitPolicy_SkipsFactory()
    {
        var explicitPolicy = new RecordingPolicy();
        var factory = new TestFactory((_, _, _) => new RecordingPolicy());

        var ctx = CreateContext();
        ctx.AddRoutePolicyFactory(factory);

        ctx.AddRoutes(r =>
        {
            r.From("direct://explicit-policy")
             .RouteId("ep-route")
             .RoutePolicy(explicitPolicy)
             .Process(e => { });
        });

        await ctx.Start();
        await ctx.Stop();

        factory.CallCount.Should().Be(0, "factory should not be called when explicit policy is set");
        explicitPolicy.Calls.Should().Contain("OnInit");
    }

    [Fact]
    public async Task NoPolicy_NoFactory_RouteStartsNormally()
    {
        var ctx = CreateContext();

        ctx.AddRoutes(r =>
        {
            r.From("direct://no-policy")
             .RouteId("np-route")
             .Process(e => { });
        });

        await ctx.Start();

        var route = ctx.GetRoute("np-route");
        route.Should().NotBeNull();
        route!.Status.Should().Be(RouteStatus.Started);

        await ctx.Stop();
    }

    // ── Per-exchange hooks with exception ──

    [Fact]
    public async Task Policy_OnExchangeDone_CalledEvenOnException()
    {
        var policy = new RecordingPolicy();
        var ctx = CreateContext();

        ctx.AddRoutes(r =>
        {
            r.From("direct://exception-exchange")
             .RouteId("ex-exc")
             .RoutePolicy(policy)
             .Process(e => throw new InvalidOperationException("boom"));

            r.OnException<InvalidOperationException>()
             .Process(e => e.ExceptionHandled = true);
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://exception-exchange").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("test")), CancellationToken.None);

        await ctx.Stop();

        // OnExchangeBegin and OnExchangeDone should both be called
        policy.Calls.Should().Contain("OnExchangeBegin");
        policy.Calls.Should().Contain("OnExchangeDone");

        // OnExchangeBegin should come before OnExchangeDone
        var beginIdx = policy.Calls.IndexOf("OnExchangeBegin");
        var doneIdx = policy.Calls.IndexOf("OnExchangeDone");
        beginIdx.Should().BeLessThan(doneIdx);
    }
}
