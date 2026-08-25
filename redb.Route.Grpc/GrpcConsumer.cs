
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc.Proto;
using redb.Route.Http;
using redb.Route.Telemetry;
using KestrelClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode;
using IMessage = redb.Route.Abstractions.IMessage;
using HttpProtocol = redb.Route.Http.HttpProtocol;

namespace redb.Route.Grpc;

/// <summary>
/// gRPC consumer. Registers the endpoint's method address as a path route on the shared Kestrel host
/// and speaks the gRPC wire protocol itself (see <see cref="GrpcWire"/>) — the same shape as the AS2
/// and SOAP consumers, which also own their protocol on top of that host.
/// <para>
/// Because the route key is the method address, many gRPC methods live on one port as ordinary routes:
/// <c>From("grpc:0.0.0.0:5001/identity.v1.Identity/Token")</c> and
/// <c>From("grpc:0.0.0.0:5001/identity.v1.Identity/Introspect")</c> are two routes with their own ids,
/// policies, metrics and lifecycle.
/// </para>
/// </summary>
public class GrpcConsumer : IConsumer
{
    private const string HealthCheckPath = "/grpc.health.v1.Health/Check";

    private readonly GrpcEndpoint _endpoint;
    private readonly IProcessor _processor;
    private readonly GrpcEndpointOptions _options;
    private ILogger? _logger;
    private RouteRegistration? _registration;
    private RouteRegistration? _streamRegistration;
    private RouteRegistration? _healthRegistration;
    private long _processedCount;

    /// <summary>Creates a gRPC consumer.</summary>
    public GrpcConsumer(GrpcEndpoint endpoint, IProcessor processor, GrpcEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc />
    public IEndpoint Endpoint => _endpoint;

    /// <summary>Number of requests successfully processed.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>The base URL the gRPC server is listening on. Available after Start().</summary>
    public string? BaseUrl { get; private set; }

    private SharedHttpServerManager Server => _endpoint.Server;

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        var host = _options.Host;
        var port = _options.Port;

        _registration = RegisterRoute(_options.MethodPath, http => HandleRequest(http, _options.Streaming));

        // A URI with no method address ("grpc:host:port") is the generic service, and that service has
        // always answered both its unary and its streaming method. Keep serving both so those endpoints
        // behave exactly as they did before the address became the route key.
        if (_options.DefaultMethodAddress)
            _streamRegistration = RegisterRoute(GrpcEndpointOptions.DefaultStreamMethodPath,
                http => HandleRequest(http, streaming: true));

        // Health is a plain unary method, so it is simply another path route on the same host:port.
        if (_options.Health)
            _healthRegistration = RegisterRoute(HealthCheckPath, HandleHealthCheck);

        await Server.EnsureStarted(host, port, ct).ConfigureAwait(false);

        BaseUrl = Server.GetBaseUrl(host, port);

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("gRPC consumer started: {Host}:{Port}{Method}", host, port, _options.MethodPath);
    }

    private RouteRegistration RegisterRoute(string path, Func<HttpContext, Task> handler) =>
        Server.RegisterRoute(
            _options.Host,
            _options.Port,
            path,
            "POST",                                   // gRPC is POST-only
            handler,
            _options.Ssl,
            _options.SslCertPath,
            _options.SslCertPassword,
            corsOptions: null,                        // gRPC is not a browser transport
            maxRequestBodySize: 0,                    // message size is enforced per frame, not per body
            protocol: HttpProtocol.Http2,
            clientCertificateMode: MapClientCertificateMode(_options.ClientCertificateMode),
            clientCertificateValidation: BuildThumbprintValidator());

    private static KestrelClientCertificateMode? MapClientCertificateMode(GrpcClientCertificateMode mode) => mode switch
    {
        GrpcClientCertificateMode.AllowCertificate => KestrelClientCertificateMode.AllowCertificate,
        GrpcClientCertificateMode.RequireCertificate => KestrelClientCertificateMode.RequireCertificate,
        _ => null,
    };

