using System.Diagnostics.Metrics;
using redb.Route.Telemetry;

namespace redb.Route.Llm.Telemetry;

/// <summary>
/// LLM-specific counters and histograms. Published on the same shared meter
/// (<see cref="RouteMetrics.MeterName"/> = <c>"redb.Route"</c>) so a single
/// <c>.AddMeter("redb.Route")</c> in OpenTelemetry picks up everything.
/// <para>
/// Standard tags: <c>llm.provider</c>, <c>llm.model.id</c>, <c>llm.factory</c>.
/// Optional: <c>llm.stop_reason</c>, <c>llm.tool.name</c>.
/// </para>
/// </summary>
public static class LlmMetrics
{
    /// <summary>Meter instance. Same canonical name as <see cref="RouteMetrics.MeterName"/>.</summary>
    public static readonly Meter Meter = new(RouteMetrics.MeterName, GetVersion());

    private static string GetVersion() =>
        typeof(LlmMetrics).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Total LLM calls dispatched to a provider (one per provider HTTP roundtrip).</summary>
    public static readonly Counter<long> Calls =
        Meter.CreateCounter<long>("redb.route.llm.calls", "calls",
            "Number of provider calls (one per HTTP roundtrip; an agent run with N iterations emits N).");

    /// <summary>Failed provider calls (timeout, transport error, parsing error).</summary>
    public static readonly Counter<long> CallsFailed =
        Meter.CreateCounter<long>("redb.route.llm.calls.failed", "calls",
            "Provider calls that ended with an exception.");

    /// <summary>Provider call latency in milliseconds (one observation per call).</summary>
    public static readonly Histogram<double> CallDuration =
        Meter.CreateHistogram<double>("redb.route.llm.call.duration", "ms",
            "Wall-clock duration of a single provider call.");

    /// <summary>Input tokens billed (sum across calls).</summary>
    public static readonly Counter<long> TokensIn =
        Meter.CreateCounter<long>("redb.route.llm.tokens.in", "tokens",
            "Input tokens reported by the provider.");

    /// <summary>Output tokens billed.</summary>
    public static readonly Counter<long> TokensOut =
        Meter.CreateCounter<long>("redb.route.llm.tokens.out", "tokens",
            "Output tokens reported by the provider.");

    /// <summary>Estimated USD cost (counter; converted to long-cents via x10000).</summary>
    public static readonly Counter<long> CostMicroUsd =
        Meter.CreateCounter<long>("redb.route.llm.cost.usd_micro", "usd*1e-6",
            "Estimated cost in micro-USD (1e-6 USD).");

    /// <summary>Number of agent runs (one per <c>To(\"llm://...\")</c> exchange).</summary>
    public static readonly Counter<long> AgentRuns =
        Meter.CreateCounter<long>("redb.route.llm.agent.runs", "runs",
            "Number of agent runs (each may include multiple provider calls).");

    /// <summary>Total tool-loop iterations performed by the agent (histogram per run).</summary>
    public static readonly Histogram<int> AgentIterations =
        Meter.CreateHistogram<int>("redb.route.llm.agent.iterations", "iterations",
            "Tool-loop iterations per agent run.");

    /// <summary>Tool invocations (one per <c>tool_use</c> block executed).</summary>
    public static readonly Counter<long> ToolInvocations =
        Meter.CreateCounter<long>("redb.route.llm.tool.invocations", "calls",
            "Number of tool invocations performed by the agent.");

    /// <summary>Tool invocation failures.</summary>
    public static readonly Counter<long> ToolFailures =
        Meter.CreateCounter<long>("redb.route.llm.tool.failures", "calls",
            "Number of tool invocations that threw an exception.");
}
