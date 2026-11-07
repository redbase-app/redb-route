using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Processors;

/// <summary>
/// Rich log processor that outputs multiple messages, selected headers and properties
/// in a structured format. Built by the Log(level) scope DSL.
/// </summary>
public class RichLogProcessor : IProcessor
{
    private readonly ILogger _logger;
    private readonly LogLevel _level;
    private readonly IReadOnlyList<string> _messages;
    private readonly IReadOnlyList<Func<IExchange, string>> _messageFuncs;
    private readonly IReadOnlyList<string> _headerNames;
    private readonly IReadOnlyList<string> _propertyNames;
    private readonly bool _showRouteId;

    public RichLogProcessor(
        ILogger logger,
        LogLevel level,
        IReadOnlyList<string> messages,
        IReadOnlyList<Func<IExchange, string>> messageFuncs,
        IReadOnlyList<string> headerNames,
        IReadOnlyList<string> propertyNames,
        bool showRouteId)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _level = level;
        _messages = messages;
        _messageFuncs = messageFuncs;
        _headerNames = headerNames;
        _propertyNames = propertyNames;
        _showRouteId = showRouteId;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (!_logger.IsEnabled(_level))
            return Task.CompletedTask;

        var sb = new StringBuilder();

        // Route ID prefix
        if (_showRouteId)
        {
            var routeId = exchange.RouteId;
            if (!string.IsNullOrEmpty(routeId))
                sb.Append("[rId:").Append(routeId).Append("] ");
        }

        // Properties
        foreach (var name in _propertyNames)
        {
            if (exchange.Properties.TryGetValue(name, out var val))
                sb.Append("[p:").Append(name).Append('=').Append(val).Append("] ");
        }

        // Headers
        foreach (var name in _headerNames)
        {
            if (exchange.In.Headers.TryGetValue(name, out var val))
                sb.Append("[h:").Append(name).Append('=').Append(val).Append("] ");
        }

        // Static/template messages — each on its own line (like original LogProcessor)
        foreach (var msg in _messages)
        {
            if (msg.Contains("${", StringComparison.Ordinal))
                sb.AppendLine(ExpressionResolver.ProcessTemplate(msg, exchange));
            else
                sb.AppendLine(msg);
        }

        // Dynamic messages
        foreach (var func in _messageFuncs)
        {
            sb.AppendLine(func(exchange));
        }

        // Remove trailing newline
        var logStr = sb.ToString();
        if (logStr.EndsWith(Environment.NewLine))
            logStr = logStr[..^Environment.NewLine.Length];

#pragma warning disable CA2254
        _logger.Log(_level, "{Message}", logStr);
#pragma warning restore CA2254
        return Task.CompletedTask;
    }
}