    /// <summary>
    /// Builds the allow-list check for client certificates. Chain validation is Kestrel's job; this adds
    /// "and it must be one of ours", which is what pins a partner in a service-to-service contour.
    /// </summary>
    private Func<System.Security.Cryptography.X509Certificates.X509Certificate2,
        System.Security.Cryptography.X509Certificates.X509Chain?,
        System.Net.Security.SslPolicyErrors, bool>? BuildThumbprintValidator()
    {
        if (string.IsNullOrWhiteSpace(_options.AllowedClientThumbprints)) return null;

        var allowed = _options.AllowedClientThumbprints!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace(" ", string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (cert, _, _) => allowed.Contains(cert.Thumbprint);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_registration is not null)
        {
            Server.UnregisterRoute(_registration);
            _registration = null;
        }
        if (_streamRegistration is not null)
        {
            Server.UnregisterRoute(_streamRegistration);
            _streamRegistration = null;
        }
        if (_healthRegistration is not null)
        {
            Server.UnregisterRoute(_healthRegistration);
            _healthRegistration = null;
        }

        await Server.StopIfEmpty(_options.Host, _options.Port, ct).ConfigureAwait(false);

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("gRPC consumer stopped: {Host}:{Port}{Method}",
            _options.Host, _options.Port, _options.MethodPath);
    }

    // ── request handling ─────────────────────────────────────

    private async Task HandleRequest(HttpContext http, bool streaming)
    {
        using var span = RouteTelemetryExtensions.StartTransportSpan(
            "grpc receive", ActivityKind.Server, "rpc.system", "grpc", _endpoint.Uri.NormalizedKey);
        _endpoint.RecordMessageIn();

        // Content type must be on the response before anything is written, otherwise a client sees a
        // non-gRPC reply and reports a protocol error instead of our status.
        http.Response.ContentType = GrpcWire.ContentType;

        // Codec negotiation. We always advertise what we can decode; we only compress the reply when the
        // route asked for it AND the caller said it can inflate it.
        var requestEncoding = http.Request.Headers[GrpcWire.EncodingHeader].ToString();
        var compressReply = _options.Compression == GrpcCompression.Gzip
                            && GrpcWire.IsGzip(http.Request.Headers[GrpcWire.AcceptEncodingHeader].ToString());

        http.Response.Headers[GrpcWire.AcceptEncodingHeader] = GrpcWire.SupportedEncodings;
        if (compressReply)
            http.Response.Headers[GrpcWire.EncodingHeader] = "gzip";

        var timeout = GrpcWire.ParseTimeout(http.Request.Headers[GrpcWire.TimeoutHeader]);
        using var deadline = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline?.CancelAfter(timeout!.Value);
        var ct = deadline?.Token ?? http.RequestAborted;

        var status = StatusCode.OK;
        string? detail = null;
        IExchange? exchange = null;
        IMessage? outMessage = null;

        try
        {
            if (!GrpcWire.IsGrpcContentType(http.Request.ContentType))
                throw new GrpcProtocolException(StatusCode.Internal,
                    $"Expected content-type {GrpcWire.ContentType}, got '{http.Request.ContentType}'.");

            var payload = await GrpcWire.ReadMessageAsync(http.Request.Body, _options.MaxRequestMessageSize, requestEncoding, ct)
                              .ConfigureAwait(false)
                          ?? throw new GrpcProtocolException(StatusCode.Internal, "Request carried no message.");

            _endpoint.RecordBytesIn(payload.Length);

            exchange = BuildExchange(http, payload);
            exchange.Pattern = _options.InOut ? ExchangePattern.InOut : ExchangePattern.InOnly;

            await _processor.Process(exchange, ct).ConfigureAwait(false);

            if (exchange.Exception is not null && !exchange.ExceptionHandled)
                throw exchange.Exception;

            // Identity's convention: a processor may answer on Out or write back into In. Same fallback
            // the SOAP consumer uses. InOut only selects the exchange pattern the route sees — the reply
            // is written either way, which is how this endpoint has always behaved.
            outMessage = exchange.HasOut ? exchange.Out! : exchange.In;
            (status, detail) = ResolveStatus(outMessage);

            // A non-OK status is delivered trailers-only: gRPC clients discard the payload of a failed
            // call, so writing it would just be bytes nobody reads. Detail travels in grpc-message and
            // any redbGrpc.Trailer.* headers.
            if (status == StatusCode.OK)
                await WriteReply(http, outMessage, streaming, compressReply, ct).ConfigureAwait(false);

            Interlocked.Increment(ref _processedCount);
        }
        catch (Exception ex)
        {
            var deadlineHit = deadline is { IsCancellationRequested: true }
                              && !http.RequestAborted.IsCancellationRequested;
            (status, detail) = GrpcWire.FromException(ex, deadlineHit);

            _endpoint.RecordError(ex);
            _logger?.LogError(ex, "gRPC request failed: method={Method} peer={Peer} status={Status}",
                _options.MethodPath, http.Connection.RemoteIpAddress, status);
        }
        finally
        {
            if (exchange is not null)
                await exchange.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            if (outMessage is not null)
                AppendTrailers(http.Response, outMessage);
            GrpcWire.WriteStatus(http.Response, status, detail);
        }
        catch (Exception ex)
        {
            // The caller went away mid-reply; nothing left to report to.
            _logger?.LogDebug(ex, "gRPC: could not write status trailers (client gone)");
        }
    }

