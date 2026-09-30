using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace redb.Route.Tests.Kafka;

/// <summary>Test-only logger provider that keeps every entry in memory, for assertions on what the connector says.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    internal sealed record LogEntry(LogLevel Level, string Message);

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }
}
