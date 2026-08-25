using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc.Proto;
using redb.Route.Telemetry;

namespace redb.Route.Grpc;

/// <summary>
/// gRPC producer (client). Calls the method address carried by the endpoint URI — by default the
/// built-in generic <c>RedbService/Process</c>, or any typed <c>/package.Service/Method</c>.
/// <para>
/// Messages are marshalled as raw bytes, so the producer can call a typed <c>.proto</c> service whose
/// stubs it has never seen: in envelope mode the body is wrapped in a <c>RedbMessage</c>, in raw mode
/// the exchange body goes on the wire as-is.
/// </para>
/// </summary>
public class GrpcProducer : ConnectableProducer
{
    private static readonly Marshaller<byte[]> ByteMarshaller = Marshallers.Create(b => b, b => b);

    private readonly GrpcEndpoint _endpoint;
    private readonly GrpcEndpointOptions _options;
    private GrpcChannel? _channel;
    private CallInvoker? _invoker;
    private Method<byte[], byte[]>? _method;

    /// <summary>Creates a gRPC producer.</summary>
    public GrpcProducer(GrpcEndpoint endpoint, GrpcEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"grpc:{_endpoint.BuildProducerAddress()}{_options.MethodPath}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct)
    {
        var address = _endpoint.BuildProducerAddress();

        var channelOptions = new GrpcChannelOptions();

        if (_options.MaxSendMessageSize > 0)
            channelOptions.MaxSendMessageSize = _options.MaxSendMessageSize;

        if (_options.MaxReceiveMessageSize > 0)
            channelOptions.MaxReceiveMessageSize = _options.MaxReceiveMessageSize;

        // mTLS: present our certificate to the server.
        if (!string.IsNullOrEmpty(_options.ClientCertPath))
        {
            var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
            handler.SslOptions.ClientCertificates = [LoadClientCertificate()];
            channelOptions.HttpHandler = handler;
        }

        _channel = GrpcChannel.ForAddress(address, channelOptions);
        _invoker = _channel.CreateCallInvoker();

        var (service, method) = SplitMethod(_options.MethodPath);
        _method = new Method<byte[], byte[]>(
            _options.Streaming ? MethodType.ServerStreaming : MethodType.Unary,
            service, method, ByteMarshaller, ByteMarshaller);

        return Task.CompletedTask;
    }

    private X509Certificate2 LoadClientCertificate()
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12FromFile(_options.ClientCertPath!, _options.ClientCertPassword);
#else
        return new X509Certificate2(_options.ClientCertPath!, _options.ClientCertPassword);
#endif
    }

    /// <summary>
    /// Whether a header name can travel as gRPC metadata: lowercase alphanumerics, underscore, hyphen and
    /// dot, and not the <c>-bin</c> suffix, which the protocol reserves for byte-valued entries. Mirrors
    /// what <see cref="Metadata"/> validates, so the check here and the throw there cannot disagree.
    /// </summary>
    private static bool IsSendableMetadataKey(string key)
    {
        if (key.EndsWith("-bin", StringComparison.Ordinal)) return false;

        foreach (var c in key)
        {
            var allowed = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.';
            if (!allowed) return false;
        }

        return true;
    }

