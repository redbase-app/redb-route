using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.WebSocket;

/// <summary>
/// WebSocket component. Scheme: "ws" (plain) or "wss" (TLS).
/// <para>Producer: ClientWebSocket — connects to remote WebSocket server and sends frames.</para>
/// <para>Consumer: Kestrel-based WebSocket server — accepts connections and receives frames.</para>
/// <para>URI format: ws:host:port/path?messageType=Text&amp;subProtocol=graphql-ws</para>
/// </summary>
public class WsComponent : ComponentBase
{
    private readonly string _scheme;

    /// <summary>Creates a WebSocket component with the given scheme.</summary>
    public WsComponent(string scheme = "ws")
    {
        _scheme = scheme;
    }

    /// <inheritdoc />
    public override string Scheme => _scheme;

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new WsEndpointOptions();
        ParseHostPort(uri.Path, options);
        options.BindFromUri(uri.RawParameters);

        // wss implies SSL
        if (_scheme == "wss")
            options.Ssl = true;

        options.Validate();

        return new WsEndpoint(uri, this, options);
    }

    /// <summary>
    /// Parses "host:port/path" from the URI path segment.
    /// Path format: "/host:port/route" or "host:port" or "/host:port".
    /// </summary>
    internal static void ParseHostPort(string path, WsEndpointOptions options)
    {
        var clean = path.TrimStart('/');
        // Find host:port — stop at the first slash after host:port
        var slashIdx = clean.IndexOf('/');
        var hostPort = slashIdx >= 0 ? clean[..slashIdx] : clean;

        var colonIdx = hostPort.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(hostPort[(colonIdx + 1)..], out var port))
        {
            options.Host = hostPort[..colonIdx];
            options.Port = port;
        }
        else
        {
            options.Host = hostPort;
        }
    }

    /// <summary>
    /// Extracts the path portion after host:port (for consumer route matching).
    /// "/0.0.0.0:9000/chat" → "/chat"
    /// "/0.0.0.0:9000" → "/"
    /// </summary>
    internal static string ExtractPath(string uriPath)
    {
        var clean = uriPath.TrimStart('/');
        var slashIdx = clean.IndexOf('/');
        return slashIdx >= 0 ? clean[slashIdx..] : "/";
    }
}

/// <summary>
/// WSS (WebSocket Secure) component. Uses the "wss" scheme and enables TLS.
/// </summary>
public class WssComponent : WsComponent
{
    /// <summary>Creates a WSS component.</summary>
    public WssComponent() : base("wss") { }
}

/// <summary>
/// WebSocket endpoint. Creates either a producer (ClientWebSocket) or consumer (Kestrel WS server).
/// </summary>
public class WsEndpoint : EndpointBase<WsEndpointOptions>
{
    /// <summary>Creates a WebSocket endpoint.</summary>
    public WsEndpoint(EndpointUri uri, WsComponent component, WsEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The consumer route path (after host:port).</summary>
    public string ConsumerPath => WsComponent.ExtractPath(Uri.Path);

    /// <summary>The endpoint options for external access.</summary>
    internal WsEndpointOptions EndpointOptions => Options;

    /// <summary>
    /// Builds the full WebSocket URL for the producer client.
    /// "ws:echo.example.com:8080/feed" → "ws://echo.example.com:8080/feed"
    /// </summary>
    public string BuildProducerUrl()
    {
        var scheme = Options.Ssl ? "wss" : "ws";
        var path = Uri.Path.TrimStart('/');
        return $"{scheme}://{path}";
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new WsProducer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new WsConsumer(this, processor, Options);
    }
}
