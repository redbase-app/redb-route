using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using static redb.Route.Demo.Routes.DemoEndpoints;
using static redb.Route.Demo.Routes.DemoHelpers;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Error-handling EIPs: DoTry/DoCatch/DoFinally, CircuitBreaker, Retry, DeadLetterChannel.
/// </summary>
internal sealed class ErrorHandlingRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public ErrorHandlingRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigureTryCatchRoute();
        ConfigureCircuitBreakerRoute();
        ConfigureRetryRoute();
        ConfigureDeadLetterRoute();
    }

    /// <summary>
    /// DoTry / DoCatch / DoFinally — structured error handling inside a route.
    /// Message flows: try body → catch if error → finally always.
    /// </summary>
    private void ConfigureTryCatchRoute()
    {
        From("direct://demo-try-catch")
            .RouteId("demo-try-catch")
            .Log("[TRY-CATCH] ▶ Starting risky operation...")

            .DoTry()
                .Log("[TRY-CATCH]   try: parsing body...")
                .Process(e =>
                {
                    var body = e.In.Body?.ToString() ?? "";
                    if (body.Contains("BOOM")) throw new InvalidOperationException("Body contains BOOM!");
                    e.In.Headers["parsed"] = "true";
                })
                .Log("[TRY-CATCH]   try: ✓ parsed OK")

            .DoCatch<InvalidOperationException>()
                .Log("[TRY-CATCH]   catch: ✖ InvalidOperation — ${exception.message}")
                .SetHeader("parsed", "false")
                .SetHeader("error", e => e.Exception?.Message ?? "unknown")

            .DoCatch<Exception>()
                .Log("[TRY-CATCH]   catch: ✖ General error — ${exception.message}")
                .SetHeader("parsed", "false")

            .DoFinally()
                .Log("[TRY-CATCH]   finally: cleanup, parsed=${header.parsed}")
                .RemoveHeader("tempData")

            .End()

            .Log("[TRY-CATCH] ◀ Done, parsed=${header.parsed}");
    }

    /// <summary>
    /// CircuitBreaker — protects against cascading failures.
    /// 3 failures → circuit opens → fallback → auto-recovery after 10s.
    /// </summary>
    private void ConfigureCircuitBreakerRoute()
    {
        From("direct://demo-circuit-breaker")
            .RouteId("demo-circuit-breaker")
            .Log("[CB] ▶ Calling unreliable service...")

            .CircuitBreaker(cb => cb
                .Threshold(3)
                .ResetTimeout(TimeSpan.FromSeconds(10))
                .HalfOpenMaxCalls(1)
                .FallBack(fb => fb
                    .Log("[CB] ⚡ FALLBACK: circuit is open, returning cached response")
                    .SetBody(e => "{\"source\":\"cache\",\"note\":\"circuit breaker active\"}")
                    .SetHeader("cb.state", "open")))

            .Log("[CB]   → calling external API...")
            .Process(e =>
            {
                if (GetHeader(e, "fail") == "true")
                    throw new TimeoutException("External API timed out!");
            })
            .Log("[CB] ◀ Response: ${body}");
    }

    /// <summary>
    /// Retry — automatic retry with exponential backoff.
    /// Fails twice, succeeds on third attempt.
    /// </summary>
    private void ConfigureRetryRoute()
    {
        From("direct://demo-retry")
            .RouteId("demo-retry")
            .Log("[RETRY] ▶ Processing with retry policy...")

            // NOTE: fluent .Retry(...) DSL is not currently exposed on IRouteDefinition;
            // retries are configured via OnException(...).MaximumRedeliveries(...) at the route-context level.
            .Log("[RETRY]   → attempt...")
            .Process(e =>
            {
                var attempt = e.Properties.TryGetValue("RetryAttempt", out var a) ? (int)a! : 0;
                e.In.Headers["retry.attempt"] = attempt.ToString();
                if (attempt < 2) throw new TimeoutException($"Timeout on attempt {attempt}");
            })
            .Log("[RETRY] ✓ Succeeded on attempt ${header.retry.attempt}")

            .Log("[RETRY] ◀ Done");
    }

    /// <summary>
    /// DeadLetterChannel — failed messages go to a dead-letter SEDA queue
    /// instead of being lost. Separate consumer processes them.
    /// </summary>
    private void ConfigureDeadLetterRoute()
    {
        From("direct://demo-dead-letter")
            .RouteId("demo-dead-letter")
            .Log("[DLQ] ▶ Processing message...")
            // NOTE: fluent .DeadLetterChannel(uri) DSL is not currently exposed on IRouteDefinition;
            // dead-letter routing is configured via OnException(...).To(DeadLetterQueue).Handled(true) instead.
            .Process(e =>
            {
                if (GetHeader(e, "poison") == "true")
                    throw new InvalidOperationException("Poison message detected!");
            })
            .Log("[DLQ] ✓ Message processed successfully");

        // Dead-letter consumer
        From(DeadLetterQueue)
            .RouteId("demo-dlq-consumer")
            .Log("[DLQ-SINK] ▶ Dead letter received: ${body}")
            .Log("[DLQ-SINK]   exception: ${exception.message}")
            .SetHeader("dlq.receivedAt", e => DateTime.UtcNow.ToString("o"))
            .Log("[DLQ-SINK] ◀ Archived");

        // Poison message sender — cron uses this to exercise the DLQ path
        From("direct://demo-dead-letter-poison")
            .RouteId("demo-dead-letter-poison")
            .SetHeader("poison", "true")
            .Log("[DLQ-POISON] ▶ Sending poison message to DLQ route...")
            .To("direct://demo-dead-letter");
    }
}