    /// <summary>
    /// Writes the reply. A route may answer with one message or, on a streaming address, with many:
    /// any <c>IEnumerable</c> / <c>IAsyncEnumerable</c> body that is not itself a payload
    /// (<c>string</c>, <c>byte[]</c>) is written element by element as its own frame.
    /// </summary>
    private async Task WriteReply(HttpContext http, IMessage outMessage, bool streaming, bool compress, CancellationToken ct)
    {
        if (!streaming && TryGetStreamItems(outMessage.Body, out _))
        {
            // A unary method carries exactly one message, so a stream body here cannot be delivered.
            // Without this guard it would fall through to ToBytes() and the caller would receive the
            // enumerable's type name as the payload — the same silent-garbage failure the SOAP consumer
            // had with byte[]. Fail loudly and say what to change.
            throw new GrpcProtocolException(StatusCode.Internal,
                $"Route replied with a stream ({outMessage.Body!.GetType().Name}) on the unary method " +
                $"'{_options.MethodPath}'. Declare the address as server-streaming (.Streaming()) or " +
                "materialize the body into a single message.");
        }

        if (streaming && TryGetStreamItems(outMessage.Body, out var items))
        {
            await foreach (var item in items!.WithCancellation(ct).ConfigureAwait(false))
            {
                var frame = Encode(item, outMessage);
                await GrpcWire.WriteMessageAsync(http.Response, frame, compress, ct).ConfigureAwait(false);
                _endpoint.RecordMessageOut();
            }
            return;
        }

        var single = Encode(outMessage.Body, outMessage);
        await GrpcWire.WriteMessageAsync(http.Response, single, compress, ct).ConfigureAwait(false);
        _endpoint.RecordMessageOut();
    }

    /// <summary>
    /// Recognises a multi-message body. The signal is the framework's own streaming shape — an
    /// <c>IAsyncEnumerable</c>, one yield per message — the same contract the HTTP consumer honours for
    /// chunked/SSE replies and the one <c>StreamingSplitterProcessor</c> produces. A <c>List</c> is
    /// deliberately not a stream: a collection body stays one message, as it is for every other transport.
    /// </summary>
    private static bool TryGetStreamItems(object? body, out IAsyncEnumerable<object?>? items)
    {
        // IAsyncEnumerable<T> is covariant, so this one pattern also catches IAsyncEnumerable<string>
        // and IAsyncEnumerable<byte[]> — the shapes routes actually yield.
        items = body as IAsyncEnumerable<object?>;
        return items is not null;
    }

