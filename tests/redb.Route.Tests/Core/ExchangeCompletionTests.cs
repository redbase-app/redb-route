using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Completions registered with <see cref="ExchangeResources.OnCompletion"/> — Apache Camel's <c>addOnCompletion</c> with a
/// <c>Synchronization</c> — run once when the exchange's unit of work ends: <c>OnComplete</c> on success, <c>OnFailure</c>
/// otherwise, reaching the consumer's verdict. The outermost route owns the unit of work and ends it before returning;
/// an exchange no route owns ends it when it is released. Copies do not inherit completions.
/// </summary>
public sealed class ExchangeCompletionTests
{
    private sealed class Recording : IExchangeCompletion
    {
        private readonly List<string> _log;
        private readonly string _name;

        public Recording(List<string> log, string name = "c")
        {
            _log = log;
            _name = name;
        }

        public Task OnComplete(IExchange exchange, CancellationToken ct)
        {
            lock (_log) _log.Add($"{_name}:complete");
            return Task.CompletedTask;
        }

        public Task OnFailure(IExchange exchange, CancellationToken ct)
        {
            lock (_log) _log.Add($"{_name}:failure");
            return Task.CompletedTask;
        }
    }

    private sealed class Throwing : IExchangeCompletion
    {
        public Task OnComplete(IExchange exchange, CancellationToken ct) => throw new InvalidOperationException("completion fails");
        public Task OnFailure(IExchange exchange, CancellationToken ct) => throw new InvalidOperationException("completion fails");
    }

    private static async Task<IProducer> Start(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task A_route_ends_the_unit_of_work_before_it_returns_success()
    {
        var log = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://c-success")
            .Process(ex => ExchangeResources.OnCompletion(ex, new Recording(log)))
            .Process(_ => { lock (log) log.Add("route"); }));
        var producer = await Start(context, "direct://c-success");

        await producer.Process(new Exchange(new Message("x")));

        log.Should().Equal("route", "c:complete");
    }

    [Fact]
    public async Task An_escaping_exception_is_a_failure()
    {
        var log = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://c-thrown")
            .Process(ex => ExchangeResources.OnCompletion(ex, new Recording(log)))
            .Process(_ => throw new InvalidOperationException("boom")));
        var producer = await Start(context, "direct://c-thrown");

        var send = () => producer.Process(new Exchange(new Message("x")));
        await send.Should().ThrowAsync<InvalidOperationException>();

        log.Should().Equal("c:failure");
    }

    [Fact]
    public async Task A_failure_left_unhandled_on_the_exchange_is_a_failure()
    {
        var log = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://c-unhandled")
            .OnException<InvalidOperationException>()
                .Log("seen, not handled")
            .End()
            .Process(ex => ExchangeResources.OnCompletion(ex, new Recording(log)))
            .Process(_ => throw new InvalidOperationException("boom")));
        var producer = await Start(context, "direct://c-unhandled");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.EndedInFailure().Should().BeTrue("OnException without Handled leaves the failure on the exchange");
        log.Should().Equal("c:failure");
    }

    [Fact]
    public async Task A_handled_failure_is_a_success()
    {
        var log = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://c-handled")
            .OnException<InvalidOperationException>()
                .Handled()
            .End()
            .Process(ex => ExchangeResources.OnCompletion(ex, new Recording(log)))
            .Process(_ => throw new InvalidOperationException("boom")));
        var producer = await Start(context, "direct://c-handled");

        await producer.Process(new Exchange(new Message("x")));

        log.Should().Equal("c:complete");
    }

    [Fact]
    public async Task A_rollback_only_exchange_is_a_failure()
    {
        var log = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://c-rollback")
            .Process(ex => ExchangeResources.OnCompletion(ex, new Recording(log)))
            .RollbackAll());
        var producer = await Start(context, "direct://c-rollback");

        await producer.Process(new Exchange(new Message("x")));

        log.Should().Equal("c:failure");
    }

    [Fact]
    public async Task A_release_in_the_middle_of_the_route_does_not_end_its_unit_of_work()
    {
        var log = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://c-midrelease")
            .Process(ex => ExchangeResources.OnCompletion(ex, new Recording(log)))
            .Process(async (ex, _) => await ex.ReleaseScopes())
            .Process(_ => throw new InvalidOperationException("after the release")));
        var producer = await Start(context, "direct://c-midrelease");

        var send = () => producer.Process(new Exchange(new Message("x")));
        await send.Should().ThrowAsync<InvalidOperationException>();

        log.Should().Equal("c:failure");
    }

    [Fact]
    public async Task Completions_run_in_registration_order_and_a_failing_one_does_not_stop_the_others()
    {
        var log = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://c-order")
            .Process(ex =>
            {
                ExchangeResources.OnCompletion(ex, new Recording(log, "first"));
                ExchangeResources.OnCompletion(ex, new Throwing());
                ExchangeResources.OnCompletion(ex, new Recording(log, "second"));
            }));
        var producer = await Start(context, "direct://c-order");

        await producer.Process(new Exchange(new Message("x")));

        log.Should().Equal("first:complete", "second:complete");
    }

    [Fact]
    public async Task An_exchange_no_route_owns_ends_its_unit_of_work_when_released()
    {
        var log = new List<string>();
        var exchange = new Exchange(new Message("x"));
        ExchangeResources.OnCompletion(exchange, new Recording(log));

        log.Should().BeEmpty();
        exchange.Exception = new InvalidOperationException("failed");
        await exchange.DisposeAsync();

        log.Should().Equal("c:failure");
    }

    [Fact]
    public async Task Copies_do_not_inherit_completions()
    {
        var log = new List<string>();
        var exchange = new Exchange(new Message("x"));
        ExchangeResources.OnCompletion(exchange, new Recording(log));

        await using (var copy = (Exchange)exchange.Clone())
            copy.Properties.Keys.Should().NotContain(k => k.StartsWith("__redb_uow", StringComparison.Ordinal));
        log.Should().BeEmpty("the copy has no unit of work of its own and ended nothing");

        await exchange.DisposeAsync();
        log.Should().Equal("c:complete");
    }

    [Fact]
    public async Task The_unit_of_work_ends_once()
    {
        var log = new List<string>();
        var exchange = new Exchange(new Message("x"));
        ExchangeResources.OnCompletion(exchange, new Recording(log));

        await exchange.ReleaseScopes();
        await exchange.DisposeAsync();

        log.Should().Equal("c:complete");
    }
}
