using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Logs a message for each exchange passing through.
/// Supports static messages and dynamic expressions via Func.
/// </summary>
public class LogProcessor : IProcessor
{
    private readonly ILogger _logger;
    private readonly Func<IExchange, string> _messageFunc;
    private readonly LogLevel _level;

    /// <summary>Creates a log processor with a static message.</summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="message">Static log message.</param>
    /// <param name="level">Log level (default: Information).</param>
    public LogProcessor(ILogger logger, string message, LogLevel level = LogLevel.Information)
        : this(logger, _ => message, level)
    {
    }

    /// <summary>Creates a log processor with a dynamic message function.</summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="messageFunc">Function to compute the log message from the exchange.</param>
    /// <param name="level">Log level (default: Information).</param>
    public LogProcessor(ILogger logger, Func<IExchange, string> messageFunc, LogLevel level = LogLevel.Information)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _messageFunc = messageFunc ?? throw new ArgumentNullException(nameof(messageFunc));
        _level = level;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
#pragma warning disable CA2254 // Template should be a static expression
        _logger.Log(_level, "{Message}", _messageFunc(exchange));
#pragma warning restore CA2254
        return Task.CompletedTask;
    }
}