    /// <summary>
    /// Encodes one reply item. In envelope mode it becomes a <c>RedbMessage</c> carrying the body plus
    /// the message headers; in raw mode the bytes go on the wire untouched, which is what a typed
    /// <c>.proto</c> client expects.
    /// </summary>
    private byte[] Encode(object? body, IMessage outMessage)
    {
        if (!_options.UseEnvelope)
            return GrpcWire.ToBytes(body);

        var reply = new RedbMessage();
        var bytes = GrpcWire.ToBytes(body);
        if (bytes.Length > 0)
            reply.Payload = ByteString.CopyFrom(bytes);

        foreach (var (key, value) in outMessage.Headers)
        {
            if (value is null) continue;
            if (GrpcHeaders.IsRedbHeader(key)) continue;
            reply.Headers[key] = value.ToString() ?? string.Empty;
        }

        return reply.ToByteArray();
    }

    /// <summary>
    /// Chooses the status the caller receives: an explicit <c>redbGrpc.StatusCode</c> wins, otherwise the
    /// transport-neutral <c>status.code</c> that controllers and processors already write is translated.
    /// </summary>
    private (StatusCode Status, string? Detail) ResolveStatus(IMessage outMessage)
    {
        var detail = outMessage.GetHeader<string>(GrpcHeaders.StatusDetail);

        if (outMessage.Headers.TryGetValue(GrpcHeaders.StatusCode, out var explicitStatus) && explicitStatus is not null)
        {
            var raw = explicitStatus.ToString();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
                && Enum.IsDefined(typeof(StatusCode), numeric))
                return ((StatusCode)numeric, detail);

            if (Enum.TryParse<StatusCode>(raw, ignoreCase: true, out var named))
                return (named, detail);
        }

        if (_options.SuppressStatusMapping)
            return (StatusCode.OK, null);

        if (outMessage.Headers.TryGetValue("status.code", out var neutral) && neutral is not null
            && int.TryParse(neutral.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var httpStatus))
        {
            var mapped = GrpcWire.FromHttpStatus(httpStatus);
            return (mapped, mapped == StatusCode.OK ? null : detail ?? $"status.code {httpStatus}");
        }