    private static (string Service, string Method) SplitMethod(string methodPath)
    {
        var trimmed = methodPath.TrimStart('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? (trimmed, string.Empty) : (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    /// <inheritdoc />
    protected override Task DisconnectAsync(CancellationToken ct)
    {
        if (_channel is not null)
        {
            _channel.Dispose();
            _channel = null;
            _invoker = null;
            _method = null;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            "grpc.invoke", ActivityKind.Client,
            "rpc.system", "grpc",
            _endpoint.Uri.NormalizedKey);

        var request = BuildRequest(exchange);

        var callOptions = new CallOptions(cancellationToken: ct);

        // Deadline: expression > static
        var deadline = _options.Deadline;
        if (_options.DeadlineExpression is not null)
        {
            var resolved = _options.ResolveOption(_options.DeadlineExpression, exchange);
            if (resolved is not null && int.TryParse(resolved, out var parsedDeadline))
                deadline = parsedDeadline;
        }

        if (deadline > 0)
            callOptions = callOptions.WithDeadline(DateTime.UtcNow.AddMilliseconds(deadline));

        // Add exchange headers as gRPC metadata
        var metadata = new Metadata();
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (value is null) continue;
            if (GrpcHeaders.IsRedbHeader(key)) continue;
            // gRPC metadata keys must be lowercase ASCII
            var metaKey = key.ToLowerInvariant();
            if (metaKey.StartsWith("grpc-")) continue; // reserved

            // Exchange headers are whatever upstream put there: copied HTTP request headers, keys a
            // processor derived from data. gRPC's alphabet is far narrower, and Metadata.Add throws on
            // anything outside it — including a perfectly legal HTTP name like `trace-bin`, where the
            // suffix promises a binary value. This loop runs before the try below, so one such header
            // took the whole call down with an ArgumentException that carried no status and never
            // reached .OnException / retry / dead-letter. Losing a header the wire cannot express is
            // recoverable; losing the call is not.
            if (!IsSendableMetadataKey(metaKey))
            {
                Logger?.LogDebug(
                    "gRPC: header '{Key}' is not a valid metadata key and was not sent with the call", key);
                continue;
            }

            metadata.Add(metaKey, value.ToString() ?? string.Empty);
        }

        // Grpc.Net.Client compresses a request when this metadata entry is present; the server then reads
        // grpc-encoding off the wire. We only ask for it when the endpoint opted in.
        if (_options.Compression == GrpcCompression.Gzip)
            metadata.Add("grpc-internal-encoding-request", "gzip");

        if (metadata.Count > 0)
            callOptions = callOptions.WithHeaders(metadata);

        try
        {
            if (_options.Streaming)
            {
                // Server-streaming call: the reply is an IAsyncEnumerable, the framework's streaming
                // shape. It flows straight into a streaming consumer (the HTTP one turns it into SSE or
                // chunked output) without ever being buffered here.
                var streamingCall = _invoker!.AsyncServerStreamingCall(_method!, host: null, callOptions, request);
                exchange.Out = new Message(ReadStream(streamingCall, ct));
                exchange.Out.Headers[GrpcHeaders.StatusCode] = (int)StatusCode.OK;
                return;
            }

            using var call = _invoker!.AsyncUnaryCall(_method!, host: null, callOptions, request);
            var response = await call.ResponseAsync.ConfigureAwait(false);

            exchange.Out = BuildOutMessage(response);
            exchange.Out.Headers[GrpcHeaders.StatusCode] = (int)StatusCode.OK;
        }
        catch (RpcException rpcEx)
        {
            Logger?.LogError(rpcEx, "gRPC call failed: endpoint={Endpoint}{Method}, status={StatusCode}, detail={Detail}",
                _endpoint.BuildProducerAddress(), _options.MethodPath, rpcEx.StatusCode, rpcEx.Status.Detail);

            exchange.In.Headers[GrpcHeaders.StatusCode] = (int)rpcEx.StatusCode;
            exchange.In.Headers[GrpcHeaders.StatusDetail] = rpcEx.Status.Detail;
            exchange.Exception = rpcEx;

            // Same contract as the HTTP and SOAP producers: a failed call must reach .OnException(...),
            // retry and dead-letter. Without the throw the pipeline simply carried on with an empty Out
            // and the failure was visible only in metrics.
            if (_options.ThrowOnError)
                throw;
        }
    }

    /// <summary>
    /// Lazily yields the messages of a server-streaming call, one element per frame, and disposes the
    /// call when the consumer stops reading. Envelope mode unwraps each frame's payload; raw mode yields
    /// the bytes.
    /// </summary>
    private async IAsyncEnumerable<object?> ReadStream(
        AsyncServerStreamingCall<byte[]> call,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using (call)
        {
            while (true)
            {
                bool advanced;
                try
                {
                    advanced = await call.ResponseStream.MoveNext(ct).ConfigureAwait(false);
                }
                catch (RpcException ex)
                {
                    // A server-streaming call reports failure while the consumer is enumerating — long
                    // after Process returned. So .OnException, retry and dead-letter cannot see it, and
                    // that boundary is inherent to lazy streaming rather than something to paper over.
                    // What is not inherent is the failure being invisible: without this, a stream that
                    // broke every time looked exactly like a stream that ended early. Record it, then
                    // rethrow — the reader still has to learn the stream did not finish.
                    _endpoint.RecordError(ex);
                    Logger?.LogError(ex,
                        "gRPC server stream failed: endpoint={Endpoint}{Method}, status={StatusCode}, detail={Detail}",
                        _endpoint.BuildProducerAddress(), _options.MethodPath, ex.StatusCode, ex.Status.Detail);
                    throw;
                }

                if (!advanced) break;

                var frame = call.ResponseStream.Current;
                yield return _options.UseEnvelope
                    ? RedbMessage.Parser.ParseFrom(frame).Payload.ToByteArray()
                    : frame;
            }
        }
    }

    /// <summary>Turns the reply bytes into the exchange's Out message.</summary>
    private Message BuildOutMessage(byte[] response)
    {
        if (!_options.UseEnvelope)
            return new Message(response.Length == 0 ? null : response);

        var envelope = RedbMessage.Parser.ParseFrom(response);
        var outMessage = new Message(envelope.Payload.IsEmpty ? null : envelope.Payload.ToByteArray());

        foreach (var (key, value) in envelope.Headers)
            outMessage.Headers[key] = value;

        if (envelope.Headers.TryGetValue("Content-Type", out var contentType))
            outMessage.ContentType = contentType;

        return outMessage;
    }

    /// <summary>
    /// Builds the request bytes. Envelope mode wraps body and headers in a <c>RedbMessage</c>; raw mode
    /// sends the body untouched, which is what a typed service on the far side expects.
    /// </summary>
    private byte[] BuildRequest(IExchange exchange)
    {
        if (!_options.UseEnvelope)
            return GrpcWire.ToBytes(exchange.In.Body);

        var request = new RedbMessage();

        if (exchange.In.Body is not null)
        {
            request.Payload = exchange.In.Body switch
            {
                byte[] bytes => ByteString.CopyFrom(bytes),
                string str => ByteString.CopyFromUtf8(str),
                _ => ByteString.CopyFromUtf8(exchange.In.Body.ToString() ?? string.Empty)
            };
        }

        // Copy non-redb headers to proto headers
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (value is null) continue;
            if (GrpcHeaders.IsRedbHeader(key)) continue;
            request.Headers[key] = value.ToString() ?? string.Empty;
        }

        // Propagate ContentType as a proto header if set
        if (!string.IsNullOrEmpty(exchange.In.ContentType))
            request.Headers["Content-Type"] = exchange.In.ContentType;

        return request.ToByteArray();
    }
}
