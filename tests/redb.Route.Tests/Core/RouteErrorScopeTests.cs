using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Core;

/// <summary>
/// One error scope per route, as in Apache Camel: the route's <c>OnException</c>, its builder's, then the context's
/// error handling (<c>AddLocalExceptionHandler</c> → <c>AddGlobalExceptionHandler</c> → <c>ErrorHandler</c>) for a
/// failure no <c>OnException</c> took. The same scope wraps the detached body of <c>.Threads()</c>, so a failure there
/// gets the full handler configuration. A context handler that does not mark the exchange handled lets the failure go
/// on to the caller, as Camel's DefaultErrorHandler does.
/// </summary>
public class RouteErrorScopeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // ── The detached .Threads() body gets the full OnException configuration ──

    [Fact]
    public async Task Threads_OnWhenFalse_DoesNotTakeTheFailure()
    {
        var hits = 0;
        var errors = new RecordingErrorHandler();
        await using var ctx = new RouteContext { ErrorHandler = errors };
        ctx.AddRoutes(r => r
            .From("direct://scope-threads-onwhen")
            .OnException<InvalidOperationException>()
                .OnWhen(_ => false)
                .Handled()
                .Process(_ => Interlocked.Increment(ref hits))
            .End()
            .Threads(2)
                .Process((IExchange _, CancellationToken _) => throw new InvalidOperationException("boom"))
            .End());
        await ctx.Start();

        await Send(ctx, "direct://scope-threads-onwhen");

        await WaitFor(() => errors.Count == 1);
        Volatile.Read(ref hits).Should().Be(0, "OnWhen is false, so the handler does not take the failure");
    }

    [Fact]
    public async Task Threads_Redeliveries_RerunTheThreadsBody()
    {
        var runs = 0;
        var handled = 0;
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r
            .From("direct://scope-threads-redelivery")
            .OnException<InvalidOperationException>()
                .MaximumRedeliveries(2)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                .Handled()
                .Process(_ => Interlocked.Increment(ref handled))
            .End()
            .Threads(2)
                .Process((IExchange _, CancellationToken _) =>
                {
                    Interlocked.Increment(ref runs);
                    throw new InvalidOperationException("boom");
                })
            .End());
        await ctx.Start();

        await Send(ctx, "direct://scope-threads-redelivery");

        await WaitFor(() => Volatile.Read(ref handled) == 1);
        Volatile.Read(ref runs).Should().Be(3, "the first run and two redeliveries of the Threads body");
    }

    [Fact]
    public async Task Threads_OnRedeliveryAndUseOriginalBody_Apply()
    {
        var redeliveries = 0;
        var bodies = new ConcurrentQueue<object?>();
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r
            .From("direct://scope-threads-original")
            .OnException<InvalidOperationException>()
                .MaximumRedeliveries(1)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                .UseOriginalBody()
                .OnRedelivery(_ => Interlocked.Increment(ref redeliveries))
                .Handled()
                .Process(e => bodies.Enqueue(e.In.Body))
            .End()
            .Threads(2)
                .Process((IExchange e, CancellationToken _) =>
                {
                    e.In.Body = "changed";
                    throw new InvalidOperationException("boom");
                })
            .End());
        await ctx.Start();

        await Send(ctx, "direct://scope-threads-original", "original");

        await WaitFor(() => !bodies.IsEmpty);
        Volatile.Read(ref redeliveries).Should().Be(1);
        bodies.Should().ContainSingle().Which.Should().Be("original");
    }

    // ── The context layer on an ordinary route ──

    [Fact]
    public async Task ErrorHandler_TakesAFailureNoOnExceptionTook()
    {
        var errors = new RecordingErrorHandler();
        await using var ctx = new RouteContext { ErrorHandler = errors };
        ctx.AddRoutes(r => r
            .From("direct://scope-errorhandler")
            .Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        var exchange = await Send(ctx, "direct://scope-errorhandler");

        errors.Count.Should().Be(1);
        errors.Exceptions.Single().Should().BeOfType<InvalidOperationException>();
        exchange.Exception.Should().BeOfType<InvalidOperationException>(
            "a handler that does not mark the exchange handled lets the failure go on, as Camel's DefaultErrorHandler");
    }

    [Fact]
    public async Task ErrorHandler_ThatMarksTheExchangeHandled_EndsTheFailure()
    {
        var errors = new RecordingErrorHandler { MarkHandled = true };
        await using var ctx = new RouteContext { ErrorHandler = errors };
        ctx.AddRoutes(r => r
            .From("direct://scope-errorhandler-handled")
            .Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        var exchange = await Send(ctx, "direct://scope-errorhandler-handled");

        errors.Count.Should().Be(1);
        exchange.ExceptionHandled.Should().BeTrue("a dead letter channel takes the failure, as in Camel");
    }

    [Fact]
    public async Task ErrorHandler_IsNotCalled_WhenAnOnExceptionTookTheFailure_EvenUnhandled()
    {
        var errors = new RecordingErrorHandler();
        var hits = 0;
        await using var ctx = new RouteContext { ErrorHandler = errors };
        ctx.AddRoutes(r => r
            .From("direct://scope-errorhandler-taken")
            .OnException<InvalidOperationException>()
                .Process(_ => Interlocked.Increment(ref hits))
            .End()
            .Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        await Send(ctx, "direct://scope-errorhandler-taken");

        Volatile.Read(ref hits).Should().Be(1);
        errors.Count.Should().Be(0, "an OnException took it — Handled(false) included, as in Camel");
    }

    [Fact]
    public async Task ErrorHandler_OnATimerRoute()
    {
        var errors = new RecordingErrorHandler();
        await using var ctx = new RouteContext { ErrorHandler = errors };
        ctx.AddRoutes(r => r
            .From("timer://scope-timer?period=50&repeatCount=1")
            .Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        await WaitFor(() => errors.Count == 1);
    }

    [Fact]
    public async Task ErrorHandler_SeesTheReplayCheckpoint()
    {
        var errors = new RecordingErrorHandler();
        await using var ctx = new RouteContext { ErrorHandler = errors };
        ctx.AddRoutes(r => r
            .From("direct://scope-checkpoint")
            .RouteId("scope-checkpoint")
            .Replayable("after-load", m => m.Process(_ => throw new InvalidOperationException("boom"))));
        await ctx.Start();

        await Send(ctx, "direct://scope-checkpoint");

        errors.Count.Should().Be(1);
        errors.Exchanges.Single().Properties.Should().ContainKey(RouteCheckpoint.PropertyKey,
            "the checkpoint a dead letter queue replays from is on the exchange the error handler gets");
    }

    [Fact]
    public async Task GlobalExceptionHandler_RunsOnAnOrdinaryRoute()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddGlobalExceptionHandler<InvalidOperationException>(
            new DelegateProcessor(_ => Interlocked.Increment(ref hits)));
        ctx.AddRoutes(r => r
            .From("direct://scope-global")
            .Process(_ => throw new InvalidOperationException("boom")));
        await ctx.Start();

        await Send(ctx, "direct://scope-global");

        Volatile.Read(ref hits).Should().Be(1);
    }

    [Fact]
    public async Task LocalExceptionHandler_RunsForItsOwnRouteOnly()
    {
        var hits = 0;
        await using var ctx = new RouteContext();
        ctx.AddLocalExceptionHandler("scope-local-a", typeof(InvalidOperationException),
            new DelegateProcessor(_ => Interlocked.Increment(ref hits)));
        ctx.AddRoutes(r =>
        {
            r.From("direct://scope-local-a").RouteId("scope-local-a")
                .Process(_ => throw new InvalidOperationException("a"));
            r.From("direct://scope-local-b").RouteId("scope-local-b")
                .Process(_ => throw new InvalidOperationException("b"));
        });
        await ctx.Start();

        await Send(ctx, "direct://scope-local-b");
        await Send(ctx, "direct://scope-local-a");

        Volatile.Read(ref hits).Should().Be(1);
    }

    [Fact]
    public async Task Threads_AFailureNoOnExceptionTook_ReachesTheErrorHandlerOnce()
    {
        var errors = new RecordingErrorHandler();
        await using var ctx = new RouteContext { ErrorHandler = errors };
        ctx.AddRoutes(r => r
            .From("direct://scope-threads-errorhandler")
            .Threads(2)
                .Process((IExchange _, CancellationToken _) => throw new InvalidOperationException("boom"))
            .End());
        await ctx.Start();

        await Send(ctx, "direct://scope-threads-errorhandler");

        await WaitFor(() => errors.Count >= 1);
        await Task.Delay(200);
        errors.Count.Should().Be(1);
    }

    // ── Helpers ──

    private static async Task<IExchange> Send(IRouteContext ctx, string uri, object? body = null)
    {
        var producer = ctx.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message { Body = body ?? "x" });
        try { await producer.Process(exchange); }
        catch (Exception ex) { exchange.Exception ??= ex; }
        return exchange;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class RecordingErrorHandler : IErrorHandler
    {
        private readonly ConcurrentQueue<(IExchange Exchange, Exception Exception)> _calls = new();

        public bool MarkHandled { get; init; }

        public int Count => _calls.Count;

        public IEnumerable<Exception> Exceptions => _calls.Select(c => c.Exception);

        public IEnumerable<IExchange> Exchanges => _calls.Select(c => c.Exchange);

        public Task HandleError(IExchange exchange, Exception exception, CancellationToken ct = default)
        {
            _calls.Enqueue((exchange, exception));
            if (MarkHandled)
                exchange.ExceptionHandled = true;
            return Task.CompletedTask;
        }
    }
}
