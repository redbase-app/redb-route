using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Eip;

/// <summary>
/// OnCompletion blocks and the exchange events reach the consumer's verdict. A route whose <c>OnException</c> has no
/// <c>Handled</c> returns normally with the failure left on the exchange, and <c>.RollbackAll()</c> returns normally with
/// the exchange marked rollback-only: the consumer does not acknowledge either, so neither is a completion.
/// </summary>
public class CompletionVerdictTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private static async Task<IProducer> Start(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    private sealed class Outcomes : IRouteLifecycleListener
    {
        public readonly List<string> Seen = [];

        public Task OnExchangeCompleted(string routeId, IExchange exchange, CancellationToken ct)
        {
            lock (Seen) Seen.Add("completed");
            return Task.CompletedTask;
        }

        public Task OnExchangeFailed(string routeId, IExchange exchange, Exception exception, CancellationToken ct)
        {
            lock (Seen) Seen.Add($"failed: {exception.Message}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task OnCompletion_takes_a_failure_left_unhandled_on_the_exchange_for_a_failure()
    {
        var failures = new List<string?>();
        var completions = 0;
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://v-unhandled")
            .OnException<InvalidOperationException>()
                .Log("seen, not handled")
            .End()
            .OnCompletion().OnFailureOnly().ModeBeforeConsumer().Process(e => failures.Add(e.Exception?.Message)).EndOnCompletion()
            .OnCompletion().OnCompleteOnly().ModeBeforeConsumer().Process(_ => completions++).EndOnCompletion()
            .Process(_ => throw new InvalidOperationException("boom")));
        var producer = await Start(context, "direct://v-unhandled");

        await producer.Process(new Exchange(new Message("x")));

        failures.Should().Equal("boom");
        completions.Should().Be(0, "the consumer does not acknowledge this exchange");
    }

    [Fact]
    public async Task OnCompletion_takes_a_rollback_only_exchange_for_a_failure()
    {
        var completions = 0;
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://v-rollback")
            .OnCompletion().OnFailureOnly().To("mock://v-rollback-failed").EndOnCompletion()
            .OnCompletion().OnCompleteOnly().ModeBeforeConsumer().Process(_ => completions++).EndOnCompletion()
            .RollbackAll());
        var producer = await Start(context, "direct://v-rollback");

        await producer.Process(new Exchange(new Message("x")));

        await context.Mock("mock://v-rollback-failed").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        completions.Should().Be(0, "OnCompleteOnly must not run for a rolled-back exchange");
    }

    [Fact]
    public async Task The_events_report_a_failure_left_unhandled_on_the_exchange_as_failed()
    {
        var outcomes = new Outcomes();
        await using var context = new RouteContext();
        context.AddLifecycleListener(outcomes);
        context.AddRoutes(r => r
            .From("direct://v-events-unhandled")
            .OnException<InvalidOperationException>()
                .Log("seen, not handled")
            .End()
            .Process(_ => throw new InvalidOperationException("boom")));
        var producer = await Start(context, "direct://v-events-unhandled");

        await producer.Process(new Exchange(new Message("x")));

        outcomes.Seen.Should().Equal("failed: boom");
    }

    [Fact]
    public async Task The_events_report_a_rollback_only_exchange_as_failed()
    {
        var outcomes = new Outcomes();
        await using var context = new RouteContext();
        context.AddLifecycleListener(outcomes);
        context.AddRoutes(r => r
            .From("direct://v-events-rollback")
            .RollbackAll());
        var producer = await Start(context, "direct://v-events-rollback");

        await producer.Process(new Exchange(new Message("x")));

        outcomes.Seen.Should().ContainSingle().Which.Should().StartWith("failed: ").And.Contain("rollback-only");
    }

    [Fact]
    public async Task A_handled_failure_stays_a_completion()
    {
        var outcomes = new Outcomes();
        var completions = 0;
        await using var context = new RouteContext();
        context.AddLifecycleListener(outcomes);
        context.AddRoutes(r => r
            .From("direct://v-handled")
            .OnException<InvalidOperationException>()
                .Handled()
            .End()
            .OnCompletion().OnCompleteOnly().ModeBeforeConsumer().Process(_ => completions++).EndOnCompletion()
            .Process(_ => throw new InvalidOperationException("boom")));
        var producer = await Start(context, "direct://v-handled");

        await producer.Process(new Exchange(new Message("x")));

        completions.Should().Be(1);
        outcomes.Seen.Should().Equal("completed");
    }
}
