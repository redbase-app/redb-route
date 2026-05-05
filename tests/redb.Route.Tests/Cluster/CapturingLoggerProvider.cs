using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace redb.Route.Tests.Cluster;

/// <summary>
/// Test-only logger provider that captures every log entry into an in-memory list,
/// for assertions on warning messages emitted during route compilation.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class Logger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;

        public Logger(CapturingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _owner.Entries.Add(new LogEntry(_category, logLevel, formatter(state, exception), exception));
        }
    }
}
