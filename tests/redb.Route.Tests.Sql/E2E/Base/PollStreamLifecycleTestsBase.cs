using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// The poll consumer with <c>outputType=StreamList</c>, outside a transaction, keeps its reader open while it processes each
/// row. As in Apache Camel (<c>onConsume</c> through the data source), <c>onSuccess</c> / <c>onFailure</c> then run on a
/// connection of their own, so a driver that allows one active command per connection still marks every row. Not for SQLite:
/// in its default journal mode the open reader blocks a write on another connection — the documented cost of that rule.
/// </summary>
public abstract class PollStreamLifecycleTestsBase(ITestOutputHelper output) : IAsyncLifetime
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
    public async Task PollStream_OnSuccess_MarksEveryProcessedRow()
    {
        var factory = Db.CreateConnectionFactory();
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, $"SELECT id, val FROM {Db.Table} ORDER BY id", new()
        {
            ["mode"] = "Poll",
            ["outputType"] = "StreamList",
            ["onSuccess"] = $"UPDATE {Db.Table} SET val = 'done' WHERE id = :#id",
            ["delay"] = "100",
            ["repeatCount"] = "1",
        });
        var processed = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => processed++);
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        var thrown = await Outcome.Of(() => consumer.Poll(CancellationToken.None));

        var rows = await Db.QueryAsync($"SELECT val FROM {Db.Table} ORDER BY id");
        var values = string.Join(",", rows.Select(r => r[0]));
        output.WriteLine($"[probe] {Provider.Name}: poll StreamList + onSuccess: poll={Outcome.Describe(thrown)}, processed={processed}, rows={values}, open={factory.OpenConnections}");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        processed.Should().Be(3);
        values.Should().Be("done,done,done", "onSuccess must mark every processed row; a row left unmarked is polled and processed again");
        factory.OpenConnections.Should().Be(0);
    }

    [Fact]
    public async Task PollStream_OnFailure_RecordsEveryFailedRow_OnPrimaryConnection()
    {
        var factory = Db.CreateConnectionFactory();
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, $"SELECT id, val FROM {Db.Table} ORDER BY id", new()
        {
            ["mode"] = "Poll",
            ["outputType"] = "StreamList",
            ["onFailure"] = $"UPDATE {Db.Table} SET val = 'failed' WHERE id = :#id",
            ["delay"] = "100",
            ["repeatCount"] = "1",
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("route failed"));
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        var thrown = await Outcome.Of(() => consumer.Poll(CancellationToken.None));

        var values = string.Join(",", (await Db.QueryAsync($"SELECT val FROM {Db.Table} ORDER BY id")).Select(r => r[0]));
        output.WriteLine($"[probe] {Provider.Name}: poll StreamList + onFailure: poll={Outcome.Describe(thrown)}, rows={values}, " +
                         $"readOnly requests={string.Join(",", factory.ReadOnlyRequests)}, open={factory.OpenConnections}");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        values.Should().Be("failed,failed,failed", "onFailure records every row the route failed on");
        factory.ReadOnlyRequests.Should().Equal(new[] { false, false },
            "the poll reads from the primary database (no readOnly), and — as in Apache Camel (onConsume through the data " +
            "source) — the lifecycle statements run on a connection of their own to it");
        factory.OpenConnections.Should().Be(0);
    }
}
