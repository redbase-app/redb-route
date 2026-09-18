using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql;

/// <summary>
/// A failing <c>onSuccess</c> statement leaves the row unmarked, so the next poll processes it again. The failure must be
/// visible: it is logged, whatever <c>onFailure</c> and the transaction mode do with it afterwards.
/// </summary>
public sealed class SqlConsumerLifecycleLoggingTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlConsumerLifecycleLoggingTests()
    {
        _db.Execute("CREATE TABLE outbox_items (id INTEGER NOT NULL, message TEXT NOT NULL)");
        _db.Execute("INSERT INTO outbox_items (id, message) VALUES (1, 'm1')");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task OnSuccessFails_WithoutOnFailure_FailureIsLogged()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = new RouteContext(loggerFactory: logs);
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "SELECT id, message FROM outbox_items", new()
        {
            ["mode"] = "Poll",
            ["onSuccess"] = "UPDATE no_such_table SET done = 1 WHERE id = :#id",
            ["delay"] = "100",
            ["repeatCount"] = "1",
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        await consumer.Poll(CancellationToken.None);

        logs.Entries.Should().Contain(e => e.Level >= LogLevel.Error && e.Message.Contains("onSuccess") && e.Error != null,
            $"a failed onSuccess leaves the row to be processed again and must not pass unnoticed; logged: {string.Join(" | ", logs.Entries.Select(e => e.Message))}");
    }

    [Fact]
    public async Task Stop_WhileProcessing_IsNotAFailureOfTheRow()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = new RouteContext(loggerFactory: logs);
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "SELECT id, message FROM outbox_items", new()
        {
            ["mode"] = "Poll",
            ["transacted"] = "true",
            ["onSuccess"] = "UPDATE outbox_items SET message = 'done' WHERE id = :#id",
            ["onFailure"] = "UPDATE outbox_items SET message = 'failed' WHERE id = :#id",
            ["delay"] = "100",
            ["repeatCount"] = "1",
        });
        using var cts = new CancellationTokenSource();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        var thrown = await Outcome.Of(() => consumer.Poll(cts.Token));

        thrown.Should().BeAssignableTo<OperationCanceledException>("the stop goes up as a cancellation: " + Outcome.Describe(thrown));
        _db.Query("SELECT message FROM outbox_items")[0]["message"].Should().Be("m1", "neither onSuccess nor onFailure ran; the row is polled again after the restart");
        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Error,
            $"a stop is not an error of the poll or of the rollback; logged: {string.Join(" | ", logs.Entries.Select(e => e.Message))}");
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                    owner.Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
