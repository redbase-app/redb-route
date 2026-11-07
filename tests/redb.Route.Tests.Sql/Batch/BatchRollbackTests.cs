using redb.Route.Core;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>What surrounds a failed batch: the rollback, and the resources the batch held.</summary>
public sealed class BatchRollbackTests
{
    [Fact]
    public async Task Break_RollbackFails_OriginalExceptionIsThrown()
    {
        var connection = new FakeBatchConnection
        {
            FailRollback = true,
            OnExecute = (index, _) => index == 1 ? throw new FakeDbException("fake: item 1 violates a constraint") : 1,
        };
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection),
            "INSERT INTO t (id) VALUES (:#id)", new()
            {
                ["outputType"] = "None",
                ["batchSize"] = "10",
                ["breakBatchOnError"] = "true",
            });
        var exchange = new Exchange(new Message(new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1 },
            new() { ["id"] = 2 },
        }));

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeOfType<FakeDbException>("a failing rollback is logged; the error that ended the batch is what the route sees");
    }

    [Fact]
    public async Task Cancellation_DuringItem_PropagatesAndDisposesTransaction()
    {
        using var cts = new CancellationTokenSource();
        var connection = new FakeBatchConnection
        {
            OnExecute = (index, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (index == 1)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
                return 1;
            },
        };
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection),
            "INSERT INTO t (id) VALUES (:#id)", new()
            {
                ["outputType"] = "None",
                ["batchSize"] = "10",
            });
        var exchange = new Exchange(new Message(new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1 },
            new() { ["id"] = 2 },
            new() { ["id"] = 3 },
        }));

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, cts.Token));

        thrown.Should().BeAssignableTo<OperationCanceledException>();
        connection.Executions.Should().Be(2, "nothing is sent after the cancelled item");
        connection.Transaction!.Committed.Should().BeFalse();
        connection.Transaction.IsDisposed.Should().BeTrue("a cancelled batch still releases its transaction");
        connection.IsDisposed.Should().BeTrue("and returns its connection");
    }
}
