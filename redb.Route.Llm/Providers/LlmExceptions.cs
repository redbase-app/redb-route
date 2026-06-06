using System;

namespace redb.Route.Llm.Providers;

/// <summary>
/// Provider signalled rate limiting (HTTP 429). Distinct from
/// <see cref="LlmTransientException"/> because the caller should respect
/// <see cref="RetryAfter"/> rather than retry immediately.
/// </summary>
public sealed class LlmRateLimitException : Exception
{
    /// <summary>Provider identifier ("anthropic", "openai", ...).</summary>
    public string ProviderId { get; }

    /// <summary>Suggested wait time before retry, if the provider sent one.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Raw response body for diagnostics.</summary>
    public string? RawBody { get; }

    /// <summary>Creates a rate-limit exception.</summary>
    public LlmRateLimitException(string providerId, string message, TimeSpan? retryAfter = null, string? rawBody = null, Exception? inner = null)
        : base(message, inner)
    {
        ProviderId = providerId;
        RetryAfter = retryAfter;
        RawBody = rawBody;
    }
}

/// <summary>
/// Transient provider failure that is safe to retry (HTTP 5xx, Anthropic's
/// <c>overloaded</c> 529, network resets, timeouts). The caller is expected
/// to apply its own back-off / circuit-breaker policy.
/// </summary>
public sealed class LlmTransientException : Exception
{
    /// <summary>Provider identifier.</summary>
    public string ProviderId { get; }

    /// <summary>HTTP status code, when known.</summary>
    public int? StatusCode { get; }

    /// <summary>Raw response body for diagnostics.</summary>
    public string? RawBody { get; }

    /// <summary>Creates a transient-failure exception.</summary>
    public LlmTransientException(string providerId, string message, int? statusCode = null, string? rawBody = null, Exception? inner = null)
        : base(message, inner)
    {
        ProviderId = providerId;
        StatusCode = statusCode;
        RawBody = rawBody;
    }
}
