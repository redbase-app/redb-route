using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql;

/// <summary>
/// A poll that delivers one list (<c>pollDelivery=List</c>, Apache Camel <c>useIterator=false</c>): one exchange carries every
/// polled row; <c>onSuccess</c> / <c>onFailure</c> and <c>onBatchComplete</c> run once for it, with values from headers and
/// <c>param.*</c> — a list has no row columns to bind.
/// </summary>
public sealed class SqlConsumerListDeliveryTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlConsumerListDeliveryTests()
    {
        _db.Execute("CREATE TABLE outbox (id INTEGER PRIMARY KEY, message TEXT NOT NULL, processed INTEGER NOT NULL DEFAULT 0)");
        _db.Execute("CREATE TABLE poll_log (note TEXT)");
        _db.Execute("INSERT INTO outbox (id, message) VALUES (1, 'm1'), (2, 'm2'), (3, 'm3')");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ListDelivery_OneExchangeWithAllRows()
    {
        var received = new List<IExchange>();

        await PollAsync(new(), received);

        received.Should().ContainSingle("the whole poll is one exchange");
        var rows = received[0].In.Body.Should().BeAssignableTo<IList<Dictionary<string, object?>>>().Subject;
        rows.Select(r => r["message"]).Should().Equal("m1", "m2", "m3");
        received[0].In.Headers[SqlHeaders.RowCount].Should().Be(3);
    }

    [Fact]
    public async Task ListDelivery_OutputClass_TypedList()
    {
        var received = new List<IExchange>();

        await PollAsync(new() { ["outputClass"] = typeof(OutboxRow).AssemblyQualifiedName! }, received);

        received.Should().ContainSingle();
        received[0].In.Body.Should().BeAssignableTo<IList<OutboxRow>>()
            .Which.Select(r => r.Message).Should().Equal("m1", "m2", "m3");
    }

    [Fact]
    public async Task ListDelivery_MaxMessagesPerPoll_LimitsRows()
    {
        var received = new List<IExchange>();

        await PollAsync(new() { ["maxMessagesPerPoll"] = "2" }, received);

        received.Should().ContainSingle();
        received[0].In.Body.Should().BeAssignableTo<IList<Dictionary<string, object?>>>().Which.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListDelivery_OnSuccessAndOnBatchComplete_RunOnce()
    {
        await PollAsync(new()
        {
            ["onSuccess"] = "INSERT INTO poll_log (note) VALUES ('success')",
            ["onBatchComplete"] = "INSERT INTO poll_log (note) VALUES ('complete')",
        }, []);

        Notes().Should().Equal(new object?[] { "success", "complete" }, "the list is one exchange: one onSuccess, one onBatchComplete");
    }

    [Fact]
    public async Task ListDelivery_ProcessorFails_OnFailureOnce_TransactedRollsBack()
    {
        await PollAsync(new()
        {
            ["transacted"] = "true",
            ["onSuccess"] = "UPDATE outbox SET processed = 1",
            ["onFailure"] = "INSERT INTO poll_log (note) VALUES (:#redbError)",
        }, [], fail: true);

        Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM outbox WHERE processed = 1")).Should().Be(0);
        Notes().Should().BeEmpty("onFailure ran in the consumer transaction, which the failure rolled back");
    }

    [Fact]
    public async Task ListDelivery_ProcessorFails_OnFailureOnce_NotTransacted()
    {
        await PollAsync(new() { ["onFailure"] = "INSERT INTO poll_log (note) VALUES (:#redbError)" }, [], fail: true);

        Notes().Should().ContainSingle("the list failed once").Which.Should().Be("route failed");
    }

    [Fact]
    public async Task ListDelivery_EmptyResult_RouteEmptyResultSet_DeliversEmptyList()
    {
        _db.Execute("DELETE FROM outbox");
        var received = new List<IExchange>();

        await PollAsync(new() { ["routeEmptyResultSet"] = "true" }, received);

        received.Should().ContainSingle();
        received[0].In.Body.Should().BeAssignableTo<IList<Dictionary<string, object?>>>().Which.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDelivery_StreamList_BodyIsAStreamTheRouteReads()
    {
        var read = new List<object?>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var stream = (IAsyncEnumerable<Dictionary<string, object?>>)ci.Arg<IExchange>().In.Body!;
                await foreach (var row in stream)
                    read.Add(row["message"]);
            });

        await PollAsync(new()
        {
            ["outputType"] = "StreamList",
            ["onSuccess"] = "INSERT INTO poll_log (note) VALUES ('success')",
        }, processor);

        read.Should().Equal("m1", "m2", "m3");
        Notes().Should().Equal(new object?[] { "success" });
    }

    public sealed class OutboxRow
    {
        public long Id { get; set; }
        public string Message { get; set; } = "";
    }

    private Task PollAsync(Dictionary<string, string> extra, List<IExchange> received, bool fail = false)
    {
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var exchange = ci.Arg<IExchange>();
                // The consumer disposes the exchange after processing: keep what the route saw.
                var copy = new Exchange(new Message(exchange.In.Body is System.Collections.IList list ? CopyList(list) : exchange.In.Body));
                foreach (var (key, value) in exchange.In.Headers)
                    copy.In.Headers[key] = value;
                received.Add(copy);
                return fail ? Task.FromException(new InvalidOperationException("route failed")) : Task.CompletedTask;
            });
        return PollAsync(extra, processor);
    }

    private async Task PollAsync(Dictionary<string, string> extra, IProcessor processor)
    {
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Poll",
            ["pollDelivery"] = "List",
            ["delay"] = "100",
            ["repeatCount"] = "1",
        };
        foreach (var (key, value) in extra)
            parameters[key] = value;

        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "SELECT id, message FROM outbox ORDER BY id", parameters);
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);
    }

    private static object CopyList(System.Collections.IList list)
    {
        var copy = (System.Collections.IList)Activator.CreateInstance(list.GetType())!;
        foreach (var item in list)
            copy.Add(item);
        return copy;
    }

    private List<object?> Notes() => _db.Query("SELECT note FROM poll_log ORDER BY rowid").Select(r => r["note"]).ToList();
}
