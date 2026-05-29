using System.Diagnostics;

namespace redb.Route.Telemetry;

/// <summary>
/// Centralized telemetry identifiers and the shared <see cref="ActivitySource"/>
/// for the route engine. Consumers of OpenTelemetry subscribe with
/// <c>.AddSource(RouteActivitySource.SourceName)</c> for traces and
/// <c>.AddMeter(RouteMetrics.MeterName)</c> for metrics; both use the same
/// canonical name <see cref="TelemetryName"/> (<c>"redb.Route"</c>).
/// </summary>
public static class RouteActivitySource
{
    /// <summary>Canonical telemetry name shared by the meter and the activity source.</summary>
    public const string TelemetryName = "redb.Route";

    /// <summary>Source name used for OpenTelemetry tracing instrumentation.</summary>
    public const string SourceName = TelemetryName;

    /// <summary>The shared activity source instance.</summary>
    public static readonly ActivitySource Source = new(SourceName, GetVersion());

    private static string GetVersion()
    {
        return typeof(RouteActivitySource).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
