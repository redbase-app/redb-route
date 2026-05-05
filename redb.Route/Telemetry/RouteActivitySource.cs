using System.Diagnostics;

namespace redb.Route.Telemetry;

/// <summary>
/// Centralized <see cref="ActivitySource"/> for the route engine.
/// Consumers of OpenTelemetry can subscribe to <see cref="SourceName"/> to collect traces.
/// </summary>
public static class RouteActivitySource
{
    /// <summary>Source name used for OpenTelemetry instrumentation.</summary>
    public const string SourceName = "redb.Route";

    /// <summary>The shared activity source instance.</summary>
    public static readonly ActivitySource Source = new(SourceName, GetVersion());

    private static string GetVersion()
    {
        return typeof(RouteActivitySource).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
