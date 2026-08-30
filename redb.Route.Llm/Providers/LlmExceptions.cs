using System;
using System.Net.Http;

namespace redb.Route.Llm.Providers;

/// <summary>
/// Turns a failed HTTP response into the right provider exception. One place, because the
/// mapping is the same for every OpenAI-shaped API and getting it wrong is invisible: a caller
/// that cannot tell 429 from 401 either retries a dead key forever or gives up on a queue that
/// would have cleared in a second.
/// </summary>
public static class LlmHttpErrors
{
    /// <summary>
    /// Builds the exception for a non-2xx response: 429 → <see cref="LlmRateLimitException"/>
    /// (honouring <c>Retry-After</c> in both its delta and date forms), 5xx and the soft
    /// "overloaded" 529 → <see cref="LlmTransientException"/>, anything else →
    /// <see cref="HttpRequestException"/> carrying the status.
    /// </summary>
    /// <param name="providerId">Provider identifier, for the exception payload.</param>
    /// <param name="response">The failed response; only status and headers are read.</param>
    /// <param name="message">Ready-made message — providers word their own summaries.</param>
    /// <param name="rawBody">Response body already read by the caller, for diagnostics.</param>
    public static Exception FromResponse(
        string providerId, HttpResponseMessage response, string message, string? rawBody)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = (int)response.StatusCode;

        if (status == 429)
        {
            TimeSpan? retryAfter = null;

            if (response.Headers.RetryAfter is { } header)
            {
                if (header.Delta is { } delta) retryAfter = delta;
                else if (header.Date is { } when) retryAfter = when - DateTimeOffset.UtcNow;
            }

            return new LlmRateLimitException(providerId, message, retryAfter, rawBody);
        }

        // 529 is Anthropic's soft "overloaded"; it is not a standard code, and treating it as
        // transient is right for anyone who ever returns it.
        if (status == 529 || status is >= 500 and <= 599)
            return new LlmTransientException(providerId, message, status, rawBody);

        return new HttpRequestException(message, inner: null, statusCode: response.StatusCode);
    }
}

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
