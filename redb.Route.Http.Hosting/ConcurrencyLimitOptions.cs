using System.Threading.RateLimiting;

namespace redb.Route.Http;

/// <summary>
/// Per-registration admission limit for one route on the shared Kestrel host. Kestrel itself
/// accepts an unbounded number of concurrent requests; this caps how many of them may EXECUTE
/// a given route's handler at once, sheds the overflow with an immediate status reply
/// (load shedding, not backpressure — the industry norm: ASP.NET ConcurrencyLimiter policies,
/// nginx limit_conn, Envoy max_concurrent_requests), and leaves every other registration on the
/// same listener untouched.
/// <para>
/// Backed by <see cref="ConcurrencyLimiter"/>: up to <see cref="MaxConcurrentRequests"/> permits,
/// an optional FIFO wait queue of <see cref="RequestQueueLimit"/>, everything beyond answered with
/// <see cref="RejectStatusCode"/> + <c>Retry-After</c> before any pipeline work happens.
/// </para>
/// </summary>
public sealed class ConcurrencyLimitOptions
{
    /// <summary>Maximum concurrent executions of the route handler. Must be at least 1.</summary>
    public required int MaxConcurrentRequests { get; init; }

    /// <summary>
    /// How many requests over the limit WAIT for a permit (FIFO) instead of being rejected.
    /// 0 (default) = reject immediately once the permits are taken.
    /// </summary>
    public int RequestQueueLimit { get; init; }

    /// <summary>Status code for a shed request. Default 429 Too Many Requests.</summary>
    public int RejectStatusCode { get; init; } = 429;

    /// <summary>Value of the <c>Retry-After</c> header on a shed request; 0 = do not send it. Default 1.</summary>
    public int RetryAfterSeconds { get; init; } = 1;

    /// <summary>
    /// Invoked once per shed request, before the reject response is written. The hosting layer
    /// deliberately knows nothing about redb.Route endpoint statistics — a connector passes
    /// <c>endpoint.RecordRejected</c> here.
    /// </summary>
    public Action? OnRejected { get; init; }

    internal void Validate()
    {
        if (MaxConcurrentRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRequests), MaxConcurrentRequests,
                "maxConcurrentRequests must be at least 1 (omit the limit entirely for unlimited).");
        if (RequestQueueLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(RequestQueueLimit), RequestQueueLimit,
                "requestQueueLimit cannot be negative.");
        if (RejectStatusCode is < 400 or > 599)
            throw new ArgumentOutOfRangeException(nameof(RejectStatusCode), RejectStatusCode,
                "rejectStatusCode must be a 4xx or 5xx status code.");
        if (RetryAfterSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(RetryAfterSeconds), RetryAfterSeconds,
                "retryAfterSeconds cannot be negative (0 = do not send the header).");
    }

    internal ConcurrencyLimiter CreateLimiter() => new(new ConcurrencyLimiterOptions
    {
        PermitLimit = MaxConcurrentRequests,
        QueueLimit = RequestQueueLimit,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    /// <summary>
    /// Validates the option quartet as it appears on an ENDPOINT, where 0 for
    /// <paramref name="maxConcurrentRequests"/> means "feature off" (unlike this class, whose
    /// instances always describe an active limit). One validator for every connector that
    /// exposes the options, so the rules cannot drift apart.
    /// </summary>
    public static void ValidateShape(int maxConcurrentRequests, int requestQueueLimit,
        int rejectStatusCode, int retryAfterSeconds)
    {
        if (maxConcurrentRequests < 0)
            throw new ArgumentException("maxConcurrentRequests must be >= 0 (0 = unlimited).");
        if (requestQueueLimit < 0)
            throw new ArgumentException("requestQueueLimit must be >= 0.");
        if (maxConcurrentRequests == 0 && requestQueueLimit > 0)
            throw new ArgumentException(
                "requestQueueLimit without maxConcurrentRequests is meaningless: there is no limit to queue behind. Set maxConcurrentRequests >= 1.");
        if (rejectStatusCode is < 400 or > 599)
            throw new ArgumentException("rejectStatusCode must be a 4xx or 5xx status code.");
        if (retryAfterSeconds < 0)
            throw new ArgumentException("retryAfterSeconds must be >= 0 (0 = do not send the header).");
    }

    /// <summary>
    /// Builds the registration-level limit from endpoint options, or null when the feature is off
    /// (<paramref name="maxConcurrentRequests"/> = 0). <paramref name="onRejected"/> is the
    /// connector's statistics hook (<c>endpoint.RecordRejected</c>).
    /// </summary>
    public static ConcurrencyLimitOptions? FromEndpoint(int maxConcurrentRequests, int requestQueueLimit,
        int rejectStatusCode, int retryAfterSeconds, Action? onRejected)
        => maxConcurrentRequests <= 0
            ? null
            : new ConcurrencyLimitOptions
            {
                MaxConcurrentRequests = maxConcurrentRequests,
                RequestQueueLimit = requestQueueLimit,
                RejectStatusCode = rejectStatusCode,
                RetryAfterSeconds = retryAfterSeconds,
                OnRejected = onRejected,
            };
}
