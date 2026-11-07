using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Abstractions;

namespace redb.Route.Telemetry;

/// <summary>
/// One reading of one instrument: how many measurements, their sum, the extremes, and the last
/// value. For a counter the measurements are deltas, so <see cref="Sum"/> is the running total;
/// for a histogram they are recorded values, so all five fields are meaningful.
/// </summary>
public readonly record struct MetricSnapshotPoint(long Count, double Sum, double Min, double Max, double Last);

/// <summary>
/// Read access to the OpenTelemetry layer from inside the process (METRICS_IN_ROUTE_PLAN, П3).
/// </summary>
/// <remarks>
/// <para>
/// The <c>"redb.Route"</c> meter is push-only, as every .NET <see cref="Meter"/> is: the EIP
/// counters (<c>throttle.delayed</c>, <c>circuitbreaker.tripped</c>, …) and the <c>.Metered()</c>
/// step durations go to a subscriber or nowhere. This service is that subscriber, kept in-process:
/// current values only — no windows, no percentiles, no history. Whoever needs those plugs in an
/// OpenTelemetry backend; a bus should not carry a small Prometheus inside.
/// </para>
/// <para>
/// Deliberately opt-in (<c>context.UseMetricsSnapshot()</c>): a
/// <see cref="MeterListener"/> receives a callback for every measurement of every instrument,
/// which is a tax all routes would pay for a feature few ask for.
/// </para>
/// </remarks>
public interface IMetricsSnapshot
{
    /// <summary>
    /// The current point of an instrument, keyed exactly by its tags: <paramref name="routeId"/>
    /// matches the <c>redb.route.id</c> tag, <paramref name="step"/> the <c>redb.route.step</c>
    /// tag; <c>null</c> means the measurement carried no such tag. No aggregation across keys —
    /// a point either was recorded with these tags or there is nothing to return.
    /// </summary>
    /// <param name="instrument">Instrument name, e.g. <c>redb.route.step.duration</c>.</param>
    /// <param name="routeId">Value of the <c>redb.route.id</c> tag, or null for untagged.</param>
    /// <param name="step">Value of the <c>redb.route.step</c> tag, or null for untagged.</param>
    /// <returns>The point, or <c>null</c> when nothing was recorded under this key.</returns>
    MetricSnapshotPoint? Point(string instrument, string? routeId = null, string? step = null);

    /// <summary>
    /// Convenience reading: the <see cref="MetricSnapshotPoint.Sum"/> — a counter's running
    /// total, a histogram's sum of values.
    /// </summary>
    double? Value(string instrument, string? routeId = null, string? step = null);
}

/// <summary>
/// The in-process subscriber behind <see cref="IMetricsSnapshot"/>: a <see cref="MeterListener"/>
/// on the <c>"redb.Route"</c> meter, accumulating per (instrument, route id, step).
/// </summary>
internal sealed class MetricsSnapshotListener : IMetricsSnapshot, IDisposable
{
    private sealed class Accumulator
    {
        private readonly object _gate = new();
        private long _count;
        private double _sum;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private double _last;

        public void Add(double value)
        {
            lock (_gate)
            {
                _count++;
                _sum += value;
                if (value < _min) _min = value;
                if (value > _max) _max = value;
                _last = value;
            }
        }

        public MetricSnapshotPoint Read()
        {
            lock (_gate)
            {
                return new MetricSnapshotPoint(_count, _sum, _min, _max, _last);
            }
        }
    }

    private readonly ConcurrentDictionary<(string Instrument, string RouteId, string Step), Accumulator> _points = new();
    private readonly MeterListener _listener;
    private int _disposed;

    public MetricsSnapshotListener()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RouteMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.Start();
    }

    private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string routeId = "", step = "";
        foreach (var tag in tags)
        {
            if (tag.Key == "redb.route.id") routeId = tag.Value?.ToString() ?? "";
            else if (tag.Key == "redb.route.step") step = tag.Value?.ToString() ?? "";
        }

        _points.GetOrAdd((instrument, routeId, step), static _ => new Accumulator()).Add(value);
    }

    /// <inheritdoc />
    public MetricSnapshotPoint? Point(string instrument, string? routeId = null, string? step = null)
        => _points.TryGetValue((instrument, routeId ?? "", step ?? ""), out var acc) ? acc.Read() : null;

    /// <inheritdoc />
    public double? Value(string instrument, string? routeId = null, string? step = null)
        => Point(instrument, routeId, step)?.Sum;

    /// <summary>
    /// Stops listening. Collected points stay readable — an operator inspecting a stopped context
    /// still sees what it measured.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        _listener.Dispose();
    }
}

/// <summary>
/// Registration for <see cref="IMetricsSnapshot"/>, following the same order as every other
/// pluggable service here: what the context was told, then the DI container — and no default,
/// because the whole point is that nobody pays for the listener without asking for it.
/// </summary>
public static class MetricsSnapshotRegistration
{
    /// <summary>
    /// Starts an in-process subscriber to the <c>"redb.Route"</c> meter on this context. The
    /// listener is disposed when the context stops; calling twice keeps the first subscriber.
    /// </summary>
    public static IRouteContext UseMetricsSnapshot(this IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetService<IMetricsSnapshot>() is not null)
            return context;

        var listener = new MetricsSnapshotListener();
        context.AddService(typeof(IMetricsSnapshot), listener);
        context.AddLifecycleListener(new DisposeOnStop(listener));
        return context;
    }

    /// <summary>DI registration: <c>services.AddMetricsSnapshot()</c>.</summary>
    public static IServiceCollection AddMetricsSnapshot(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMetricsSnapshot>(_ => new MetricsSnapshotListener());
        return services;
    }

    /// <summary>
    /// Resolves the snapshot, or <c>null</c> when none was registered — there is deliberately no
    /// always-on default.
    /// </summary>
    public static IMetricsSnapshot? GetMetricsSnapshot(this IRouteContext? context)
        => context?.GetService<IMetricsSnapshot>()
           ?? context?.GetServiceProvider()?.GetService(typeof(IMetricsSnapshot)) as IMetricsSnapshot;

    private sealed class DisposeOnStop(IDisposable listener) : IRouteLifecycleListener
    {
        public Task OnContextStopped(IRouteContext context, CancellationToken ct)
        {
            listener.Dispose();
            return Task.CompletedTask;
        }
    }
}
