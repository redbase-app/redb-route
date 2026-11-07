using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// How a batch goes over the wire. Breaking on the first error on a connection that can create a <c>DbBatch</c>: chunks of
/// <c>batchSize</c> statements, one round trip each. Otherwise one command whose parameters are reused for every item. There
/// is no option to choose: as in Apache Camel, the driver's capability decides.
/// </summary>
public sealed class BatchStrategyTests
{
    private const string Insert = "INSERT INTO t (id) VALUES (:#id)";

    [Fact]
    public async Task DbBatch_ChunksByBatchSize()
    {
        var sentIds = new List<object?>();
        var connection = new FakeBatchConnection
        {
            SupportsBatch = true,
            OnExecuteBatch = (_, batch) =>
            {
                sentIds.AddRange(batch.BatchCommands.Select(c => c.Parameters["id"].Value));
                return batch.BatchCommands.Count;
            },
        };

        var (exchange, thrown) = await RunAsync(connection, Items(10), batchSize: 3);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        connection.BatchSizes.Should().Equal(new[] { 3, 3, 3, 1 }, "batchSize is the number of statements in one round trip");
        connection.Executions.Should().Be(0, "no item runs as a command of its own");
        sentIds.Should().Equal(Enumerable.Range(0, 10).Cast<object?>(), "every item keeps its own values, in order");
        exchange.In.Headers["redbSql.batchStrategy"].Should().Be("DbBatch");
        exchange.In.Headers["redbSql.batchChunkCount"].Should().Be(4);
        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(10);
        connection.Transaction!.Committed.Should().BeTrue("all chunks share one transaction");
    }

    [Fact]
    public async Task DbBatch_FailedCommand_MapsToItemIndex()
    {
        var connection = new FakeBatchConnection
        {
            SupportsBatch = true,
            OnExecuteBatch = (index, batch) => index == 1
                ? throw new FakeDbException("duplicate key", batch.BatchCommands[1])
                : batch.BatchCommands.Count,
        };

        var (exchange, thrown) = await RunAsync(connection, Items(10), batchSize: 3);

        thrown.Should().BeOfType<FakeDbException>(Outcome.Describe(thrown));
        thrown!.Data[SqlHeaders.BatchFailedIndex].Should().Be(4, "the second command of the second chunk is item 4");
        exchange.In.Headers[SqlHeaders.BatchFailedIndex].Should().Be(4);
        connection.BatchSizes.Should().Equal(new[] { 3, 3 }, "nothing is sent after the failed chunk");
        connection.Transaction!.RolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task DbBatch_ProviderWithoutBatchCommand_ReportsChunkStartAndRange()
    {
        var connection = new FakeBatchConnection
        {
            SupportsBatch = true,
            OnExecuteBatch = (index, batch) => index == 1 ? throw new FakeDbException("failed") : batch.BatchCommands.Count,
        };

        var (_, thrown) = await RunAsync(connection, Items(10), batchSize: 3);

        thrown.Should().BeOfType<FakeDbException>(Outcome.Describe(thrown));
        thrown!.Data[SqlHeaders.BatchFailedIndex].Should().Be(3, "without the failed command only the chunk is known: its first item");
        thrown.Data["redbSql.batchFailedChunk"].Should().Be("3..5");
    }

    [Fact]
    public async Task DbBatch_UsesBatchCommandCreateParameter_WhenAvailable()
    {
        var connection = new FakeBatchConnection { SupportsBatch = true, BatchCommandsCanCreateParameter = true };

        var (_, thrown) = await RunAsync(connection, Items(4), batchSize: 10);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        connection.BatchParametersCreated.Should().Be(4);
        connection.CommandsCreated.Should().Be(0);
        connection.BatchSizes.Should().Equal(4);
    }

    [Fact]
    public async Task DbBatch_BatchCommandCannotCreateParameter_OneCommandMakesThem()
    {
        var connection = new FakeBatchConnection { SupportsBatch = true, BatchCommandsCanCreateParameter = false };

        var (_, thrown) = await RunAsync(connection, Items(4), batchSize: 10);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        connection.BatchParametersCreated.Should().Be(0);
        connection.CommandsCreated.Should().Be(1, "one command serves as the parameter factory for the whole batch");
        connection.Executions.Should().Be(0);
        connection.BatchSizes.Should().Equal(4);
    }

    [Fact]
    public async Task Commands_OneCommandAndParameterSetForWholeBatch()
    {
        var connection = new FakeBatchConnection();

        var (exchange, thrown) = await RunAsync(connection, Items(5), batchSize: 10);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        connection.CommandsCreated.Should().Be(1, "the statement is parsed once and its parameters reused for every item");
        connection.Executions.Should().Be(5);
        exchange.In.Headers["redbSql.batchStrategy"].Should().Be("Commands");
        exchange.In.Headers.Should().NotContainKey("redbSql.batchChunkCount");
    }

    [Fact]
    public async Task Savepoints_OneCommandForWholeBatch()
    {
        var connection = new FakeBatchConnection { Savepoints = SavepointBehavior.Works };

        var (exchange, thrown) = await RunAsync(connection, Items(3), batchSize: 10, breakOnError: false);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        connection.CommandsCreated.Should().Be(1);
        connection.Executions.Should().Be(3);
        exchange.In.Headers["redbSql.batchStrategy"].Should().Be("Savepoints");
    }

    [Fact]
    public async Task Strategy_Continue_AlwaysSavepoints_EvenWhenBatchCapable()
    {
        var connection = new FakeBatchConnection { SupportsBatch = true, Savepoints = SavepointBehavior.Works };

        var (exchange, thrown) = await RunAsync(connection, Items(3), batchSize: 10, breakOnError: false);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        connection.BatchSizes.Should().BeEmpty("a DbBatch cannot go on past a failed command");
        connection.Executions.Should().Be(3);
        exchange.In.Headers["redbSql.batchStrategy"].Should().Be("Savepoints");
    }

    private static List<Dictionary<string, object?>> Items(int count) =>
        Enumerable.Range(0, count).Select(i => new Dictionary<string, object?> { ["id"] = i }).ToList();

    private static async Task<(Exchange Exchange, Exception? Thrown)> RunAsync(
        FakeBatchConnection connection, List<Dictionary<string, object?>> items, int batchSize, bool breakOnError = true)
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection), Insert, new()
        {
            ["outputType"] = "None",
            ["batchSize"] = batchSize.ToString(),
            ["breakBatchOnError"] = breakOnError ? "true" : "false",
        });
        var exchange = new Exchange(new Message(items));
        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));
        return (exchange, thrown);
    }
}
