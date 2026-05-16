using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for per-exchange processing timeout (Phase 5).
/// </summary>
public sealed class ProcessingTimeoutTests
{
    [Fact]
    public async Task TimeoutProcessor_CompletesNormally_WhenWithinTimeout()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var processor = new TimeoutProcessor(inner, TimeSpan.FromSeconds(5), "test-route");
        var exchange = new Exchange(new Message("hello"));

        await processor.Process(exchange, CancellationToken.None);

        exchange.Exception.Should().BeNull();
        await inner.Received(1).Process(exchange, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TimeoutProcessor_ThrowsExchangeTimedOut_WhenExceedsTimeout()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                await Task.Delay(Timeout.Infinite, ct);
            });

        var processor = new TimeoutProcessor(inner, TimeSpan.FromMilliseconds(50), "slow-route");
        var exchange = new Exchange(new Message("hello"));

        var act = () => processor.Process(exchange, CancellationToken.None);

        await act.Should().ThrowAsync<ExchangeTimedOutException>()
            .Where(e => e.RouteId == "slow-route" && e.Timeout == TimeSpan.FromMilliseconds(50));

        exchange.Exception.Should().BeOfType<ExchangeTimedOutException>();
        exchange.Properties["ExchangeTimedOut"].Should().Be(true);
    }

    [Fact]
    public async Task TimeoutProcessor_PropagatesExternalCancellation()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                await Task.Delay(Timeout.Infinite, ct);
            });

        var processor = new TimeoutProcessor(inner, TimeSpan.FromSeconds(10), "test-route");
        var exchange = new Exchange(new Message("hello"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => processor.Process(exchange, cts.Token);

        // External cancellation should propagate as OperationCanceledException, NOT ExchangeTimedOutException
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TimeoutProcessor_PassesLinkedToken_ToInner()
    {
        CancellationToken receivedToken = default;
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                receivedToken = ci.Arg<CancellationToken>();
                return Task.CompletedTask;
            });

        var processor = new TimeoutProcessor(inner, TimeSpan.FromSeconds(5), "test-route");
        var exchange = new Exchange(new Message("hello"));

        await processor.Process(exchange, CancellationToken.None);

        receivedToken.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    public void ExchangeTimedOutException_ContainsRouteIdAndTimeout()
    {
        var ex = new ExchangeTimedOutException("my-route", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

        ex.RouteId.Should().Be("my-route");
        ex.Timeout.Should().Be(TimeSpan.FromMinutes(2));
        ex.Elapsed.Should().Be(TimeSpan.FromMinutes(2));
        ex.Message.Should().Contain("my-route");
        ex.Message.Should().Contain("120");
        ex.Should().BeAssignableTo<TimeoutException>();
    }

    [Fact]
    public async Task Route_WithProcessingTimeout_TimesOut()
    {
        var timedOut = false;

        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://timeout-dsl")
             .RouteId("timeout-dsl")
             .ProcessingTimeout(TimeSpan.FromMilliseconds(50))
             .Process(async (e, ct) =>
             {
                 await Task.Delay(TimeSpan.FromSeconds(10), ct);
             });

            r.OnException<ExchangeTimedOutException>()
             .Process(e =>
             {
                 timedOut = true;
                 e.ExceptionHandled = true;
             });
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://timeout-dsl").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange, CancellationToken.None);

        await ctx.Stop();
        await ctx.DisposeAsync();

        timedOut.Should().BeTrue();
    }

    [Fact]
    public async Task Route_WithDefaultProcessingTimeout_AppliesGlobally()
    {
        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false,
            DefaultProcessingTimeout = TimeSpan.FromMilliseconds(50)
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://global-timeout")
             .RouteId("global-timeout")
             .Process(async (e, ct) =>
             {
                 await Task.Delay(TimeSpan.FromSeconds(10), ct);
             });

            r.OnException<ExchangeTimedOutException>()
             .Process(e => e.ExceptionHandled = true);
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://global-timeout").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange, CancellationToken.None);

        exchange.Exception.Should().BeOfType<ExchangeTimedOutException>();

        await ctx.Stop();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Route_WithoutTimeout_NoTimeoutApplied()
    {
        var completed = false;

        var ctx = new RouteContext(options: new Configuration.RouteEngineOptions
        {
            EnableTelemetry = false,
            EnableMetrics = false
        });

        ctx.AddRoutes(r =>
        {
            r.From("direct://no-timeout")
             .RouteId("no-timeout")
             .Process(e =>
             {
                 completed = true;
             });
        });

        await ctx.Start();

        var producer = ctx.GetEndpoint("direct://no-timeout").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("test"));
        await producer.Process(exchange, CancellationToken.None);

        completed.Should().BeTrue();
        exchange.Exception.Should().BeNull();

        await ctx.Stop();
        await ctx.DisposeAsync();
    }

    [Fact]
    public void ProcessingTimeout_ThrowsForInvalidValue()
    {
        var def = new RouteDefinition();

        var act = () => def.ProcessingTimeout(TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();

        var act2 = () => def.ProcessingTimeout(TimeSpan.FromMilliseconds(-100));
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ProcessingTimeout_AcceptsInfinite()
    {
        var def = new RouteDefinition();

        var act = () => def.ProcessingTimeout(System.Threading.Timeout.InfiniteTimeSpan);
        act.Should().NotThrow();
    }
}
