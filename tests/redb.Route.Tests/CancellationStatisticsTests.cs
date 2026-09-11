using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests;

/// <summary>
/// A cooperative cancellation is not a route error (BR-10). A dashboard that closes mid-poll
/// cancels the caller's token; the exchange is abandoned, not failed — yet the statistics wrapper
/// counted it into <c>Errors</c>, so a healthy management route turned red on the Error-Prone
/// panel, and endpoint health degraded for five minutes per closed browser tab.
/// <para>
/// The rule, applied at every counting and event site: an <see cref="OperationCanceledException"/>
/// with the caller's token cancelled is a cancellation (counted in <c>Cancelled</c>, notified to
/// nobody); an OCE while the caller's token is still live is an internal failure and stays an
/// error. This is the same reading the error-handling layer has always had — retry never retries
/// cancellation, the dead-letter channel never parks it, OnException never handles it.
/// </para>
/// </summary>
public sealed class CancellationStatisticsTests
{
    private static IEndpointStatistics Stats(RouteContext context, string uri)
        => (IEndpointStatistics)context.GetEndpoint(uri);

    private static async Task<IProducer> StartedProducer(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    // ── The consumer-side wrapper (StatisticsProcessor) ──

    [Fact]
    public async Task A_cooperative_cancellation_is_counted_as_cancelled_not_as_an_error()
    {
        using var cts = new CancellationTokenSource();
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:coop").Process((_, ct) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }));

        var producer = await StartedProducer(context, "direct:coop");
        var act = () => producer.Process(new Exchange(new Message("m")), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var stats = Stats(context, "direct:coop");
        stats.Errors.Should().Be(0, "the caller walked away; the route did nothing wrong");
        stats.Cancelled.Should().Be(1, "the abandonment is a signal of its own — a storm of these is exactly what the panel case would have shown");
        stats.HealthStatus.Should().Be(EndpointHealthStatus.Healthy,
            "one closed dashboard must not degrade endpoint health for five minutes");
    }

    [Fact]
    public async Task An_internal_cancellation_with_a_live_caller_stays_an_error()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:internal")
            .Process(_ => throw new OperationCanceledException()));

        var producer = await StartedProducer(context, "direct:internal");
        var act = () => producer.Process(new Exchange(new Message("m")), CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var stats = Stats(context, "direct:internal");
        stats.Errors.Should().Be(1, "a route-side timeout is the route's own failure to answer in time");
        stats.Cancelled.Should().Be(0);
    }

    [Fact]
    public async Task A_cancellation_parked_on_the_exchange_reads_the_same_as_a_thrown_one()
    {
        // The wrapper has a second counting branch: an exception set on the exchange without a
        // throw (a parallel branch aggregates its failure there). The same rule applies to it.
        using var cts = new CancellationTokenSource();
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:parked").Process((exchange, ct) =>
        {
            cts.Cancel();
            exchange.Exception = new OperationCanceledException(ct);
            return Task.CompletedTask;
        }));

        var producer = await StartedProducer(context, "direct:parked");
        await producer.Process(new Exchange(new Message("m")), cts.Token);

        var stats = Stats(context, "direct:parked");
        stats.Errors.Should().Be(0);
        stats.Cancelled.Should().Be(1);
    }

    // ── The producer-side funnel (CountedSend) ──

    [Fact]
    public async Task A_cancelled_send_does_not_error_the_target_endpoint()
    {
        // Mirrors StatisticsOwnershipTests.TemplateSend_Failure_CountsBothLegs: direct: is one
        // endpoint object playing both roles, so a cooperative cancellation lands on it twice —
        // the send leg (CountedSend) and the pipeline leg (StatisticsProcessor) — in Cancelled,
        // and exactly zero times in Errors.
        using var cts = new CancellationTokenSource();
        await using var context = new RouteContext();
        context.AddRoutes(r =>
        {
            r.From("direct:outer").To("direct:coop-target");
            r.From("direct:coop-target").Process((_, ct) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(ct);
            });
        });

        var producer = await StartedProducer(context, "direct:outer");
        var act = () => producer.Process(new Exchange(new Message("m")), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var target = Stats(context, "direct:coop-target");
        target.Errors.Should().Be(0, "neither leg failed; the caller cancelled both");
        target.Cancelled.Should().Be(2, "ровно две ноги, как у ошибок: отмена отправки + отмена пайплайна");

        var outer = Stats(context, "direct:outer");
        outer.Errors.Should().Be(0);
        outer.Cancelled.Should().Be(1);
    }

    // ── The lifecycle event layer (ExchangeEventsProcessor) ──

    private sealed class CountingListener : IRouteLifecycleListener
    {
        public int Received;
        public int Completed;
        public int Failed;

        public Task OnExchangeReceived(string routeId, IExchange exchange, CancellationToken ct)
        {
            Interlocked.Increment(ref Received);
            return Task.CompletedTask;
        }

        public Task OnExchangeCompleted(string routeId, IExchange exchange, CancellationToken ct)
        {
            Interlocked.Increment(ref Completed);
            return Task.CompletedTask;
        }

        public Task OnExchangeFailed(string routeId, IExchange exchange, Exception exception, CancellationToken ct)
        {
            Interlocked.Increment(ref Failed);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Listeners_do_not_hear_failed_for_a_cooperative_cancellation()
    {
        // An abandoned exchange is neither completed nor failed; the listener hears it arrive and
        // then hears nothing, which is the truthful account of what happened.
        var listener = new CountingListener();
        using var cts = new CancellationTokenSource();
        await using var context = new RouteContext();
        context.AddLifecycleListener(listener);
        context.AddRoutes(r => r.From("direct:coop-events").Process((_, ct) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }));

        var producer = await StartedProducer(context, "direct:coop-events");
        var act = () => producer.Process(new Exchange(new Message("m")), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        listener.Received.Should().Be(1);
        listener.Failed.Should().Be(0, "клиентская отмена — не отказ маршрута, NotifyBuilder не должен считать её failure");
        listener.Completed.Should().Be(0);
    }

    [Fact]
    public async Task Listeners_still_hear_failed_for_an_internal_cancellation()
    {
        var listener = new CountingListener();
        await using var context = new RouteContext();
        context.AddLifecycleListener(listener);
        context.AddRoutes(r => r.From("direct:internal-events")
            .Process(_ => throw new OperationCanceledException()));

        var producer = await StartedProducer(context, "direct:internal-events");
        var act = () => producer.Process(new Exchange(new Message("m")), CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();

        listener.Failed.Should().Be(1);
    }
}
