using System.Diagnostics.Metrics;

namespace redb.Route.Telemetry;

/// <summary>
/// Metrics instruments for EIP processors (CircuitBreaker, Throttle, Filter, Debounce, etc.).
/// Subscribe to meter <see cref="RouteMetrics.MeterName"/> ("redb.Route") to capture these.
/// </summary>
public static class ProcessorMetrics
{
    // ── Circuit Breaker ─────────────────────────────────────────
    /// <summary>Number of times a circuit breaker tripped to Open state.</summary>
    public static readonly Counter<long> CircuitBreakerTripped =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.circuitbreaker.tripped", "times", "Circuit breaker state transitions to Open.");

    /// <summary>Exchanges rejected while circuit is Open.</summary>
    public static readonly Counter<long> CircuitBreakerRejected =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.circuitbreaker.rejected", "exchanges", "Exchanges rejected by an open circuit breaker.");

    // ── Throttle ────────────────────────────────────────────────
    /// <summary>Exchanges delayed by throttle (rate limiter).</summary>
    public static readonly Counter<long> ThrottleDelayed =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.throttle.delayed", "exchanges", "Exchanges delayed due to rate limiting.");

    // ── Filter ──────────────────────────────────────────────────
    /// <summary>Exchanges dropped by filter predicates.</summary>
    public static readonly Counter<long> FilterDropped =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.filter.dropped", "exchanges", "Exchanges dropped by a filter predicate.");

    // ── Debounce ────────────────────────────────────────────────
    /// <summary>Exchanges discarded (superseded by a newer message for the same key).</summary>
    public static readonly Counter<long> DebounceDiscarded =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.debounce.discarded", "exchanges", "Exchanges replaced before the quiet period elapsed.");

    /// <summary>Exchanges flushed (forwarded after quiet period).</summary>
    public static readonly Counter<long> DebounceFlushed =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.debounce.flushed", "exchanges", "Exchanges forwarded after the debounce quiet period.");

    // ── Splitter ────────────────────────────────────────────────
    /// <summary>Total parts produced by splitter processors.</summary>
    public static readonly Counter<long> SplitterParts =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.splitter.parts", "parts", "Total parts produced by splitter processors.");

    // ── Timeout ─────────────────────────────────────────────────
    /// <summary>Exchanges that exceeded the configured timeout.</summary>
    public static readonly Counter<long> TimeoutExpired =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.timeout.expired", "exchanges", "Exchanges that exceeded their processing timeout.");

    // ── WireTap ─────────────────────────────────────────────────
    /// <summary>Clones successfully dispatched to a wire-tap branch (fire-and-forget).</summary>
    public static readonly Counter<long> WireTapDispatched =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.wiretap.dispatched", "exchanges", "Clones successfully handed off to a wire-tap branch.");

    /// <summary>Wire-tap branch failures. Logged but never propagated to the main pipeline.</summary>
    public static readonly Counter<long> WireTapFailed =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.wiretap.failed", "exchanges", "Wire-tap branches that failed in background. Never affects the main pipeline.");

    // ── Multicast ───────────────────────────────────────────────
    /// <summary>Number of branches dispatched per multicast invocation (histogram).</summary>
    public static readonly Histogram<long> MulticastBranches =
        RouteMetrics.Meter.CreateHistogram<long>("redb.route.multicast.branches", "branches", "Number of target branches dispatched per multicast invocation.");

    /// <summary>Multicast branches that failed with an unhandled exception.</summary>
    public static readonly Counter<long> MulticastFailedBranches =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.multicast.failed_branches", "branches", "Multicast target branches that failed with an unhandled exception.");

    // ── Recipient List ──────────────────────────────────────────
    /// <summary>Number of recipients resolved per recipient-list invocation (histogram).</summary>
    public static readonly Histogram<long> RecipientListRecipients =
        RouteMetrics.Meter.CreateHistogram<long>("redb.route.recipientlist.recipients", "recipients", "Number of recipients resolved per recipient-list invocation.");

    // ── Aggregator ──────────────────────────────────────────────
    /// <summary>Aggregation groups completed by the completion predicate.</summary>
    public static readonly Counter<long> AggregatorCompleted =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.aggregator.completed", "groups", "Aggregation groups closed by the completion predicate.");

    /// <summary>Aggregation groups closed by inactivity / completion timeout.</summary>
    public static readonly Counter<long> AggregatorTimedOut =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.aggregator.timed_out", "groups", "Aggregation groups closed because their timeout elapsed before completion.");

    /// <summary>Aggregation groups currently kept in memory waiting for completion (up/down).</summary>
    public static readonly UpDownCounter<long> AggregatorInflightGroups =
        RouteMetrics.Meter.CreateUpDownCounter<long>("redb.route.aggregator.inflight_groups", "groups", "Aggregation groups currently buffered in memory waiting for completion or timeout.");

    // ── Idempotent Consumer ─────────────────────────────────────
    /// <summary>Exchanges identified as duplicates by the idempotent consumer.</summary>
    public static readonly Counter<long> IdempotentDuplicate =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.idempotent.duplicate", "exchanges", "Exchanges suppressed by the idempotent consumer as duplicates.");

    /// <summary>Exchanges accepted (first occurrence) by the idempotent consumer.</summary>
    public static readonly Counter<long> IdempotentPassed =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.idempotent.passed", "exchanges", "Exchanges that passed the idempotent consumer for the first time.");

    // ── Retry / Redelivery ──────────────────────────────────────
    /// <summary>Total retry attempts performed by RetryProcessor / OnException redelivery.</summary>
    public static readonly Counter<long> RetryAttempts =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.retry.attempts", "attempts", "Total retry attempts performed before final success or give-up.");

    /// <summary>Exchanges that succeeded only after one or more retries.</summary>
    public static readonly Counter<long> RetrySuccess =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.retry.success", "exchanges", "Exchanges that ultimately succeeded after at least one retry.");

    /// <summary>Exchanges that exhausted all retries and failed.</summary>
    public static readonly Counter<long> RetryExhausted =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.retry.exhausted", "exchanges", "Exchanges that exhausted all retry attempts without success.");

    // ── Saga ────────────────────────────────────────────────────
    /// <summary>Sagas that completed successfully end-to-end.</summary>
    public static readonly Counter<long> SagaCompleted =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.saga.completed", "sagas", "Sagas that completed all forward steps successfully.");

    /// <summary>Sagas that ran compensation after a forward-step failure.</summary>
    public static readonly Counter<long> SagaCompensated =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.saga.compensated", "sagas", "Sagas that triggered compensation actions after a forward-step failure.");

    /// <summary>Sagas where both a forward step and compensation failed.</summary>
    public static readonly Counter<long> SagaFailed =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.saga.failed", "sagas", "Sagas that failed and could not fully compensate.");

    // ── Dead-Letter Channel ─────────────────────────────────────
    /// <summary>Exchanges routed to a dead-letter channel after retries were exhausted.</summary>
    public static readonly Counter<long> DeadLetterSent =
        RouteMetrics.Meter.CreateCounter<long>("redb.route.deadletter.sent", "exchanges", "Exchanges routed to a dead-letter channel after exhausting retries.");
}
