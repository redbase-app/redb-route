using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Components;

/// <summary>
/// Log component. Scheme: "log".
/// Logs the exchange body and headers to <see cref="ILogger"/>.
/// URI: log://category?level=Information&amp;showHeaders=true&amp;showBody=true
/// </summary>
public class LogComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, LogEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Creates a log component with an optional logger factory.</summary>
    /// <param name="loggerFactory">Logger factory for creating category-specific loggers.</param>
    public LogComponent(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public override string Scheme => "log";

    /// <summary>Gets the logger factory (may be null).</summary>
    internal ILoggerFactory? LoggerFactory => _loggerFactory;

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new LogEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        return _endpoints.GetOrAdd(uri.NormalizedKey, _ => new LogEndpoint(uri, this, options));
    }
}

/// <summary>
/// Options for log endpoints.
/// </summary>
public class LogEndpointOptions : EndpointOptions
{
    /// <summary>Log level name (default: "Information"). Supports: Trace, Debug, Information, Warning, Error, Critical.</summary>
    public string Level { get; set; } = "Information";

    /// <summary>Whether to include headers in the log output (default: true).</summary>
    public bool ShowHeaders { get; set; } = true;

    /// <summary>Whether to include body in the log output (default: true).</summary>
    public bool ShowBody { get; set; } = true;

    /// <summary>Gets the parsed <see cref="LogLevel"/>.</summary>
    internal LogLevel ParsedLevel =>
        Enum.TryParse<LogLevel>(Level, ignoreCase: true, out var result)
            ? result
            : LogLevel.Information;

    /// <inheritdoc />
    public override void Validate() { }
}

/// <summary>
/// Log endpoint. Produces a log entry for each exchange sent through it.
/// </summary>
public class LogEndpoint : EndpointBase<LogEndpointOptions>
{
    private readonly ILogger? _logger;
    private readonly LogEndpointOptions _logOptions;

    /// <summary>Creates a log endpoint.</summary>
    public LogEndpoint(EndpointUri uri, LogComponent component, LogEndpointOptions options)
        : base(uri, component, options)
    {
        _logOptions = options;
        var category = uri.Path;
        _logger = component.LoggerFactory?.CreateLogger(
            string.IsNullOrEmpty(category) ? "redb.Route.Log" : $"redb.Route.Log.{category}");
    }

    /// <summary>Gets the configured logger (may be null).</summary>
    internal ILogger? Logger => _logger;

    /// <summary>Gets the log options.</summary>
    internal LogEndpointOptions LogOptions => _logOptions;

    /// <inheritdoc />
    public override IProducer CreateProducer() => new LogProducer(this);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        throw new NotSupportedException("Log endpoints do not support consumers. Use them only as To() destinations.");
    }
}

/// <summary>
/// Log producer. Logs the exchange body and/or headers.
/// </summary>
public class LogProducer : IProducer
{
    private readonly LogEndpoint _endpoint;
    private ILogger? _logger;

    /// <summary>Creates a log producer.</summary>
    public LogProducer(LogEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var logger = _endpoint.Logger;
        if (logger == null || !logger.IsEnabled(_endpoint.LogOptions.ParsedLevel))
            return Task.CompletedTask;

        var options = _endpoint.LogOptions;
        var parts = new List<string>(3);

        if (options.ShowBody)
        {
            var body = exchange.In.Body;
            parts.Add($"Body: {body ?? "(null)"}");
        }

        if (options.ShowHeaders && exchange.In.Headers.Count > 0)
        {
            var headers = string.Join(", ",
                exchange.In.Headers.Select(h => $"{h.Key}={h.Value}"));
            parts.Add($"Headers: [{headers}]");
        }

        var message = parts.Count > 0
            ? string.Join(" | ", parts)
            : "(empty exchange)";

#pragma warning disable CA2254 // Template should be a static expression
        logger.Log(_endpoint.LogOptions.ParsedLevel, "Exchange [{RouteId}]: {Message}",
            exchange.RouteId ?? "(no-route)", message);
#pragma warning restore CA2254

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        _logger?.LogInformation("Log producer started: {Name}", _endpoint.Uri.NormalizedKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default)
    {
        _logger?.LogInformation("Log producer stopped: {Name}", _endpoint.Uri.NormalizedKey);
        return Task.CompletedTask;
    }
}
