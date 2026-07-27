using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tcp;

/// <summary>
/// TCP component. Scheme: "tcp".
/// <para>Producer: TCP client — connects to remote host and sends data.</para>
/// <para>Consumer: TCP server — listens on a port, accepts connections, receives data.</para>
/// <para>URI format: tcp:host:port?textLine=true&amp;delimiter=\n&amp;inOut=true</para>
/// </summary>
public class TcpComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "tcp";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new TcpEndpointOptions();

        // Parse host:port from URI path
        ParseHostPort(uri.Path, options);

        options.BindFromUri(uri.RawParameters);

        // Named ConnectionFactory keeps the TLS certificate password out of the route URI.
        if (!string.IsNullOrEmpty(options.ConnectionFactory) && Context is not null)
        {
            var factory = Context.GetFromRegistry<TcpConnectionFactory>(options.ConnectionFactory);
            if (factory is not null)
                factory.ApplyTo(options, uri);
            else
                Logger?.LogWarning(
                    "TCP: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                    options.ConnectionFactory);
        }

        options.Validate();

        return new TcpEndpoint(uri, this, options);
    }

    /// <summary>
    /// Parses "host:port" from the URI path segment.
    /// Path format: "/host:port" or "host:port".
    /// </summary>
    internal static void ParseHostPort(string path, TcpEndpointOptions options)
    {
        var clean = path.TrimStart('/');
        var colonIdx = clean.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(clean[(colonIdx + 1)..], out var port))
        {
            options.Host = clean[..colonIdx];
            options.Port = port;
        }
        else
        {
            options.Host = clean;
        }
    }
}

/// <summary>
/// TCP endpoint. Creates either a producer (TCP client) or consumer (TCP server).
/// </summary>
public class TcpEndpoint : EndpointBase<TcpEndpointOptions>
{
    /// <summary>Creates a TCP endpoint.</summary>
    public TcpEndpoint(EndpointUri uri, TcpComponent component, TcpEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The endpoint options for external access.</summary>
    internal TcpEndpointOptions EndpointOptions => Options;

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new TcpProducer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new TcpConsumer(this, processor, Options);
    }
}
