using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace redb.Route.Grpc;

/// <summary>
/// The gRPC wire protocol over HTTP/2, implemented here rather than taken from the
/// <c>Grpc.AspNetCore</c> server stack.
/// <para>
/// Reason: redb.Route hosts every HTTP-based transport (Http, As2, Soap) as a plain path route on the
/// shared Kestrel host and speaks the protocol itself. <c>MapGrpcService</c> would instead demand
/// ASP.NET service registration and endpoint routing wired <i>before</i> the host is built, which
/// forces one gRPC endpoint per port, a host restart on every hot-reload, and a header-based route
/// selector instead of the method address. Owning the framing keeps a gRPC route exactly what every
/// other route is: a URI, an endpoint, a consumer.
/// </para>
/// <para>
/// Framing (gRPC over HTTP/2): a message is <c>[1 byte compressed-flag][4 bytes length, big-endian]
/// [message bytes]</c>, repeated for streams. Request path is <c>/package.Service/Method</c>,
/// content-type is <c>application/grpc</c>(<c>+proto</c>). The HTTP status is always 200 — the real
/// outcome travels in the <c>grpc-status</c> / <c>grpc-message</c> trailers. A caller deadline arrives
/// as <c>grpc-timeout</c>.
/// </para>
/// </summary>
internal static class GrpcWire
{
    /// <summary>Base content type of a gRPC message stream.</summary>
    public const string ContentType = "application/grpc";

    /// <summary>Protobuf-qualified content type; what generated clients send.</summary>
    public const string ProtoContentType = "application/grpc+proto";

    public const string StatusTrailer = "grpc-status";
    public const string MessageTrailer = "grpc-message";
    public const string TimeoutHeader = "grpc-timeout";
    public const string EncodingHeader = "grpc-encoding";
    public const string AcceptEncodingHeader = "grpc-accept-encoding";

    /// <summary>Size of the length-prefix that precedes every message on the wire.</summary>
    public const int PrefixLength = 5;

    /// <summary>True when the content type belongs to a gRPC call (any sub-format).</summary>
    public static bool IsGrpcContentType(string? contentType) =>
        contentType is not null
        && contentType.StartsWith(ContentType, StringComparison.OrdinalIgnoreCase);

    // ── reading ──────────────────────────────────────────────

    /// <summary>Message encodings we can decode and advertise in <c>grpc-accept-encoding</c>.</summary>
    public const string SupportedEncodings = "identity,gzip";

    /// <summary>
    /// Reads one length-prefixed message, decompressing it when the caller set the compressed flag and
    /// declared a codec we support. Returns <c>null</c> at a clean end of stream (no more messages),
    /// which is how a client signals it is done sending.
    /// </summary>
    /// <exception cref="GrpcProtocolException">
    /// Truncated prefix/body, an unsupported codec, or a message above <paramref name="maxMessageSize"/>
    /// either on the wire or after decompression.
    /// </exception>
    public static async Task<byte[]?> ReadMessageAsync(
        Stream body, int maxMessageSize, string? requestEncoding, CancellationToken ct)
    {
        var prefix = new byte[PrefixLength];
        var read = await ReadExactAsync(body, prefix, ct).ConfigureAwait(false);
        if (read == 0) return null;                       // clean end of stream
        if (read < PrefixLength)
            throw new GrpcProtocolException(StatusCode.Internal, "Truncated gRPC message prefix.");

        var compressed = prefix[0] != 0;
        if (compressed && !IsGzip(requestEncoding))
            throw new GrpcProtocolException(StatusCode.Unimplemented,
                $"Unsupported grpc-encoding '{requestEncoding ?? "(none)"}'. Supported: {SupportedEncodings}.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(1, 4));
        if (maxMessageSize > 0 && length > (uint)maxMessageSize)
            throw new GrpcProtocolException(StatusCode.ResourceExhausted,
                $"Received message exceeds the limit of {maxMessageSize} bytes.");

        if (length == 0) return [];

        var payload = new byte[length];
        var got = await ReadExactAsync(body, payload, ct).ConfigureAwait(false);
        if (got < payload.Length)
            throw new GrpcProtocolException(StatusCode.Internal, "Truncated gRPC message body.");

        if (!compressed) return payload;

        // The size limit is re-checked after inflation: the on-wire check alone would let a small
        // compressed frame expand into an arbitrarily large buffer.
        return Decompress(payload, maxMessageSize);
    }

    /// <summary>True when the encoding names gzip (the one codec we implement).</summary>
    public static bool IsGzip(string? encoding) =>
        encoding is not null && encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);

