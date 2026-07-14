using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

/// <summary>
/// A poll consumer with <c>outputClass</c> must hand the route a mapped POCO as the body,
/// while still copying the raw columns into headers — OnSuccess/OnFailure parameter binding
/// runs off those columns and must keep working even for columns the POCO has no property for.
/// </summary>
public class SqlConsumerOutputClassTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteTestHelper _db = new();
    private SqlConsumer? _consumer;
    private readonly List<IExchange> _captured = [];

    public Task InitializeAsync()
    {
        _db.Execute("""
            CREATE TABLE outbox (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                message   TEXT NOT NULL,
                processed INTEGER NOT NULL DEFAULT 0
            )
            """);
        _db.Execute("INSERT INTO outbox(message) VALUES('first'), ('second')");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_consumer is not null)
            try { await _consumer.Stop(); } catch (ObjectDisposedException) { }
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Deliberately has no property for the <c>processed</c> column.</summary>
    public class OutboxRow
    {
        public long Id { get; set; }
        public string? Message { get; set; }
    }

    private SqlEndpoint CreateConsumerEndpoint(string sql, Dictionary<string, string> extra)
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Poll",
            ["dataSource"] = "main",
            ["delay"] = "100"
        };
        foreach (var kv in extra) parameters[kv.Key] = kv.Value;

        var uri = new EndpointUri("sql", sql, $"sql:{sql}", parameters);
        return (SqlEndpoint)component.CreateEndpoint(uri);
    }

    private IProcessor CaptureProcessor()
    {
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lock (_captured) _captured.Add(ci.Arg<IExchange>());
                return Task.CompletedTask;
            });
        return processor;
    }

    // Generous: three TFM test processes run in parallel and contend for the CPU,
    // and a missed poll cycle here would surface as a bogus assertion failure.
    private async Task WaitForCaptured(int count, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_captured) if (_captured.Count >= count) return;
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task OutputClass_BodyIsPoco_HeadersStillCarryRawColumns()
    {
        var endpoint = CreateConsumerEndpoint(
            "SELECT id, message, processed FROM outbox WHERE processed = 0 ORDER BY id",
            new()
            {
                ["repeatCount"] = "1",
                ["outputClass"] = typeof(OutboxRow).AssemblyQualifiedName!
            });

        _consumer = (SqlConsumer)endpoint.CreateConsumer(CaptureProcessor());
        await _consumer.Start();
        await WaitForCaptured(2);
        await _consumer.Stop();

        List<IExchange> exchanges;
        lock (_captured) exchanges = [.. _captured];

        exchanges.Should().HaveCount(2);

        var first = exchanges[0].In.Body.Should().BeOfType<OutboxRow>().Subject;
        first.Id.Should().Be(1);
        first.Message.Should().Be("first");

        // The POCO has no 'processed' property, but the header must still be there —
        // this is what OnSuccess/OnFailure bind against.
        exchanges[0].In.Headers.Should().ContainKey("id");
        exchanges[0].In.Headers.Should().ContainKey("message");
        exchanges[0].In.Headers.Should().ContainKey("processed");
        exchanges[0].In.Headers["id"].Should().Be(1L);
    }

    [Fact]
    public async Task OutputClass_WithOnSuccess_LifecycleSqlStillBinds()
    {
        var endpoint = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox WHERE processed = 0 ORDER BY id",
            new()
            {
                ["repeatCount"] = "1",
                ["outputClass"] = typeof(OutboxRow).AssemblyQualifiedName!,
                ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
            });

        _consumer = (SqlConsumer)endpoint.CreateConsumer(CaptureProcessor());
        await _consumer.Start();
        await WaitForCaptured(2);
        await _consumer.Stop();

        var remaining = _db.ExecuteScalar("SELECT COUNT(*) FROM outbox WHERE processed = 0");
        Convert.ToInt64(remaining).Should().Be(0);
    }

    [Fact]
    public async Task OutputClass_NotSet_BodyStaysADictionary()
    {
        // Regression guard for the default path.
        var endpoint = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox ORDER BY id",
            new() { ["repeatCount"] = "1" });

        _consumer = (SqlConsumer)endpoint.CreateConsumer(CaptureProcessor());
        await _consumer.Start();
        await WaitForCaptured(2);
        await _consumer.Stop();

        List<IExchange> exchanges;
        lock (_captured) exchanges = [.. _captured];

        exchanges[0].In.Body.Should().BeOfType<Dictionary<string, object?>>();
    }
}
