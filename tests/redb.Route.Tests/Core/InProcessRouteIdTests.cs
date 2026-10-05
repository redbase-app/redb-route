using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// A direct:/direct-vm: call runs the target route's pipeline on the CALLER's exchange, and the target's
/// first step stamps <c>exchange.RouteId</c>, which nothing used to restore: after <c>to("direct:callee")</c>
/// the caller carried the callee's id, so the caller's route span and its later step metrics were attributed
/// to the callee. The caller must get its own id back, as it already does for <c>IExchange.Context</c>.
/// </summary>
public class InProcessRouteIdTests : IAsyncDisposable
{
    private readonly ServiceProvider _sp;

    public InProcessRouteIdTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SharedVmRegistry());
        _sp = services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync()
    {
        _sp.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private RouteContext New(string name) => new(_sp, $"{name}-{Guid.NewGuid():N}");

    private static async Task SendAsync(RouteContext ctx, string uri)
    {
        var producer = ctx.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("x")));
    }

    [Fact]
    public async Task Direct_call_gives_the_caller_its_route_id_back()
    {
        await using var ctx = New("routeid-direct");
        string? insideCallee = null;
        string? afterCall = null;

        ctx.AddRoutes(r =>
        {
            r.From("direct://callee").RouteId("callee").Process(e => insideCallee = e.RouteId);
            r.From("direct://caller").RouteId("caller").To("direct://callee").Process(e => afterCall = e.RouteId);
        });

        await ctx.Start();
        await SendAsync(ctx, "direct://caller");

        insideCallee.Should().Be("callee", "inside the sub-route the current route is the sub-route");
        afterCall.Should().Be("caller", "after the direct: call the caller is attributed to its own route again");
    }

    [Fact]
    public async Task Nested_direct_calls_restore_each_frame()
    {
        await using var ctx = New("routeid-nested");
        string? insideC = null;
        string? insideB = null;
        string? afterB = null;

        ctx.AddRoutes(r =>
        {
            r.From("direct://c").RouteId("c").Process(e => insideC = e.RouteId);
            r.From("direct://b").RouteId("b").To("direct://c").Process(e => insideB = e.RouteId);
            r.From("direct://a").RouteId("a").To("direct://b").Process(e => afterB = e.RouteId);
        });

        await ctx.Start();
        await SendAsync(ctx, "direct://a");

        insideC.Should().Be("c");
        insideB.Should().Be("b", "when C returns, B is the current route again");
        afterB.Should().Be("a", "when B returns, A is the current route again");
    }

    [Fact]
    public async Task A_failing_sub_route_still_gives_the_caller_its_route_id_back()
    {
        await using var ctx = New("routeid-fail");
        string? caughtInCaller = null;

        ctx.AddRoutes(r =>
        {
            r.From("direct://boom").RouteId("boom").Process(_ => throw new InvalidOperationException("boom"));
            r.From("direct://caller").RouteId("caller")
                .TryCatch()
                    .To("direct://boom")
                .Catch<InvalidOperationException>()
                    .Process(e => caughtInCaller = e.RouteId)
                .EndTryCatch();
        });

        await ctx.Start();
        await SendAsync(ctx, "direct://caller");

        caughtInCaller.Should().Be("caller",
            "the caller's id is restored on the way out, before its own error handler runs");
    }

    [Fact]
    public async Task DirectVm_call_gives_the_caller_its_route_id_back()
    {
        await using var ctx = New("routeid-directvm");
        string? afterCall = null;

        ctx.AddRoutes(r =>
        {
            r.From("direct-vm://callee").RouteId("vm-callee").Process(_ => { });
            r.From("direct://caller").RouteId("vm-caller").To("direct-vm://callee").Process(e => afterCall = e.RouteId);
        });

        await ctx.Start();
        await SendAsync(ctx, "direct://caller");

        afterCall.Should().Be("vm-caller");
    }
}
