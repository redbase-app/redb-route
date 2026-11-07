using System.Diagnostics;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Telemetry;

/// <summary>
/// The processor of <c>.SpanTag(key, value, target)</c>: puts one attribute on a span of the engine. The exchange is
/// not touched. Nothing happens when there is no such span, when it records no data (no listener, not sampled) — the
/// value is then not even evaluated — or when it has already ended. A value that fails to evaluate is skipped and
/// logged at Debug: telemetry does not change the outcome of the message.
/// </summary>
internal sealed class SpanTagProcessor : IProcessor
{
    private readonly string _key;
    private readonly IExpression _value;
    private readonly SpanTarget _target;
    private readonly ILogger? _logger;

    internal SpanTagProcessor(string key, IExpression value, SpanTarget target, ILogger? logger)
    {
        _key = key;
        _value = value;
        _target = target;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var span = _target == SpanTarget.Inbound ? (exchange as Exchange)?.InboundSpan : EnclosingSpan();
        if (span is not { IsAllDataRequested: true } || span.IsStopped)
            return Task.CompletedTask;

        object? value;
        try
        {
            value = SpanTagPolicy.ToAttributeValue(_value.Evaluate<object?>(exchange));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "SpanTag '{Key}': the value could not be evaluated; the attribute is not set.", _key);
            return Task.CompletedTask;
        }

        if (value is not null)
            span.SetTag(_key, value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The nearest span of the engine from <see cref="Activity.Current"/> up: an internal one (a <c>Traced</c> scope, the
    /// route span, a detached branch) or nothing. A transport span reached first means route tracing is off — no
    /// attribute then — and a span of another source is passed over, never written.
    /// </summary>
    private static Activity? EnclosingSpan()
    {
        for (var span = Activity.Current; span is not null; span = span.Parent)
        {
            if (span.Source == RouteActivitySource.Source)
                return span.Kind == ActivityKind.Internal ? span : null;
        }
        return null;
    }
}
