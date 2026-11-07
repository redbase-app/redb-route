using redb.Route.Core;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>An empty batch source writes nothing and reports zero, as Camel's empty <c>executeBatch()</c> does.</summary>
public sealed class BatchEmptySourceTests
{
    [Fact]
    public async Task Batch_EmptyList_ExecutesNothing()
    {
        var connection = new FakeBatchConnection();
        var factory = new FixedConnectionFactory(connection);
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, "INSERT INTO t (id, val) VALUES (:#id, :#val)", new()
        {
            ["outputType"] = "None",
            ["batchSize"] = "10",
        });
        var exchange = new Exchange(new Message(new List<Dictionary<string, object?>>()));

        await endpoint.CreateProducer().Process(exchange, CancellationToken.None);

        factory.Requests.Should().Be(0, "an empty batch needs no connection");
        connection.Executions.Should().Be(0, "an empty batch must not run the statement once with NULL parameters");
        Convert.ToInt32(exchange.In.Headers["redbSql.updateCount"]).Should().Be(0);
    }
}
