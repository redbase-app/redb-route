using System.Globalization;
using System.Text;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Applies an RFC 6585 §4 "429 Too Many Requests" short-circuit to an exchange. Shared
/// by <see cref="ThrottleProcessor"/> and <see cref="KeyedThrottleProcessor"/> when their
/// <c>rejectOnOverflow</c> flag is set.
/// <para>
/// Writes a small OAuth-style JSON body (<c>error</c> / <c>error_description</c> /
/// <c>retry_after</c>), a <c>Retry-After</c> response header (RFC 7231 §7.1.3 delta-seconds
/// form), and the <c>redbHttp.ResponseCode</c> bridge header used by the redb.Route HTTP
/// consumer. The exchange is stopped via <see cref="IExchange.Stop"/> so no downstream
/// processor runs (no WireTap, no tx commit, no idempotency capture).
/// </para>
/// </summary>
internal static class ThrottleRejection
{
    /// <summary>Constant <c>error</c> code returned in the 429 body.</summary>
    public const string ErrorCode = "rate_limit_exceeded";

    /// <summary>
    /// Writes the 429 short-circuit on <paramref name="exchange"/>. The retry-after value
    /// is the configured throttle period rounded up to whole seconds (per RFC 7231 §7.1.3
    /// delta-seconds), clamped to a minimum of 1 second so the header is never absent or
    /// degenerate when the period is sub-second.
    /// </summary>
    public static void Apply(IExchange exchange, TimeSpan period)
    {
        var retryAfter = (int)Math.Max(1, Math.Ceiling(period.TotalSeconds));
        var retryAfterStr = retryAfter.ToString(CultureInfo.InvariantCulture);

        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["error"] = ErrorCode,
            ["error_description"] = $"Rate limit exceeded. Retry after {retryAfterStr} second(s).",
            ["retry_after"] = retryAfter,
        });

        var msg = new Message(body)
        {
            ContentType = "application/json",
        };
        msg.Headers["redbHttp.ResponseCode"] = 429;
        msg.Headers["redbHttp.ResponseContentType"] = "application/json";
        msg.Headers["Retry-After"] = retryAfterStr;

        exchange.Out = msg;
        exchange.ExceptionHandled = true;
        exchange.Stop();
    }
}
