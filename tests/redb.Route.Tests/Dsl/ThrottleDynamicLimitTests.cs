using System.Diagnostics;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// The throttle limit is computed per message. Until 2026-08-29 <c>ThrottleExpression</c>
/// evaluated its template once at route build against an empty exchange and silently fell back
/// to <c>int.MaxValue</c>, so <c>${header.rate}</c> throttled nothing at all.
/// </summary>
[Collection("ExpressionResolver")]
public class ThrottleDynamicLimitTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IExchange> Send(string uri, int rate)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["rate"] = rate;
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        return exchange;
    }

    /// <summary>
    /// Red before the fix: the limit used to be int.MaxValue whatever the header said, so the
    /// second message was never rejected.
    /// </summary>
    [Fact]
    public async Task ThrottleExpression_ReadsTheLimitFromTheMessage()
    {
        var reached = 0;
        _context.AddRoutes(r => r.From("direct://throttle-dyn-reject")
            .Throttle("${header.rate}", TimeSpan.FromSeconds(10)).RejectOnOverflow()
                .Process(_ => Interlocked.Increment(ref reached))
            .EndThrottle());
        await _context.Start();

        await Send("direct://throttle-dyn-reject", 1);
        await Send("direct://throttle-dyn-reject", 1);

        reached.Should().Be(1, "the second message exceeds a limit of one per period and is rejected");
    }

    /// <summary>The limit belongs to the message: a later message with a larger limit gets in.</summary>
    [Fact]
    public async Task ThrottleExpression_LimitChangesBetweenMessages()
    {
        var reached = 0;
        _context.AddRoutes(r => r.From("direct://throttle-dyn-grow")
            .Throttle("${header.rate}", TimeSpan.FromSeconds(10)).RejectOnOverflow()
                .Process(_ => Interlocked.Increment(ref reached))
            .EndThrottle());
        await _context.Start();

        await Send("direct://throttle-dyn-grow", 1);   // occupies the single slot
        await Send("direct://throttle-dyn-grow", 1);   // rejected: one slot, one occupant
        await Send("direct://throttle-dyn-grow", 3);   // its own limit is three: room for it

        reached.Should().Be(2);
    }

    [Fact]
    public async Task ThrottleExpression_WithExpressionForm_Works()
    {
        var reached = 0;
        _context.AddRoutes(r => r.From("direct://throttle-dyn-expr")
            .Throttle("header.rate * 2", TimeSpan.FromSeconds(10)).RejectOnOverflow()
                .Process(_ => Interlocked.Increment(ref reached))
            .EndThrottle());
        await _context.Start();

        await Send("direct://throttle-dyn-expr", 1);
        await Send("direct://throttle-dyn-expr", 1);
        await Send("direct://throttle-dyn-expr", 1);

        reached.Should().Be(2, "rate 1 * 2 gives two slots per period");
    }

    [Fact]
    public async Task ThrottleExpression_WaitsForASlotWhenNotRejecting()
    {
        var period = TimeSpan.FromMilliseconds(300);
        _context.AddRoutes(r => r.From("direct://throttle-dyn-wait")
            .Throttle("${header.rate}", period)
                .Process(_ => { })
            .EndThrottle());
        await _context.Start();

        var stopwatch = Stopwatch.StartNew();
        await Send("direct://throttle-dyn-wait", 1);
        await Send("direct://throttle-dyn-wait", 1);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(period - TimeSpan.FromMilliseconds(50),
            "the second message waits for the slot released after one period");
    }

    [Fact]
    public async Task ThrottleExpression_WithMalformedTemplate_ThrowsWhileTheRouteIsBuilt()
    {
        _context.AddRoutes(r => r.From("direct://throttle-dyn-broken")
            .Throttle("header.rate >", TimeSpan.FromSeconds(1)).Process(_ => { }).EndThrottle());

        var start = async () => await _context.Start();

        await start.Should().ThrowAsync<ExpressionCompilationException>();
    }

    [Fact]
    public async Task ThrottleExpression_WithNonPositiveLimit_FailsThatExchangeLoudly()
    {
        _context.AddRoutes(r => r.From("direct://throttle-dyn-zero")
            .Throttle("${header.rate}", TimeSpan.FromSeconds(1)).Process(_ => { }).EndThrottle());
        await _context.Start();

        var send = async () => await Send("direct://throttle-dyn-zero", 0);

        await send.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>The fixed-limit form is untouched by the change.</summary>
    [Fact]
    public async Task Throttle_WithFixedLimit_StillRejectsOverflow()
    {
        var reached = 0;
        _context.AddRoutes(r => r.From("direct://throttle-fixed")
            .Throttle(2).Period(TimeSpan.FromSeconds(10)).RejectOnOverflow()
                .Process(_ => Interlocked.Increment(ref reached))
            .EndThrottle());
        await _context.Start();

        for (var i = 0; i < 5; i++)
            await Send("direct://throttle-fixed", 99);

        reached.Should().Be(2);
    }
}
