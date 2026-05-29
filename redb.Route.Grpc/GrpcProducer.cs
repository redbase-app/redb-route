using System.Diagnostics;
using Google.Protobuf;
using Grpc.Core;
using GrpcCore = global::Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc.Proto;
using redb.Route.Telemetry;

namespace redb.Route.Grpc;

/// <summary>
/// gRPC producer. Uses GrpcChannel to call gRPC services.
/// Sends the exchange body as a RedbMessage payload and maps response back.
/// </summary>
public class GrpcProducer : ConnectableProducer
{
    private readonly GrpcEndpoint _endpoint;
    private readonly GrpcEndpointOptions _options;
    private GrpcChannel? _channel;
    private RedbService.RedbServiceClient? _client;

    /// <summary>Creates a gRPC producer.</summary>
    public GrpcProducer(GrpcEndpoint endpoint, GrpcEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"grpc:{_endpoint.BuildProducerAddress()}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct)
    {
        var address = _endpoint.BuildProducerAddress();

        var channelOptions = new GrpcChannelOptions();

        if (_options.MaxSendMessageSize > 0)
            channelOptions.MaxSendMessageSize = _options.MaxSendMessageSize;

        if (_options.MaxReceiveMessageSize > 0)
            channelOptions.MaxReceiveMessageSize = _options.MaxReceiveMessageSize;

        _channel = GrpcChannel.ForAddress(address, channelOptions);
        _client = new RedbService.RedbServiceClient(_channel);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task DisconnectAsync(CancellationToken ct)
    {
        if (_channel is not null)
        {
            _channel.Dispose();
            _channel = null;
            _client = null;
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

        // Build request from exchange
        var request = BuildRequest(exchange);

        // Build call options
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
            metadata.Add(metaKey, value.ToString() ?? string.Empty);
        }

        if (metadata.Count > 0)
            callOptions = callOptions.WithHeaders(metadata);

        try
        {
            var response = await _client.ProcessAsync(request, callOptions).ConfigureAwait(false);

            // Map response to exchange
            var outMessage = new Message(
                response.Payload.IsEmpty ? null : (object)response.Payload.ToByteArray());

            // Copy response headers
            foreach (var (key, value) in response.Headers)
            {
                outMessage.Headers[key] = value;
            }

            // Restore ContentType from proto header if present
            if (response.Headers.TryGetValue("Content-Type", out var respCt))
                outMessage.ContentType = respCt;

            outMessage.Headers[GrpcHeaders.StatusCode] = (int)GrpcCore.StatusCode.OK;

            exchange.Out = outMessage;
        }
        catch (RpcException rpcEx)
        {
            Logger?.LogError(rpcEx, "gRPC call failed: endpoint={Endpoint}, status={StatusCode}, detail={Detail}",
                _endpoint.BuildProducerAddress(), rpcEx.StatusCode, rpcEx.Status.Detail);
            exchange.In.Headers[GrpcHeaders.StatusCode] = (int)rpcEx.StatusCode;
            exchange.In.Headers[GrpcHeaders.StatusDetail] = rpcEx.Status.Detail;
            exchange.Exception = rpcEx;
        }
    }

    private static RedbMessage BuildRequest(IExchange exchange)
    {
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

        return request;
    }
}
