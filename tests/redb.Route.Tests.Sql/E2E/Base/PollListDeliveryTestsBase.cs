using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// A streaming poll delivered as one list (<c>outputType=StreamList</c>, <c>pollDelivery=List</c>, Apache Camel
/// <c>useIterator=false</c> with a result set iterator): the route reads the rows from the open reader; <c>onSuccess</c> runs
/// once, after the reader is closed, so it shares the consumer transaction on every driver.
/// </summary>
public abstract class PollListDeliveryTestsBase(ITestOutputHelper output) : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _db = await SqlE2EDatabase.CreateAsync(Provider);
        for (var id = 1; id <= 3; id++)
            await Db.ExecuteAsync(Provider.InsertLiteral(Db.Table, id, "new"));
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_db is not null)
                await _db.DisposeAsync();
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task StreamListAsList_Transacted_OnSuccessOnceAfterReaderClosed()
    {
        var factory = Db.CreateConnectionFactory();
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, $"SELECT id, val FROM {Db.Table} ORDER BY id", new()
        {
            ["mode"] = "Poll",
            ["outputType"] = "StreamList",
            ["pollDelivery"] = "List",
            ["transacted"] = "true",
            ["onSuccess"] = $"UPDATE {Db.Table} SET val = 'done'",
            ["delay"] = "100",
            ["repeatCount"] = "1",
        });
        var exchanges = 0;
        var rowsRead = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                exchanges++;
                if (ci.Arg<IExchange>().In.Body is IAsyncEnumerable<Dictionary<string, object?>> stream)
                    await foreach (var _ in stream)
                        rowsRead++;
            });
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        var thrown = await Outcome.Of(() => consumer.Poll(CancellationToken.None));

        var values = string.Join(",", (await Db.QueryAsync($"SELECT val FROM {Db.Table} ORDER BY id")).Select(r => r[0]));
        output.WriteLine($"[probe] {Provider.Name}: StreamList as one list, transacted: poll={Outcome.Describe(thrown)}, " +
                         $"exchanges={exchanges}, rows read={rowsRead}, rows={values}, open={factory.OpenConnections}");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchanges.Should().Be(1, "the whole poll is one exchange");
        rowsRead.Should().Be(3, "the route reads the rows from the stream");
        values.Should().Be("done,done,done", "onSuccess ran once, after the reader was closed, in the consumer transaction");
        factory.OpenConnections.Should().Be(0);
    }
}
