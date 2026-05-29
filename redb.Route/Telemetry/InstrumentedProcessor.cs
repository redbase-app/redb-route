using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Telemetry;

/// <summary>
/// Wraps an <see cref="IProcessor"/> with OpenTelemetry tracing.
/// Creates an <see cref="Activity"/> span per <see cref="Process"/> call,
/// recording exceptions and enriching tags from exchange metadata.
/// </summary>
public sealed class InstrumentedProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly string _operationName;
    private readonly Func<IExchange, string>? _nameTemplate;

    /// <summary>Creates an instrumented processor wrapper.</summary>
    /// <param name="inner">The processor to instrument.</param>
    /// <param name="operationName">Operation name for the activity span (e.g., "route.process", "direct://output send"). Supports <c>${...}</c> templates resolved at runtime.</param>
    public InstrumentedProcessor(IProcessor inner, string operationName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _operationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
        if (operationName.Contains("${"))
            _nameTemplate = ExpressionResolver.GetCompiledTemplate(operationName);
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var name = _nameTemplate != null ? _nameTemplate(exchange) : _operationName;
        using var activity = RouteActivitySource.Source.StartActivity(name, ActivityKind.Internal);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("redb.route.id", exchange.RouteId);
            activity.SetTag("redb.exchange.pattern", exchange.Pattern.ToString());

            if (exchange.In.Headers.TryGetValue("CamelCorrelationId", out var correlationId))
                activity.SetTag("redb.correlation.id", correlationId?.ToString());
        }

        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);

            if (activity != null && exchange.Exception != null && !exchange.ExceptionHandled)
            {
                activity.SetStatus(ActivityStatusCode.Error, exchange.Exception.Message);
                activity.RecordException(exchange.Exception);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
    }
}

/// <summary>
/// Extension methods for OpenTelemetry activity enrichment.
/// </summary>
internal static class ActivityExtensions
{
    /// <summary>
    /// Records an exception event on the activity. Uses the framework-built-in
    /// <c>Activity.AddException</c> when available (.NET 9+ / OTel semconv 1.6+),
    /// falling back to a manual <c>exception</c> event with OTel standard tags
    /// otherwise. Either way the resulting span event is interoperable with
    /// Jaeger / Tempo / OTLP collectors.
    /// </summary>
    internal static void RecordException(this Activity activity, Exception ex)
    {
#if NET9_0_OR_GREATER
        activity.AddException(ex);
#else
        var tags = new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName },
            { "exception.message", ex.Message },
            { "exception.stacktrace", ex.ToString() }
        };
        activity.AddEvent(new ActivityEvent("exception", tags: tags));
#endif
    }
}
