using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 F7 — TryCatchDefinition, CatchDefinition, FinallyDefinition.
/// </summary>
public class TryCatchDefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Exchange MakeExchange(object? body = null)
        => new Exchange(new Message { Body = body });

    // ── Structure ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryCatch_ReturnsDefinitionWithParentSet()
    {
        var route = new RouteDefinition();
        var tc = route.TryCatch();

        tc.Should().BeOfType<TryCatchDefinition>();
        tc.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void TryCatch_IsAddedToRouteOutputs()
    {
        var route = new RouteDefinition();
        route.TryCatch();

        route.Outputs.Should().HaveCount(1);
        route.Outputs[0].Should().BeOfType<TryCatchDefinition>();
    }

    [Fact]
    public void Catch_AddsCatchDefinitionToTryCatch()
    {
        var route = new RouteDefinition();
        var tc = route.TryCatch();
        tc.Catch<InvalidOperationException>();

        tc.Catches.Should().HaveCount(1);
        tc.Catches[0].ExceptionType.Should().Be(typeof(InvalidOperationException));
    }

    [Fact]
    public void Catch_ReturnsDefinitionWithParentSet()
    {
        var route = new RouteDefinition();
        var catchDef = route.TryCatch().Catch<IOException>();

        catchDef.Parent.Should().BeOfType<TryCatchDefinition>();
    }

    [Fact]
    public void Finally_SetsFinallyBlock()
    {
        var route = new RouteDefinition();
        var tc = route.TryCatch();
        var fin = tc.Finally();

        tc.FinallyBlock.Should().BeSameAs(fin);
        fin.Parent.Should().BeSameAs(tc);
    }

    [Fact]
    public void Finally_ThrowsIfCalledTwice()
    {
        var tc = new TryCatchDefinition();
        tc.Finally();

        tc.Invoking(t => t.Finally())
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EndTryCatch_ReturnsParentRouteDefinition()
    {
        var route = new RouteDefinition();
        var back = route.TryCatch().EndTryCatch();

        back.Should().BeSameAs(route);
    }

    [Fact]
    public void EndCatch_ReturnsTryCatchDefinition()
    {
        var route = new RouteDefinition();
        var tc = route.TryCatch();
        var back = tc.Catch<IOException>().EndCatch();

        back.Should().BeSameAs(tc);
    }

    [Fact]
    public void EndFinally_ReturnsTryCatchDefinition()
    {
        var route = new RouteDefinition();
        var tc = route.TryCatch();
        var back = tc.Finally().EndFinally();

        back.Should().BeSameAs(tc);
    }

    // ── Runtime — happy path ─────────────────────────────────────────────────

    [Fact]
    public async Task TryCatch_NoException_RunsBody()
    {
        var route = new RouteDefinition();
        route.TryCatch()
            .SetBody("ok")
            .Catch<Exception>().SetBody("caught").EndCatch()
            .EndTryCatch();

        var exchange = MakeExchange();
        var proc = route.CreateProcessor(_context);
        await proc.Process(exchange);

        exchange.In.Body.Should().Be("ok");
    }

    [Fact]
    public async Task TryCatch_MatchingCatch_CatchesException()
    {
        var route = new RouteDefinition();
        route.TryCatch()
            .Process(_ => throw new InvalidOperationException("boom"))
            .Catch<InvalidOperationException>().SetBody("handled").EndCatch()
            .EndTryCatch();

        var exchange = MakeExchange();
        var proc = route.CreateProcessor(_context);
        await proc.Process(exchange);

        exchange.In.Body.Should().Be("handled");
        exchange.ExceptionHandled.Should().BeTrue();
    }

    [Fact]
    public async Task TryCatch_NonMatchingCatch_PropagatesException()
    {
        var route = new RouteDefinition();
        route.TryCatch()
            .Process(_ => throw new ArgumentException("bad arg"))
            .Catch<InvalidOperationException>().SetBody("not this one").EndCatch()
            .EndTryCatch();

        var exchange = MakeExchange();
        var proc = route.CreateProcessor(_context);
        Func<Task> act = () => proc.Process(exchange);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryCatch_Finally_RunsOnSuccess()
    {
        var finallyRan = false;
        var route = new RouteDefinition();
        route.TryCatch()
            .SetBody("ok")
            .Finally().Process(_ => finallyRan = true).EndFinally()
            .EndTryCatch();

        var exchange = MakeExchange();
        var proc = route.CreateProcessor(_context);
        await proc.Process(exchange);

        finallyRan.Should().BeTrue();
        exchange.In.Body.Should().Be("ok");
    }

    [Fact]
    public async Task TryCatch_Finally_RunsEvenWhenExceptionNotCaught()
    {
        var finallyRan = false;
        var route = new RouteDefinition();
        route.TryCatch()
            .Process(_ => throw new InvalidOperationException())
            .Finally().Process(_ => finallyRan = true).EndFinally()
            .EndTryCatch();

        var exchange = MakeExchange();
        var proc = route.CreateProcessor(_context);
        Func<Task> act = () => proc.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>();
        finallyRan.Should().BeTrue();
    }

    [Fact]
    public async Task TryCatch_MultipleCatches_MatchesFirstCompatible()
    {
        var route = new RouteDefinition();
        route.TryCatch()
            .Process(_ => throw new IOException("disk"))
            .Catch<InvalidOperationException>().SetBody("wrong").EndCatch()
            .Catch<IOException>().SetBody("io-caught").EndCatch()
            .EndTryCatch();

        var exchange = MakeExchange();
        var proc = route.CreateProcessor(_context);
        await proc.Process(exchange);

        exchange.In.Body.Should().Be("io-caught");
    }

    [Fact]
    public async Task TryCatch_Finally_RunsAfterCatch()
    {
        var finallyRan = false;
        var route = new RouteDefinition();
        route.TryCatch()
            .Process(_ => throw new InvalidOperationException())
            .Catch<InvalidOperationException>().SetBody("handled").EndCatch()
            .Finally().Process(_ => finallyRan = true).EndFinally()
            .EndTryCatch();

        var exchange = MakeExchange();
        var proc = route.CreateProcessor(_context);
        await proc.Process(exchange);

        exchange.In.Body.Should().Be("handled");
        finallyRan.Should().BeTrue();
    }

    // ── Chained navigation from CatchDefinition ───────────────────────────────

    [Fact]
    public void CatchDefinition_ChainedCatch_AddsToTryCatch()
    {
        var route = new RouteDefinition();
        var tc = route.TryCatch();
        tc.Catch<IOException>()
            .Catch<InvalidOperationException>()
            .EndTryCatch();

        tc.Catches.Should().HaveCount(2);
    }
}
