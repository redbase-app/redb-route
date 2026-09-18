using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>Cancellation during a batch ends the batch; it is never an item error to collect and move past.</summary>
public sealed class BatchCancellationTests
{
    [Fact]
    public async Task Batch_Cancellation_IsNotRecordedAsItemError()
    {
        using var cts = new CancellationTokenSource();
        // A provider with savepoints: continue mode needs them, and the subject here is cancellation, not their absence.
        var connection = new FakeBatchConnection
        {
            Savepoints = SavepointBehavior.Works,
            OnExecute = (index, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (index == 1)
                {
                    // The host shuts down while the second item is on the wire.
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
                ["breakBatchOnError"] = "false",
            });
        var producer = endpoint.CreateProducer();
        var items = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1 },
            new() { ["id"] = 2 },
            new() { ["id"] = 3 },
        };
        var exchange = new Exchange(new Message(items));

        var act = () => producer.Process(exchange, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        connection.Executions.Should().Be(2, "no item may be attempted after the batch was cancelled");
        exchange.In.Headers.Should().NotContainKey(SqlHeaders.Error, "a cancellation is not an item error");
    }
}
