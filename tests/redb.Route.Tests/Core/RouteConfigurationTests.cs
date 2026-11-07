using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Route configurations (Apache Camel's Route Configuration): <c>OnException</c> declared once, apart from the routes,
/// for the routes of any builder of the context. The default configuration (no id) is for the routes that have no
/// configurations assigned; a configuration with an id is for the routes that name it. A configuration's handler has
/// everything a builder's has — redeliveries, <c>OnWhen</c>, the handler's steps.
/// </summary>
public class RouteConfigurationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // ── The default configuration ──

    [Fact]
    public async Task TheDefaultConfiguration_HandlesTheFailuresOfEveryBuildersRoutes()
    {
        // The layout this is for: handlers in a class of their own, routes in others.
        var handled = new List<string?>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(new Handlers(handled));
        ctx.AddRoutes(a => a.From("direct://cfg-a").RouteId("cfg-a").Process(_ => throw new InvalidOperationException("a")));
        ctx.AddRoutes(b => b.From("direct://cfg-b").RouteId("cfg-b").Process(_ => throw new InvalidOperationException("b")));
        await ctx.Start();

        var a = await Send(ctx, "direct://cfg-a");
        var b = await Send(ctx, "direct://cfg-b");

        handled.Should().Equal("cfg-a", "cfg-b");
        a.Exception.Should().BeNull("the configuration's handler handled the failure");
        b.Exception.Should().BeNull();
    }

    [Fact]
    public async Task AConfigurationHandler_Redelivers()
    {
        var attempts = 0;
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration(c => c.OnException<InvalidOperationException>()
            .MaximumRedeliveries(2).RedeliveryDelay(TimeSpan.FromMilliseconds(1)).Handled());
        ctx.AddRoutes(r => r.From("direct://cfg-retry").Process(_ =>
        {
            if (Interlocked.Increment(ref attempts) < 3)
                throw new InvalidOperationException("not yet");
        }));
        await ctx.Start();

        var exchange = await Send(ctx, "direct://cfg-retry");

        attempts.Should().Be(3, "the first attempt and two redeliveries");
        exchange.Exception.Should().BeNull();
    }

    [Fact]
    public async Task AConfigurationHandler_HonoursOnWhen()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration(c => c.OnException<InvalidOperationException>()
            .OnWhen(e => e.RouteId == "cfg-when-yes").Handled().Process(_ => Interlocked.Increment(ref hits)));
        ctx.AddRoutes(r =>
        {
            r.From("direct://cfg-when-yes").RouteId("cfg-when-yes").Process(_ => throw new InvalidOperationException("x"));
            r.From("direct://cfg-when-no").RouteId("cfg-when-no").Process(_ => throw new InvalidOperationException("x"));
        });
        await ctx.Start();

        var yes = await Send(ctx, "direct://cfg-when-yes");
        var no = await Send(ctx, "direct://cfg-when-no");

        hits.Should().Be(1);
        yes.Exception.Should().BeNull();
        no.Exception.Should().BeOfType<InvalidOperationException>("the guard did not pass for this route");
    }

    [Fact]
    public async Task AFailureInADetachedBody_ReachesTheConfiguration()
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration(c => c.OnException<InvalidOperationException>()
            .Handled().Process(_ => handled.TrySetResult()));
        ctx.AddRoutes(r => r.From("direct://cfg-threads").Threads(1)
            .Process(_ => throw new InvalidOperationException("after the caller returned")));
        await ctx.Start();

        await Send(ctx, "direct://cfg-threads");

        await handled.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task ARestart_KeepsTheConfiguration()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration(c => c.OnException<InvalidOperationException>()
            .Handled().Process(_ => Interlocked.Increment(ref hits)));
        ctx.AddRoutes(r => r.From("direct://cfg-restart").Process(_ => throw new InvalidOperationException("x")));
        await ctx.Start();
        await ctx.Stop();
        await ctx.Start();

        await Send(ctx, "direct://cfg-restart");

        hits.Should().Be(1);
    }

    // ── Configurations with an id ──

    [Fact]
    public async Task AConfigurationWithAnId_IsForTheRoutesThatNameIt()
    {
        var hits = new List<string?>();
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration("audit", c => c.OnException<InvalidOperationException>()
            .Handled().Process(e => { lock (hits) hits.Add(e.RouteId); }));
        ctx.AddRoutes(r =>
        {
            r.From("direct://cfg-named").RouteId("cfg-named").RouteConfigurationId("audit")
                .Process(_ => throw new InvalidOperationException("x"));
            r.From("direct://cfg-unnamed").RouteId("cfg-unnamed")
                .Process(_ => throw new InvalidOperationException("x"));
        });
        await ctx.Start();

        var named = await Send(ctx, "direct://cfg-named");
        var unnamed = await Send(ctx, "direct://cfg-unnamed");

        hits.Should().Equal("cfg-named");
        named.Exception.Should().BeNull();
        unnamed.Exception.Should().BeOfType<InvalidOperationException>("the route did not ask for the configuration");
    }

    [Fact]
    public async Task ARouteWithAssignedConfigurations_DoesNotGetTheDefaultOne()
    {
        // Camel: the nameless configuration is the default for routes which have NOT been assigned configurations.
        var byDefault = 0;
        var byAudit = 0;
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration(c => c.OnException<Exception>().Handled().Process(_ => Interlocked.Increment(ref byDefault)));
        ctx.AddRouteConfiguration("audit", c => c.OnException<IOException>().Handled().Process(_ => Interlocked.Increment(ref byAudit)));
        ctx.AddRoutes(r => r.From("direct://cfg-assigned").RouteConfigurationId("audit")
            .Process(_ => throw new InvalidOperationException("not an IOException")));
        await ctx.Start();

        var exchange = await Send(ctx, "direct://cfg-assigned");

        byDefault.Should().Be(0);
        byAudit.Should().Be(0);
        exchange.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Theory]
    [InlineData("audit,retry", 2)]
    [InlineData("au*", 1)]
    [InlineData("(audit|retry)", 2)]
    [InlineData("*", 2)]
    public async Task Ids_SeveralAWildcardOrARegularExpression(string ids, int expectedHandlers)
    {
        var hits = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration("audit", c => c.OnException<IOException>()
            .Handled().Process(_ => { lock (hits) hits.Add("audit"); }));
        ctx.AddRouteConfiguration("retry", c => c.OnException<InvalidOperationException>()
            .Handled().Process(_ => { lock (hits) hits.Add("retry"); }));
        ctx.AddRoutes(r => r.From("direct://cfg-ids").RouteConfigurationId(ids)
            .Process(e => throw (e.In.Body is "io" ? new IOException("io") : new InvalidOperationException("op"))));
        await ctx.Start();

        await Send(ctx, "direct://cfg-ids", "io");
        await Send(ctx, "direct://cfg-ids", "op");

        hits.Should().HaveCount(expectedHandlers);
        hits.Should().Contain("audit");
    }

    [Fact]
    public async Task AnExactIdNoConfigurationHas_FailsStart_NamingTheRoute()
    {
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration("audit", c => c.OnException<Exception>().Handled());
        ctx.AddRoutes(r => r.From("direct://cfg-missing").RouteId("cfg-missing").RouteConfigurationId("audti"));

        var start = () => ctx.Start();

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*cfg-missing*").WithMessage("*'audti'*");
    }

    [Fact]
    public async Task ADuplicateId_FailsStart()
    {
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration("audit", c => c.OnException<Exception>().Handled());
        ctx.AddRouteConfiguration("audit", c => c.OnException<IOException>().Handled());
        ctx.AddRoutes(r => r.From("direct://cfg-duplicate"));

        var start = () => ctx.Start();

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Duplicate route configuration id 'audit'*");
    }

    // ── A handlers-only builder that is not a configuration ──

    [Fact]
    public async Task ABuilderWithHandlersAndNoRoutes_IsAWarningAtStart()
    {
        // What a class of shared handlers becomes when it stays a plain RouteBuilder: it applies to nothing, silently.
        var log = new Cluster.CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(log));
        await using var ctx = new RouteContext(loggerFactory: loggerFactory);
        ctx.AddRoutes(new PlainHandlers());
        ctx.AddRoutes(new Handlers([]));
        ctx.AddRoutes(r =>
        {
            r.OnException<IOException>().Handled();
            r.From("direct://cfg-warning").Process(_ => { });
        });
        await ctx.Start();

        var warnings = log.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("declares no routes"))
            .Select(e => e.Message).ToList();
        warnings.Should().ContainSingle("only the plain builder: a configuration is meant to have no routes")
            .Which.Should().Contain(nameof(PlainHandlers)).And.Contain("RouteConfiguration");
    }

    private sealed class PlainHandlers : RouteBuilder
    {
        protected override void Configure() => OnException<InvalidOperationException>().Handled();
    }

    // ── With the builder's and the route's own handlers ──

    [Fact]
    public async Task AConfigurationsHandler_IsUsedInsteadOfTheBuildersHandlerOfTheSameType()
    {
        // Camel: "The onException from route configurations are added after the ones in the RouteBuilder, and are
        // therefore used instead."
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration(c => c.OnException<InvalidOperationException>()
            .Handled().Process(_ => { lock (taken) taken.Add("configuration"); }));
        ctx.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>().Handled().Process(_ => { lock (taken) taken.Add("builder"); });
            r.OnException<IOException>().Handled().Process(_ => { lock (taken) taken.Add("builder-io"); });
            r.From("direct://cfg-over-builder")
                .Process(e => throw (e.In.Body is "io" ? new IOException("io") : new InvalidOperationException("op")));
        });
        await ctx.Start();

        await Send(ctx, "direct://cfg-over-builder", "op");
        await Send(ctx, "direct://cfg-over-builder", "io");

        taken.Should().Equal("configuration", "builder-io");
    }

    [Fact]
    public async Task TheRoutesOwnHandler_WinsATieWithTheConfiguration()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration(c => c.OnException<InvalidOperationException>()
            .Handled().Process(_ => { lock (taken) taken.Add("configuration"); }));
        ctx.AddRoutes(r => r.From("direct://cfg-route-tie")
            .OnException<InvalidOperationException>().Handled().Process(_ => { lock (taken) taken.Add("route"); }).End()
            .Process(_ => throw new InvalidOperationException("x")));
        await ctx.Start();

        await Send(ctx, "direct://cfg-route-tie");

        taken.Should().Equal("route");
    }

    [Fact]
    public async Task ANearerHandlerOfTheConfiguration_WinsOverTheRoutesWiderOne()
    {
        var taken = new List<string>();
        await using var ctx = new RouteContext();
        ctx.AddRouteConfiguration(c => c.OnException<FileNotFoundException>()
            .Handled().Process(_ => { lock (taken) taken.Add("configuration"); }));
        ctx.AddRoutes(r => r.From("direct://cfg-nearer")
            .OnException<Exception>().Handled().Process(_ => { lock (taken) taken.Add("route"); }).End()
            .Process(e => throw (e.In.Body is "file" ? new FileNotFoundException("f") : new InvalidOperationException("op"))));
        await ctx.Start();

        await Send(ctx, "direct://cfg-nearer", "file");
        await Send(ctx, "direct://cfg-nearer", "op");

        taken.Should().Equal("configuration", "route");
    }

    private sealed class Handlers(List<string?> handled) : RouteConfigurationBuilder
    {
        protected override void Configuration()
            => RouteConfiguration().OnException<InvalidOperationException>()
                .Handled().Process(e => { lock (handled) handled.Add(e.RouteId); });
    }

    private static async Task<IExchange> Send(IRouteContext ctx, string uri, object? body = null)
    {
        var producer = ctx.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message { Body = body ?? "x" });
        try { await producer.Process(exchange); }
        catch (Exception ex) { exchange.Exception ??= ex; }
        return exchange;
    }
}
