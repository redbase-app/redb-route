using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.SignalR;

/// <summary>
/// SignalR component. Scheme: "signalr".
/// <para>Consumer: Kestrel-based SignalR Hub server — accepts connections and receives method invocations.</para>
/// <para>Producer (client): HubConnection — connects to a remote SignalR hub and invokes methods.</para>
/// <para>Producer (server): IHubContext — broadcasts to clients of the local hub.</para>
/// <para>URI format: signalr:host:port/hubPath?method=Send&amp;inOut=true&amp;messagePack=true</para>
/// </summary>
public class SignalRComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, SignalRConsumer> _consumers = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string Scheme => "signalr";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new SignalREndpointOptions();
        ParseHostPort(uri.Path, options);
        options.BindFromUri(uri.RawParameters);

        // Named ConnectionFactory keeps the access token / cert password out of the route URI.
        if (!string.IsNullOrEmpty(options.ConnectionFactory) && Context is not null)
        {
            var factory = Context.GetFromRegistry<SignalRConnectionFactory>(options.ConnectionFactory);
            if (factory is not null)
                factory.ApplyTo(options, uri);
            else
                Logger?.LogWarning(
                    "SignalR: ConnectionFactory '{Name}' not found in registry, falling back to URI parameters",
                    options.ConnectionFactory);
        }

        options.Validate();

        return new SignalREndpoint(uri, this, options);
    }

    /// <summary>Registers a consumer for server-mode producer lookup.</summary>
    internal void RegisterConsumer(string normalizedKey, SignalRConsumer consumer)
        => _consumers[normalizedKey] = consumer;

    /// <summary>Unregisters a consumer.</summary>
    internal void UnregisterConsumer(string normalizedKey)
        => _consumers.TryRemove(normalizedKey, out _);

    /// <summary>Gets a running consumer by its normalized URI key.</summary>
    internal SignalRConsumer? GetConsumer(string normalizedKey)
        => _consumers.TryGetValue(normalizedKey, out var c) ? c : null;

    /// <summary>
    /// Parses "host:port/hubPath" from the URI path segment.
    /// Sets Host and Port on options. HubPath is extracted separately via <see cref="ExtractHubPath"/>.
    /// </summary>
    internal static void ParseHostPort(string path, SignalREndpointOptions options)
    {
        var clean = path.TrimStart('/');
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
    /// Extracts the hub path after host:port.
    /// "0.0.0.0:5000/chatHub" → "/chatHub"
    /// "0.0.0.0:5000" → "/"
    /// </summary>
    internal static string ExtractHubPath(string uriPath)
    {
        var clean = uriPath.TrimStart('/');
        var slashIdx = clean.IndexOf('/');
        return slashIdx >= 0 ? clean[slashIdx..] : "/";
    }
}

/// <summary>
/// SignalR endpoint. Creates either a producer or consumer for the SignalR hub.
/// </summary>
public class SignalREndpoint : EndpointBase<SignalREndpointOptions>
{
    /// <summary>Creates a SignalR endpoint.</summary>
    public SignalREndpoint(EndpointUri uri, SignalRComponent component, SignalREndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>The hub path (after host:port in the URI).</summary>
    public string HubPath => SignalRComponent.ExtractHubPath(Uri.Path);

    /// <summary>The endpoint options for external access.</summary>
    internal SignalREndpointOptions EndpointOptions => Options;

    /// <summary>
    /// Builds the full URL for producer client connections.
    /// "signalr:api.example.com:5000/chatHub" → "http://api.example.com:5000/chatHub"
    /// </summary>
    public string BuildClientUrl()
    {
        var scheme = Options.Ssl ? "https" : "http";
        var host = Options.Host;
        var hubPath = HubPath;
        return $"{scheme}://{host}:{Options.Port}{hubPath}";
    }

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        return new SignalRProducer(this, Options);
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new SignalRConsumer(this, processor, Options);
    }
}
