using System.Collections.Concurrent;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Transactions;
using Xunit;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// <c>Retry</c> wraps the transaction, it does not live inside it. One attempt is one unit of work: a failed attempt
/// rolls its own database work and its own outgoing sends back, and the transaction never stays open across the
/// retry delays (a long-lived transaction on a broker connection is exactly what the deferred design tried to avoid).
/// </summary>
public class TransactedRetryOutsideTests
{
    private sealed class TrackingAction : ITransactedAction
    {
        public int Commits;
        public int Rollbacks;
        public Task Commit(CancellationToken ct = default) { Commits++; return Task.CompletedTask; }
        public Task Rollback(CancellationToken ct = default) { Rollbacks++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task A_failed_attempt_does_not_leave_its_send_for_the_successful_one()
    {
        var attempts = 0;
        var perAttempt = new List<TrackingAction>();
        await using var context = new RouteContext();

        context.AddRoutes(r => r
            .From("direct://tx-retry-in")
            .RouteId("tx-retry")
            .Transacted()
                .Retry(3, TimeSpan.FromMilliseconds(1))
                .Process((ex, _) =>
                {
                    attempts++;
                    var action = new TrackingAction();
                    perAttempt.Add(action);
                    var bag = (ConcurrentDictionary<string, ITransactedAction>)
                        ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
                    bag[$"send-{attempts}"] = action;

                    if (attempts < 3)
                        throw new InvalidOperationException($"attempt {attempts} fails");

                    return Task.CompletedTask;
                })
            .End());

        await context.Start();
        var producer = context.GetEndpoint("direct://tx-retry-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("m")));

        attempts.Should().Be(3);
        perAttempt.Should().HaveCount(3);
        perAttempt[0].Rollbacks.Should().Be(1, "a failed attempt rolls its own send back");
        perAttempt[0].Commits.Should().Be(0, "a failed attempt must never be sent by the attempt that succeeded");
        perAttempt[1].Rollbacks.Should().Be(1);
        perAttempt[1].Commits.Should().Be(0);
        perAttempt[2].Commits.Should().Be(1, "the attempt that succeeded sends its own message");
        perAttempt[2].Rollbacks.Should().Be(0);
    }

    [Fact]
    public async Task Every_attempt_runs_in_a_transaction_of_its_own()
    {
        var ambientPerAttempt = new List<string?>();
        await using var context = new RouteContext();

        context.AddRoutes(r => r
            .From("direct://tx-retry-scope-in")
            .RouteId("tx-retry-scope")
            .Transacted()
                .Retry(2, TimeSpan.FromMilliseconds(1))
                .Process((ex, _) =>
                {
                    ambientPerAttempt.Add(
                        System.Transactions.Transaction.Current?.TransactionInformation.LocalIdentifier);
                    if (ambientPerAttempt.Count == 1)
                        throw new InvalidOperationException("first attempt fails");
                    return Task.CompletedTask;
                })
            .End());

        await context.Start();
        var producer = context.GetEndpoint("direct://tx-retry-scope-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("m")));

        ambientPerAttempt.Should().HaveCount(2);
        ambientPerAttempt[0].Should().NotBeNull("a transacted block runs in a transaction");
        ambientPerAttempt[1].Should().NotBeNull();
        ambientPerAttempt[1].Should().NotBe(ambientPerAttempt[0],
            "the second attempt gets a transaction of its own, so a retry never holds one open across its delay");
    }
}
