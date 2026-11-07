using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Tests.Core;

/// <summary>
/// The declarative storage verbs (Route-XML Шаг 1): string/Type forms of get/save/delete over
/// a substituted <see cref="IRedbService"/> — no database, pure verb semantics.
/// </summary>
public sealed class RedbStorageDslTests
{
    public sealed class OrderProps
    {
        public string? Value { get; set; }
    }

    private readonly IRedbService _redb = Substitute.For<IRedbService>();
    private readonly IRouteContext _context = Substitute.For<IRouteContext>();

    public RedbStorageDslTests() => _context.GetService<IRedbService>().Returns(_redb);

    private RouteDefinition NewRoute()
    {
        var route = new RouteDefinition();
        route._context = _context;
        return route;
    }

    private async Task<Exchange> RunAsync(RouteDefinition route, object? body = null,
        params (string Key, object? Value)[] headers)
    {
        var exchange = new Exchange();
        exchange.In.Body = body;
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        var processor = route.Outputs[0].CreateProcessor(_context);
        await processor.Process(exchange, CancellationToken.None);
        return exchange;
    }

    // ── RedbGet (typed) ──────────────────────────────────────────────

    [Fact]
    public async Task RedbGet_LoadsById_IntoTheBody()
    {
        var stored = new RedbObject<OrderProps> { Props = new OrderProps { Value = "42" } };
        _redb.LoadAsync<OrderProps>(42L, 10).Returns(stored);
        var route = NewRoute();
        route.RedbGet(typeof(OrderProps), "${header.orderId}");

        var exchange = await RunAsync(route, headers: ("orderId", 42L));

        exchange.In.Body.Should().BeSameAs(stored);
    }

    [Fact]
    public async Task RedbGet_TargetHeader_LeavesTheBodyAlone()
    {
        var stored = new RedbObject<OrderProps>();
        _redb.LoadAsync<OrderProps>(7L, 3).Returns(stored);
        var route = NewRoute();
        route.RedbGet(typeof(OrderProps), "${header.id}", depth: 3, target: "header:order");

        var exchange = await RunAsync(route, body: "untouched", headers: ("id", 7L));

        exchange.In.Body.Should().Be("untouched");
        exchange.In.Headers["order"].Should().BeSameAs(stored);
    }

    [Fact]
    public void RedbGet_UnknownTarget_FailsAtDefinitionTime()
    {
        var act = () => NewRoute().RedbGet(typeof(OrderProps), "${header.id}", target: "cookie:x");

        act.Should().Throw<ArgumentException>().WithMessage("*'cookie:x'*");
    }

    // ── RedbGetJson ──────────────────────────────────────────────────

    [Fact]
    public async Task RedbGetJson_LoadsRawJson_NoClrTypeInvolved()
    {
        _redb.LoadJsonAsync(11L, 2).Returns("{\"id\":11}");
        var route = NewRoute();
        route.RedbGetJson("${header.id}", depth: 2);

        var exchange = await RunAsync(route, headers: ("id", 11L));

        exchange.In.Body.Should().Be("{\"id\":11}");
    }

    // ── RedbSave ─────────────────────────────────────────────────────

    [Fact]
    public async Task RedbSave_RedbObjectBody_SavedAsIs()
    {
        var obj = new RedbObject<OrderProps>();
        var route = NewRoute();
        route.RedbSave();

        var exchange = await RunAsync(route, body: obj);

        await _redb.Received(1).SaveAsync((IRedbObject)obj);
        exchange.In.Body.Should().BeSameAs(obj);
    }

    [Fact]
    public async Task RedbSave_JsonBody_MaterializedThroughTheType()
    {
        var route = NewRoute();
        route.RedbSave(typeof(OrderProps));

        var exchange = await RunAsync(route, body: "{\"properties\":{\"Value\":\"hello\"}}");

        await _redb.Received(1).SaveAsync(Arg.Is<IRedbObject>(o =>
            o is RedbObject<OrderProps> && ((RedbObject<OrderProps>)o).Props.Value == "hello"));
        exchange.In.Body.Should().BeOfType<RedbObject<OrderProps>>();
    }

    [Fact]
    public async Task RedbSave_JsonBodyWithoutType_FailsNamingTheAttribute()
    {
        var route = NewRoute();
        route.RedbSave();

        var act = () => RunAsync(route, body: "{\"properties\":{}}");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*type*");
        await _redb.DidNotReceive().SaveAsync(Arg.Any<IRedbObject>());
    }

    [Fact]
    public async Task RedbSave_ByUnique_GoesThroughSaveByUniqueAsync()
    {
        var obj = new RedbObject<OrderProps>();
        var route = NewRoute();
        route.RedbSave(typeof(OrderProps), byUnique: true);

        await RunAsync(route, body: obj);

        await _redb.Received(1).SaveByUniqueAsync(obj);
        await _redb.DidNotReceive().SaveAsync(Arg.Any<IRedbObject>());
    }

    [Fact]
    public void RedbSave_ByUniqueWithoutType_FailsAtDefinitionTime()
    {
        var act = () => NewRoute().RedbSave(byUnique: true);

        act.Should().Throw<ArgumentException>().WithMessage("*props type*");
    }

    // ── RedbDelete ───────────────────────────────────────────────────

    [Fact]
    public async Task RedbDelete_DeletesById_AndReportsTheOutcome()
    {
        _redb.DeleteAsync(13L).Returns(true);
        var route = NewRoute();
        route.RedbDelete("${header.id}");

        var exchange = await RunAsync(route, body: "kept", headers: ("id", 13L));

        exchange.In.Body.Should().Be("kept");
        exchange.In.Headers["redbDeleted"].Should().Be(true);
    }

    // ── storage= resolves the NAMED service ──────────────────────────

    [Fact]
    public async Task NamedStorage_ResolvesFromTheRegistry()
    {
        var named = Substitute.For<IRedbService>();
        named.DeleteAsync(5L).Returns(true);
        _context.GetFromRegistry<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(Arg.Any<string>())
            .Returns((Microsoft.Extensions.DependencyInjection.IServiceScopeFactory?)null);
        _context.GetFromRegistry<IRedbService>("redb:archive").Returns(named);
        var route = NewRoute();
        route.RedbDelete("${header.id}", storage: "archive");

        await RunAsync(route, headers: ("id", 5L));

        await named.Received(1).DeleteAsync(5L);
        await _redb.DidNotReceive().DeleteAsync(Arg.Any<long>());
    }
}