        return (StatusCode.OK, null);
    }

    /// <summary>Copies <c>redbGrpc.Trailer.*</c> headers into the response trailers.</summary>
    private static void AppendTrailers(HttpResponse response, IMessage outMessage)
    {
        foreach (var (key, value) in outMessage.Headers)
        {
            if (value is null) continue;
            if (!key.StartsWith(GrpcHeaders.TrailerPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var name = key[GrpcHeaders.TrailerPrefix.Length..];
            if (name.Length > 0)
                response.AppendTrailer(name.ToLowerInvariant(), value.ToString() ?? string.Empty);
        }
    }

    private Exchange BuildExchange(HttpContext http, byte[] payload)
    {
        object? body = payload;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (_options.UseEnvelope)
        {
            RedbMessage envelope;
            try
            {
                envelope = RedbMessage.Parser.ParseFrom(payload);
            }
            catch (InvalidProtocolBufferException ex)
            {
                // The caller sent bytes that are not a RedbMessage. Left alone this surfaced through the
                // catch-all as Internal — which tells the caller "our fault, retry" for input only they
                // can fix — and put the parser's own wording in grpc-message. Their mistake, their status.
                throw new GrpcProtocolException(StatusCode.InvalidArgument,
                    "Request is not a valid RedbMessage envelope. Send the raw message instead, or " +
                    $"address an endpoint without envelope mode. ({ex.GetType().Name})");
            }

            body = envelope.Payload.IsEmpty ? null : envelope.Payload.ToByteArray();
            foreach (var (key, value) in envelope.Headers)
                headers[key] = value;
        }

        var message = new Message(body) { ContentType = GrpcWire.ProtoContentType };

        // 1. Caller-supplied headers first — so our own metadata below cannot be overwritten by them.
        foreach (var (key, value) in headers)
        {
            if (!Accept(key)) continue;
            message.Headers[key] = value;
        }

        foreach (var entry in http.Request.Headers)
        {
            var key = entry.Key;
            if (key.StartsWith(':')) continue;                                    // HTTP/2 pseudo-headers
            if (key.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase)) continue;  // protocol plane
            if (!Accept(key)) continue;
            message.Headers[key] = entry.Value.ToString();
        }

        // 2. Transport metadata last: it is ours, and it is what upstream processors trust.
        var (service, method) = SplitMethod(_options.MethodPath);
        message.Headers[GrpcHeaders.Route] = _options.MethodPath;
        message.Headers[GrpcHeaders.Service] = service;
        message.Headers[GrpcHeaders.Method] = method;
        message.Headers[GrpcHeaders.Port] = _options.Port;

        var remote = http.Connection.RemoteIpAddress;
        if (remote is not null)
        {
            var ip = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString();
            message.Headers[GrpcHeaders.RemoteIp] = ip;
            message.Headers[GrpcHeaders.RemotePort] = http.Connection.RemotePort;
            message.Headers[GrpcHeaders.RemotePeer] = GrpcWire.FormatPeer(remote, http.Connection.RemotePort);

            // Opt-in bridge for processors written against the HTTP transport (rate limiting, lockout,
            // device metadata). Off by default: a header in someone else's namespace is a deliberate act.
            if (_options.EmitHttpCompatHeaders)
                message.Headers["redbHttp.RemoteAddress"] = ip;
        }

        if (http.Request.Host.HasValue)
            message.Headers[GrpcHeaders.Authority] = http.Request.Host.Value!;

        var timeout = GrpcWire.ParseTimeout(http.Request.Headers[GrpcWire.TimeoutHeader]);
        if (timeout is not null)
            message.Headers[GrpcHeaders.Deadline] = DateTime.UtcNow.Add(timeout.Value).ToString("O");

        var clientCert = http.Connection.ClientCertificate;
        if (clientCert is not null)
        {
            message.Headers[GrpcHeaders.ClientCertThumbprint] = clientCert.Thumbprint;
            message.Headers[GrpcHeaders.ClientCertSubject] = clientCert.Subject;
            message.Headers[GrpcHeaders.ClientCertNotAfter] = clientCert.NotAfter.ToUniversalTime().ToString("O");
        }

        return Exchange.Create(message, _endpoint.ScopeFactory);

        bool Accept(string key) => _options.AllowClientReservedHeaders || !GrpcWire.IsReservedKey(key);
    }

    private static (string Service, string Method) SplitMethod(string methodPath)
    {
        var trimmed = methodPath.TrimStart('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? (trimmed, string.Empty) : (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    // ── health ───────────────────────────────────────────────

    /// <summary>
    /// Answers <c>grpc.health.v1.Health/Check</c> with SERVING. The response message is two protobuf
    /// bytes (field 1, varint 1), so the standard health contract is met without generating the proto.
    /// </summary>
    private async Task HandleHealthCheck(HttpContext http)
    {
        http.Response.ContentType = GrpcWire.ContentType;
        try
        {
            // HealthCheckResponse { ServingStatus status = 1; } with SERVING = 1.
            await GrpcWire.WriteMessageAsync(http.Response, [0x08, 0x01], compress: false, http.RequestAborted).ConfigureAwait(false);
            GrpcWire.WriteStatus(http.Response, StatusCode.OK);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "gRPC: health check could not be answered");
        }
    }
}
