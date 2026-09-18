using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// A streaming poll inside the consumer transaction (<c>transacted=true</c>). As in Apache Camel, whose <c>JdbcTemplate</c>
/// uses the transaction's connection, <c>onSuccess</c> runs on the reader's connection and transaction. Whether that works
/// is the driver's fact: SQLite runs the statement; PostgreSQL and SQL Server refuse a second command while the reader is
/// open, and the transaction rolls back — never a second connection that the transaction would not cover.
/// </summary>
public abstract class PollStreamTransactedTestsBase(ITestOutputHelper output) : IAsyncLifetime
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
    public async Task PollStream_Transacted_OnSuccess_RunsOnReaderConnectionAndTransaction()
    {
        var factory = Db.CreateConnectionFactory();
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, $"SELECT id, val FROM {Db.Table} ORDER BY id", new()
        {
            ["mode"] = "Poll",
            ["outputType"] = "StreamList",
            ["transacted"] = "true",
            ["onSuccess"] = $"UPDATE {Db.Table} SET val = 'done' WHERE id = :#id",
            ["delay"] = "100",
            ["repeatCount"] = "1",
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        var thrown = await Outcome.Of(() => consumer.Poll(CancellationToken.None));

        var values = string.Join(",", (await Db.QueryAsync($"SELECT val FROM {Db.Table} ORDER BY id")).Select(r => r[0]));
        output.WriteLine($"[probe] {Provider.Name}: transacted poll StreamList + onSuccess: poll={Outcome.Describe(thrown)}, rows={values}, " +
                         $"readOnly requests={string.Join(",", factory.ReadOnlyRequests)}, open={factory.OpenConnections}");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        factory.ReadOnlyRequests.Should().ContainSingle("inside the transaction no second connection is opened");
        factory.OpenConnections.Should().Be(0);
        if (Provider.ReaderConnectionRunsAnotherCommand)
            values.Should().Be("done,done,done", "the driver runs onSuccess on the reader's connection, in its transaction");
        else
            values.Should().Be("new,new,new",
                "the driver refuses a second command while the reader is open; the failure is logged and the transaction rolls back");
    }
}
