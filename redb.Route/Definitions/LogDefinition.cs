using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that logs a static message.
/// </summary>
public sealed class LogStaticDefinition : ProcessorDefinition
{
    private readonly string _message;
    private readonly LogLevel _level;

    /// <summary>The static log message (may contain <c>${...}</c> placeholders).</summary>
    public string Message => _message;

    /// <summary>The log level.</summary>
    public LogLevel Level => _level;

    /// <summary>Creates a static log definition.</summary>
    public LogStaticDefinition(string message, LogLevel level = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _message = message;
        _level = level;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("redb.Route");

        // If the message contains ${...} placeholders, route through the template
        // processor so .Log("...${header.x}...") in DSL just works without the
        // caller having to pick LogTemplateDefinition explicitly.
        if (_message.Contains("${", StringComparison.Ordinal))
            return new TemplateLogProcessor(_message, _level, logger);

        if (logger != null)
            return new LogProcessor(logger, _message, _level);
        return new DelegateProcessor(_ => { });
    }
}

/// <summary>
/// Leaf definition that logs a message produced by a factory function.
/// </summary>
public sealed class LogDynamicDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, string> _messageFactory;
    private readonly LogLevel _level;

    /// <summary>Creates a dynamic log definition.</summary>
    public LogDynamicDefinition(Func<IExchange, string> messageFactory, LogLevel level = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(messageFactory);
        _messageFactory = messageFactory;
        _level = level;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var loggerFactory = context.GetService<ILoggerFactory>();
        if (loggerFactory != null)
        {
            var logger = loggerFactory.CreateLogger("redb.Route");
            return new LogProcessor(logger, _messageFactory, _level);
        }
        return new DelegateProcessor(_ => { });
    }
}

/// <summary>
/// Leaf definition that logs a string expression template, resolving <c>${...}</c> placeholders at runtime.
/// </summary>
public sealed class LogTemplateDefinition : ProcessorDefinition
{
    private readonly string _template;
    private readonly LogLevel _level;

    /// <summary>The log message template (with <c>${...}</c> placeholders).</summary>
    public string Template => _template;

    /// <summary>The log level.</summary>
    public LogLevel Level => _level;

    /// <summary>Creates a template log definition.</summary>
    public LogTemplateDefinition(string template, LogLevel level = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        _template = template;
        _level = level;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger("redb.Route");
        return new TemplateLogProcessor(_template, _level, logger);
    }
}

/// <summary>
/// Leaf definition that logs multiple messages with structured header/property output.
/// </summary>
public sealed class RichLogDefinition : ProcessorDefinition
{
    private readonly IReadOnlyList<string> _messages;
    private readonly IReadOnlyList<Func<IExchange, string>> _messageFuncs;
    private readonly IReadOnlyList<string> _headerNames;
    private readonly IReadOnlyList<string> _propertyNames;
    private readonly LogLevel _level;
    private readonly bool _showRouteId;

    /// <summary>Creates a rich log definition.</summary>
    public RichLogDefinition(
        IReadOnlyList<string> messages,
        IReadOnlyList<Func<IExchange, string>> messageFuncs,
        IReadOnlyList<string> headerNames,
        IReadOnlyList<string> propertyNames,
        LogLevel level = LogLevel.Information,
        bool showRouteId = false)
    {
        _messages = messages;
        _messageFuncs = messageFuncs;
        _headerNames = headerNames;
        _propertyNames = propertyNames;
        _level = level;
        _showRouteId = showRouteId;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var loggerFactory = context.GetService<ILoggerFactory>();
        if (loggerFactory != null)
        {
            var logger = loggerFactory.CreateLogger("redb.Route");
            return new RichLogProcessor(logger, _level, _messages, _messageFuncs,
                _headerNames, _propertyNames, _showRouteId);
        }
        return new DelegateProcessor(_ => { });
    }
}

/// <summary>
/// Scope-opener definition for rich log output. Collects messages, headers, properties,
/// and log level. Build content with <see cref="Message(string)"/>,
/// <see cref="Header"/>, <see cref="Property"/>, <see cref="ShowRouteId"/>,
/// then close with <see cref="EndLog"/> or <see cref="IRouteScope.End"/>.
/// </summary>
public sealed class RichLogScopeDefinition : ProcessorDefinition, IRouteScope
{
    private readonly List<string> _messages = [];
    private readonly List<Func<IExchange, string>> _messageFuncs = [];
    private readonly List<string> _headerNames = [];
    private readonly List<string> _propertyNames = [];
    private bool _showRouteId;

    /// <summary>The log level for all output from this scope.</summary>
    public LogLevel Level { get; }

    /// <summary>Static log messages added via <see cref="Message(string)"/>.</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>Dynamic log message factories added via <see cref="Message(Func{IExchange,string})"/>.</summary>
    public IReadOnlyList<Func<IExchange, string>> MessageFuncs => _messageFuncs;

    /// <summary>Header names to include in log output.</summary>
    public IReadOnlyList<string> HeaderNames => _headerNames;

    /// <summary>Property names to include in log output.</summary>
    public IReadOnlyList<string> PropertyNames => _propertyNames;

    /// <summary>Whether the route ID is included in log output.</summary>
    public bool IncludeRouteId => _showRouteId;

    internal RichLogScopeDefinition(LogLevel level) { Level = level; }

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>Adds a static message to the log output.</summary>
    public RichLogScopeDefinition Message(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _messages.Add(message);
        return this;
    }

    /// <summary>Adds a dynamic message factory to the log output.</summary>
    public RichLogScopeDefinition Message(Func<IExchange, string> messageFactory)
    {
        ArgumentNullException.ThrowIfNull(messageFactory);
        _messageFuncs.Add(messageFactory);
        return this;
    }

    /// <summary>Includes the specified header value in log output.</summary>
    public RichLogScopeDefinition Header(string headerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        _headerNames.Add(headerName);
        return this;
    }

    /// <summary>Includes the specified exchange property in log output.</summary>
    public RichLogScopeDefinition Property(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        _propertyNames.Add(propertyName);
        return this;
    }

    /// <summary>Includes the route ID in log output.</summary>
    public RichLogScopeDefinition ShowRouteId(bool value = true)
    {
        _showRouteId = value;
        return this;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this rich-log scope and returns the parent route definition.</summary>
    public IRouteDefinition EndLog()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndLog() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndLog();

    // ── IProcessorDefinition ──────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var loggerFactory = context.GetService<ILoggerFactory>();
        if (loggerFactory != null)
        {
            var logger = loggerFactory.CreateLogger("redb.Route");
            return new RichLogProcessor(logger, Level, _messages, _messageFuncs,
                _headerNames, _propertyNames, _showRouteId);
        }
        return new DelegateProcessor(_ => { });
    }
}
