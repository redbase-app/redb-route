using System.Diagnostics;
using redb.Route.Abstractions;

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

    /// <summary>Creates an instrumented processor wrapper.</summary>
    /// <param name="inner">The processor to instrument.</param>
    /// <param name="operationName">Operation name for the activity span (e.g., "route.process", "direct://output send").</param>
    public InstrumentedProcessor(IProcessor inner, string operationName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _operationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        using var activity = RouteActivitySource.Source.StartActivity(_operationName, ActivityKind.Internal);

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
    /// <summary>Records an exception event on the activity.</summary>
    internal static void RecordException(this Activity activity, Exception ex)
    {
        var tags = new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName },
            { "exception.message", ex.Message },
            { "exception.stacktrace", ex.StackTrace }
        };
        activity.AddEvent(new ActivityEvent("exception", tags: tags));
    }
}
