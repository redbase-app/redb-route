using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Grpc;

/// <summary>
/// gRPC component. Scheme: "grpc".
/// <para>Producer: GrpcChannel-based client for calling gRPC services.</para>
/// <para>Consumer: Kestrel-based embedded gRPC server with a generic RedbService.</para>
/// <para>URI format: grpc:host:port?deadline=30000&amp;plaintext=true</para>
/// </summary>
public class GrpcComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "grpc";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new GrpcEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // Extract host[:port] from URI path (e.g. "0.0.0.0:50051")
        ParseHostPort(uri.Path, options, uri.RawParameters);

        // Named ConnectionFactory keeps the TLS certificate password out of the route URI.
        if (!string.IsNullOrEmpty(options.ConnectionFactory) && Context is not null)
        {
            var factory = Context.GetFromRegistry<GrpcConnectionFactory>(options.ConnectionFactory);
            if (factory is not null)
                factory.ApplyTo(options, uri);
            else
                Logger?.LogWarning(
                    "gRPC: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                    options.ConnectionFactory);
        }

        options.Validate();

        return new GrpcEndpoint(uri, this, options);
    }

    /// <summary>
    /// Extracts host and port from the URI path segment (host[:port]).
    /// Only overrides options if not already set via query parameters.
    /// </summary>
    private static void ParseHostPort(string path, GrpcEndpointOptions options, IReadOnlyDictionary<string, string> rawParams)
    {
        var segment = path.TrimStart('/');
        var colonIdx = segment.LastIndexOf(':');
        if (colonIdx > 0)
        {
            var hostPart = segment[..colonIdx];
            var portPart = segment[(colonIdx + 1)..];

            if (!rawParams.ContainsKey("host") && !string.IsNullOrEmpty(hostPart))
                options.Host = hostPart;

            if (!rawParams.ContainsKey("port") && int.TryParse(portPart, out var port))
                options.Port = port;
        }
        else if (!rawParams.ContainsKey("host") && !string.IsNullOrEmpty(segment))
        {
            options.Host = segment;
        }
    }
}

/// <summary>
/// gRPC endpoint. Creates either a producer (gRPC client) or consumer (gRPC server).
/// <para>URI path format: host[:port]</para>
/// </summary>
public class GrpcEndpoint : EndpointBase<GrpcEndpointOptions>
{
    /// <summary>Creates a gRPC endpoint.</summary>
    public GrpcEndpoint(EndpointUri uri, GrpcComponent component, GrpcEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The endpoint options for external access.</summary>
    internal GrpcEndpointOptions EndpointOptions => Options;

    /// <summary>
    /// Builds the target address for the producer.
    /// URI path is "host:port" — we prepend http:// or https://.
    /// </summary>
    public string BuildProducerAddress()
    {
        var path = Uri.Path.TrimStart('/');
        var scheme = Options.Plaintext ? "http" : "https";
        return $"{scheme}://{path}";
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new GrpcProducer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new GrpcConsumer(this, processor, Options);
    }
}
