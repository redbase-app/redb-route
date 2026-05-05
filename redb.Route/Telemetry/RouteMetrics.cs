using System.Diagnostics.Metrics;

namespace redb.Route.Telemetry;

/// <summary>
/// Provides <see cref="Meter"/> and standard counters/histograms for route engine metrics.
/// Consumers subscribe to meter name <see cref="MeterName"/> via OpenTelemetry.
/// </summary>
public static class RouteMetrics
{
    /// <summary>Meter name for OpenTelemetry subscription.</summary>
    public const string MeterName = "redb.Route";

    internal static readonly Meter Meter = new(MeterName, GetVersion());

    /// <summary>Total exchanges processed (counter).</summary>
    public static readonly Counter<long> ExchangesProcessed =
        Meter.CreateCounter<long>("redb.route.exchanges.processed", "exchanges", "Total exchanges processed by all routes.");

    /// <summary>Exchanges that failed with an unhandled exception (counter).</summary>
    public static readonly Counter<long> ExchangesFailed =
        Meter.CreateCounter<long>("redb.route.exchanges.failed", "exchanges", "Exchanges that resulted in an unhandled exception.");

    /// <summary>Exchange processing duration (histogram).</summary>
    public static readonly Histogram<double> ExchangeDuration =
        Meter.CreateHistogram<double>("redb.route.exchange.duration", "ms", "Duration of exchange processing in milliseconds.");

    /// <summary>Currently active exchanges being processed (up-down counter).</summary>
    public static readonly UpDownCounter<long> ExchangesInflight =
        Meter.CreateUpDownCounter<long>("redb.route.exchanges.inflight", "exchanges", "Number of exchanges currently being processed.");

    private static string GetVersion()
    {
        return typeof(RouteMetrics).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
