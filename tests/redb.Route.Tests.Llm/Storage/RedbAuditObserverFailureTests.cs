using Microsoft.Extensions.Logging;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Storage.Redb;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// A failed audit write never breaks the run, and it does not vanish either: the row that was not written
/// is an audit gap, and an operator has to be able to find it in the log.
/// </summary>
public sealed class RedbAuditObserverFailureTests
{
    /// <summary>Keeps every log entry, with its level, message and exception.</summary>
    private sealed class ListLoggerFactory : ILoggerFactory
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public ILogger CreateLogger(string categoryName) => new ListLogger(this);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class ListLogger(ListLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries) owner._entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    [Fact]
    public async Task SaveFailure_IsLogged_AndTheRunGoesOn()
    {
        var loggers = new ListLoggerFactory();
        var context = new RouteContext();
        // No IRedbService is registered, so the save fails the way a lost connection would.
        context.AddService(typeof(ILoggerFactory), loggers);
        var observer = new RedbAuditObserver(context);

        var invocation = new AgentToolInvocationContext
        {
            Run = new AgentRunContext
            {
                ConversationId = "c-1",
                FactoryName = "test",
                ProviderId = "test",
                ModelId = "test-model",
                ExchangeId = "x-1"
            },
            Tool = new LlmToolCapability { Name = "order_lookup", Description = "t", InputSchema = "{}" },
            InputJson = """{"orderId":"42"}""",
            OutputJson = """{"status":"shipped"}""",
            ToolUseId = "tu-1",
            Duration = TimeSpan.FromMilliseconds(5)
        };

        var act = () => observer.OnToolInvokedAsync(invocation);
        await act.Should().NotThrowAsync("audit problems never break a run");

        var entry = loggers.Entries.Should().ContainSingle(e => e.Level >= LogLevel.Warning,
            "the row that was not written is an audit gap an operator must be able to find").Subject;
        entry.Exception.Should().NotBeNull("the cause of the lost row travels with the entry");
        entry.Message.Should().Contain("order_lookup").And.Contain("tu-1");
    }
}