    private static byte[] Decompress(byte[] payload, int maxMessageSize)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var buffer = new byte[8192];
        int chunk;
        while ((chunk = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, chunk);
            if (maxMessageSize > 0 && output.Length > maxMessageSize)
                throw new GrpcProtocolException(StatusCode.ResourceExhausted,
                    $"Decompressed message exceeds the limit of {maxMessageSize} bytes.");
        }

        return output.ToArray();
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        return offset;
    }

    // ── writing ──────────────────────────────────────────────

    /// <summary>
    /// Writes one length-prefixed message and flushes it (a stream consumer must see it now).
    /// With <paramref name="compress"/> the payload is gzipped and the compressed flag is set — only ever
    /// called when the caller advertised gzip in <c>grpc-accept-encoding</c>.
    /// </summary>
    public static async Task WriteMessageAsync(
        HttpResponse response, byte[] payload, bool compress, CancellationToken ct)
    {
        if (compress) payload = Compress(payload);

        var frame = new byte[PrefixLength + payload.Length];
        frame[0] = compress ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame, PrefixLength);

        await response.Body.WriteAsync(frame, ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the terminating status. Called exactly once per call, after any messages. When nothing has
    /// been written yet this produces a valid "trailers-only" response, which is what clients expect for
    /// a failure with no payload.
    /// </summary>
    public static void WriteStatus(HttpResponse response, StatusCode status, string? detail = null)
    {
        response.AppendTrailer(StatusTrailer, ((int)status).ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(detail))
            response.AppendTrailer(MessageTrailer, PercentEncode(detail!));
    }

    /// <summary>
    /// Percent-encodes a status message per the gRPC spec (ASCII outside 0x20..0x7E and <c>%</c> itself
    /// are escaped as UTF-8 bytes). Over-escaping is allowed and lossless, so the standard escaper is
    /// used rather than a hand-rolled table.
    /// </summary>
    private static string PercentEncode(string message) => Uri.EscapeDataString(message);

    // ── deadlines ────────────────────────────────────────────

    /// <summary>
    /// Parses the caller's <c>grpc-timeout</c> (<c>&lt;digits&gt;&lt;unit&gt;</c>, units
    /// H/M/S/m/u/n). Returns <c>null</c> when absent or unparsable — an unreadable deadline must never
    /// fail the call, it just means we do not enforce one.
    /// </summary>
    public static TimeSpan? ParseTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value!.Length < 2) return null;

        var unit = value[^1];
        if (!long.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var amount) || amount < 0)
            return null;

        try
        {
            return unit switch
            {
                'H' => TimeSpan.FromHours(amount),
                'M' => TimeSpan.FromMinutes(amount),
                'S' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMilliseconds(amount),
                // `checked`: the multiply is on a caller-supplied long, and wrapping it produced a
                // NEGATIVE tick count — a deadline that either cancels the call at once or makes
                // CancelAfter throw. Overflow here means "unreadable", which is the null case below.
                'u' => TimeSpan.FromTicks(checked(amount * (TimeSpan.TicksPerMillisecond / 1000))),
                'n' => TimeSpan.FromTicks(Math.Max(1, amount / 100)),
                _ => null,
            };
        }
        // Both, because the two failure modes look nothing alike from here: the multiply above raises
        // OverflowException, while TimeSpan.FromHours and friends raise ArgumentOutOfRangeException for
        // an amount they cannot represent. Catching only the first let a crafted `9223372036854775807H`
        // escape this method entirely — and the consumer parses the deadline BEFORE its own try-block,
        // so the request died at the host instead of answering a status.
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // ── status mapping ───────────────────────────────────────

    /// <summary>
    /// Maps the transport-neutral <c>status.code</c> that controllers and processors write (an
    /// HTTP-shaped int — see <c>redb.Route.Controllers</c>) onto a gRPC status. Keeps a gRPC route
    /// behaving like the HTTP one without every processor learning a second vocabulary.
    /// </summary>
    public static StatusCode FromHttpStatus(int httpStatus) => httpStatus switch
    {
        >= 200 and <= 299 => StatusCode.OK,
        400 or 422 => StatusCode.InvalidArgument,
        401 => StatusCode.Unauthenticated,
        403 => StatusCode.PermissionDenied,
        404 => StatusCode.NotFound,
        405 or 501 => StatusCode.Unimplemented,
        409 => StatusCode.AlreadyExists,
        412 or 428 => StatusCode.FailedPrecondition,
        429 => StatusCode.ResourceExhausted,
        499 => StatusCode.Cancelled,
        503 => StatusCode.Unavailable,
        504 => StatusCode.DeadlineExceeded,
        >= 500 => StatusCode.Internal,
        _ => StatusCode.Unknown,
    };

    /// <summary>
    /// Maps an unhandled exception onto a status. <see cref="RpcException"/> keeps its own status so a
    /// processor can be explicit; cancellation splits into deadline vs. caller-abort.
    /// </summary>
    public static (StatusCode Status, string Detail) FromException(Exception ex, bool deadlineExceeded) => ex switch
    {
        RpcException rpc => (rpc.StatusCode, rpc.Status.Detail),
        GrpcProtocolException gpe => (gpe.Status, gpe.Message),
        OperationCanceledException when deadlineExceeded => (StatusCode.DeadlineExceeded, "Deadline exceeded."),
        OperationCanceledException => (StatusCode.Cancelled, "Call cancelled."),
        _ => (StatusCode.Internal, ex.Message),
    };

    // ── header hygiene (G3) ──────────────────────────────────

    /// <summary>
    /// Prefixes owned by transports. A client must never be able to forge these: processors upstream
    /// trust <c>redbHttp.RemoteAddress</c> for rate-limiting, <c>redbGrpc.*</c> for call metadata, and
    /// so on. Inbound keys carrying one of these prefixes are dropped before the exchange sees them.
    /// </summary>
    private static readonly string[] ReservedPrefixes =
    [
        "redbGrpc.", "redbHttp.", "redbSoap.", "redbSignalR.", "redbMail.", "redbAs2.",
    ];

    /// <summary>True when a caller-supplied header key belongs to a transport and must be dropped.</summary>
    public static bool IsReservedKey(string key)
    {
        foreach (var prefix in ReservedPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Formats a connection endpoint the way gRPC names peers (<c>ipv4:10.0.0.5:51234</c>,
    /// <c>ipv6:[::1]:5</c>). Kept because <c>redbGrpc.RemotePeer</c> has carried that shape since the
    /// connector's first release; the bare address lives in <c>redbGrpc.RemoteIp</c>.
    /// </summary>
    public static string FormatPeer(IPAddress address, int port)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6)
            return $"ipv6:[{address}]:{port}";

        var v4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return $"ipv4:{v4}:{port}";
    }

    /// <summary>
    /// Encodes a reply body for the wire. Only bytes and strings are payloads.
    /// <para>
    /// Anything else is refused rather than run through <c>ToString()</c>: a route that answers with, say,
    /// a dictionary would otherwise put the literal text
    /// <c>System.Collections.Generic.Dictionary`2[…]</c> on the wire and the caller would receive garbage
    /// with an OK status. That silent-stringification failure is exactly the one the SOAP consumer shipped
    /// with <c>byte[]</c>; a loud Internal naming the type is strictly better than plausible nonsense.
    /// </para>
    /// </summary>
    public static byte[] ToBytes(object? body) => body switch
    {
        null => [],
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        _ => throw new GrpcProtocolException(StatusCode.Internal,
            $"Route replied with {body.GetType().FullName}, which is not a gRPC payload. " +
            "Encode it to bytes or a string before the reply leaves the route."),
    };
}

/// <summary>
/// A violation of the gRPC wire contract by the caller (bad framing, unsupported compression,
/// oversized message). Carries the status the caller should see.
/// </summary>
internal sealed class GrpcProtocolException(StatusCode status, string message) : Exception(message)
{
    /// <summary>Status reported to the caller in the <c>grpc-status</c> trailer.</summary>
    public StatusCode Status { get; } = status;
}
