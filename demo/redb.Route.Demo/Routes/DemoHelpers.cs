using System.Text.Encodings.Web;
using System.Text.Json;
using redb.Route.Abstractions;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Pure helpers shared by every demo route builder.
/// No state, no DI - just utilities for header access and HTTP response shaping.
/// </summary>
internal static class DemoHelpers
{
    /// <summary>Reads a header value as string (null if missing).</summary>
    public static string? GetHeader(IExchange e, string key)
        => e.In.Headers.TryGetValue(key, out var v) ? v?.ToString() : null;

    /// <summary>Safe substring used by error-handler logging.</summary>
    public static string? Trunc(string? s, int max = 200)
        => s is null ? null : s[..Math.Min(max, s.Length)];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Builds the JSON response returned by the main HTTP pipeline.</summary>
    public static string BuildResponse(IExchange e)
    {
        return JsonSerializer.Serialize(new
        {
            success = true,
            traceId = GetHeader(e, "traceId"),
            mode = GetHeader(e, "mode"),
            stamps = new
            {
                rabbit = GetHeader(e, "stamp.rabbit"),
                amqp = GetHeader(e, "stamp.amqp"),
                grpc = GetHeader(e, "stamp.grpc"),
                wmq = GetHeader(e, "stamp.wmq"),
                vm = GetHeader(e, "stamp.vm"),
            },
            pipeline = "HTTP -> Direct -> RabbitMQ -> AMQP -> gRPC -> WMQ -> DirectVM -> "
                     + "SQL(tx) -> Kafka(tap) -> File(tap) -> VM(tap) -> WMQ(tap)",
            startedAt = GetHeader(e, "startedAt"),
        }, JsonOpts);
    }
}
