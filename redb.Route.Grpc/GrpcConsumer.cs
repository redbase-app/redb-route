using System.Net;
using Google.Protobuf;
using Grpc.Core;
using GrpcCore = global::Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc.Proto;

namespace redb.Route.Grpc;

/// <summary>
/// gRPC consumer. Embedded Kestrel-based gRPC server that receives generic
/// RedbMessage requests and dispatches them into the route processor pipeline.
/// </summary>
public class GrpcConsumer : IConsumer
{
    private readonly GrpcEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly GrpcEndpointOptions _options;
    private ILogger? _logger;
    private WebApplication? _app;
    private long _processedCount;

    /// <summary>Creates a gRPC consumer.</summary>
    public GrpcConsumer(GrpcEndpoint endpoint, IProcessor processor, GrpcEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <summary>Number of requests successfully processed.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processedCount);

    /// <summary>The base URL the gRPC server is listening on. Available after Start().</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>The processor, exposed for the service implementation.</summary>
    internal IProcessor Processor => _processor;

    /// <summary>The endpoint options, exposed for the service implementation.</summary>
    internal GrpcEndpointOptions EndpointOptions => _options;

    /// <summary>Logger exposed for the service implementation.</summary>
    internal ILogger? ConsumerLogger => _logger;

    /// <summary>The service scope factory from the endpoint, for creating scoped exchanges.</summary>
    internal IServiceScopeFactory? ScopeFactory => _endpoint.ScopeFactory;

    /// <summary>Increments the processed counter.</summary>
    internal void IncrementProcessed() => Interlocked.Increment(ref _processedCount);

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Register gRPC services
        builder.Services.AddGrpc(grpcOptions =>
        {
            if (_options.MaxRequestMessageSize > 0)
                grpcOptions.MaxReceiveMessageSize = _options.MaxRequestMessageSize;
            else
                grpcOptions.MaxReceiveMessageSize = null;
        });

        // Register this consumer as a singleton so the service can access it
        builder.Services.AddSingleton(this);

        // Configure Kestrel for HTTP/2 (required for gRPC)
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var host = _options.Host;
            var port = _options.Port;

            if (_options.Ssl && !string.IsNullOrEmpty(_options.SslCertPath))
            {
                kestrel.Listen(IPAddress.Parse(host), port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                    listenOptions.UseHttps(_options.SslCertPath, _options.SslCertPassword);
                });
            }
            else
            {
                kestrel.Listen(IPAddress.Parse(host), port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
            }
        });

        builder.Logging.ClearProviders();

        _app = builder.Build();

        // Map the gRPC service
        _app.MapGrpcService<RedbServiceImpl>();

        await _app.StartAsync(ct).ConfigureAwait(false);

        var scheme = _options.Ssl ? "https" : "http";
        var displayHost = _options.Host == "0.0.0.0" ? "localhost" : _options.Host;
        BaseUrl = $"{scheme}://{displayHost}:{_options.Port}";

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("gRPC consumer started: {Host}:{Port}", _options.Host, _options.Port);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            await _app.StopAsync(ct).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("gRPC consumer stopped: {Host}:{Port}", _options.Host, _options.Port);
    }
}

/// <summary>
/// gRPC service implementation that bridges to the redb.Route pipeline.
/// </summary>
internal sealed class RedbServiceImpl : RedbService.RedbServiceBase
{
    private readonly GrpcConsumer _consumer;

    public RedbServiceImpl(GrpcConsumer consumer)
    {
        _consumer = consumer;
    }

    public override async Task<RedbMessage> Process(RedbMessage request, ServerCallContext context)
    {
        var exchange = BuildExchange(request, context);
        exchange.Pattern = _consumer.EndpointOptions.InOut
            ? ExchangePattern.InOut
            : ExchangePattern.InOnly;

        try
        {
            try
            {
                await _consumer.Processor.Process(exchange, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _consumer.ConsumerLogger?.LogError(ex, "gRPC request processing failed: method={Method}, peer={Peer}",
                    context.Method, context.Peer);
                exchange.Exception ??= ex;
            }

            if (exchange.Exception is not null && !exchange.ExceptionHandled)
            {
                throw new RpcException(new Status(
                    global::Grpc.Core.StatusCode.Internal,
                    exchange.Exception.Message));
            }

            _consumer.IncrementProcessed();
            return BuildResponse(exchange);
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override async Task ProcessStream(RedbMessage request, IServerStreamWriter<RedbMessage> responseStream, ServerCallContext context)
    {
        var exchange = BuildExchange(request, context);
        exchange.Pattern = ExchangePattern.InOut;

        try
        {
            try
            {
                await _consumer.Processor.Process(exchange, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _consumer.ConsumerLogger?.LogError(ex, "gRPC stream processing failed: method={Method}, peer={Peer}",
                    context.Method, context.Peer);
                exchange.Exception ??= ex;
            }

            if (exchange.Exception is not null && !exchange.ExceptionHandled)
            {
                throw new RpcException(new Status(
                    global::Grpc.Core.StatusCode.Internal,
                    exchange.Exception.Message));
            }

            // Write the response as a single streamed message
            var response = BuildResponse(exchange);
            await responseStream.WriteAsync(response, context.CancellationToken).ConfigureAwait(false);

            _consumer.IncrementProcessed();
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Exchange BuildExchange(RedbMessage request, ServerCallContext context)
    {
        // Body = raw bytes from payload
        byte[]? body = request.Payload.IsEmpty ? null : request.Payload.ToByteArray();
        var message = new Message(body) { ContentType = "application/grpc+proto" };

        // Map proto headers to exchange headers
        foreach (var (key, value) in request.Headers)
        {
            message.Headers[key] = value;
        }

        // Set gRPC-specific headers
        message.Headers[GrpcHeaders.Method] = context.Method;
        message.Headers[GrpcHeaders.Port] = _consumer.EndpointOptions.Port;

        if (context.Peer is not null)
            message.Headers[GrpcHeaders.RemotePeer] = context.Peer;

        if (context.Host is not null)
            message.Headers[GrpcHeaders.Authority] = context.Host;

        if (context.Deadline != DateTime.MaxValue)
            message.Headers[GrpcHeaders.Deadline] = context.Deadline.ToString("O");

        // Copy gRPC request metadata to exchange headers
        foreach (var entry in context.RequestHeaders)
        {
            if (entry.IsBinary) continue; // skip binary metadata
            if (entry.Key.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase)) continue;
            message.Headers[entry.Key] = entry.Value;
        }

        return Exchange.Create(message, _consumer.ScopeFactory);
    }

    private static RedbMessage BuildResponse(IExchange exchange)
    {
        var response = new RedbMessage();

        // Get output message (Out if InOut, else In)
        var outMsg = exchange.HasOut ? exchange.Out! : exchange.In;

        if (outMsg.Body is not null)
        {
            response.Payload = outMsg.Body switch
            {
                byte[] bytes => ByteString.CopyFrom(bytes),
                string str => ByteString.CopyFromUtf8(str),
                _ => ByteString.CopyFromUtf8(outMsg.Body.ToString() ?? string.Empty)
            };
        }

        // Copy non-redb headers to response
        foreach (var (key, value) in outMsg.Headers)
        {
            if (value is null) continue;
            if (GrpcHeaders.IsRedbHeader(key)) continue;
            response.Headers[key] = value.ToString() ?? string.Empty;
        }

        return response;
    }
}
