using System.Diagnostics.Metrics;

namespace redb.Route.Telemetry;

/// <summary>
/// Per-step metric instruments for the <c>.Metered()</c> DSL.
/// Uses the same <see cref="Meter"/> as <see cref="RouteMetrics"/> to allow a single
/// <c>.AddMeter("redb.Route")</c> subscription in OpenTelemetry configuration.
/// </summary>
public static class StepMetrics
{
    private static readonly Meter _meter = RouteMetrics.Meter;

    /// <summary>Total step executions completed successfully.</summary>
    public static readonly Counter<long> StepProcessed =
        _meter.CreateCounter<long>("redb.route.step.processed", "exchanges",
            "Total exchanges processed by a named step.");

    /// <summary>Step executions that failed with an unhandled exception.</summary>
    public static readonly Counter<long> StepFailed =
        _meter.CreateCounter<long>("redb.route.step.failed", "exchanges",
            "Exchanges that failed at a named step.");

    /// <summary>Step execution duration histogram.</summary>
    public static readonly Histogram<double> StepDuration =
        _meter.CreateHistogram<double>("redb.route.step.duration", "ms",
            "Duration of a named step execution in milliseconds.");
}
