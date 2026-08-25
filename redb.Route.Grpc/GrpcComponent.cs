using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Grpc;

/// <summary>
/// gRPC component. Scheme: "grpc".
/// <para>Producer: GrpcChannel-based client for calling gRPC services.</para>
/// <para>
/// Consumer: a path route on the shared Kestrel host (the same
/// <see cref="SharedHttpServerManager"/> that serves Http, As2 and Soap), speaking the gRPC wire
/// protocol directly. A gRPC method address is just the route path, so many methods share one port.
/// </para>
/// <para>URI format: grpc:host:port[/package.Service/Method]?deadline=30000&amp;plaintext=true</para>
/// </summary>
public class GrpcComponent : ComponentBase
{
    // Fallback for a host that never registered the shared manager in DI (a bare gRPC unit test, or a
    // process that uses no other HTTP-based transport).
    private readonly Lazy<SharedHttpServerManager> _ownServer = new(() => new SharedHttpServerManager());

    /// <inheritdoc />
    public override string Scheme => "grpc";

    /// <summary>
    /// Shared Kestrel host. Set by <c>AddRedbRouteGrpc()</c> from DI so that a gRPC route and an HTTP
    /// (or AS2, or SOAP) route in the same worker share one server per host:port and never fight over
    /// a port.
    /// </summary>
    public SharedHttpServerManager? ServerManager { get; set; }

    /// <summary>
    /// Resolution order: explicitly assigned → the host's DI singleton → a private instance. The DI step
    /// matters for module hosts that add the component by hand rather than through the extension method.
    /// </summary>
    internal SharedHttpServerManager Server =>
        ServerManager
        ?? Context?.GetServiceProvider()?.GetService<SharedHttpServerManager>()
        ?? _ownServer.Value;

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new GrpcEndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // Path is "host[:port][/package.Service/Method]".
        var (authority, methodPath) = SplitPath(uri.Path);
        ParseHostPort(authority, options, uri.RawParameters);

        if (!uri.RawParameters.ContainsKey("methodPath") && !string.IsNullOrEmpty(methodPath))
            options.MethodPath = methodPath!;

        // camel-grpc parity: grpc://host:port/my.Service?method=Call — service in the path, method as a
        // parameter. Composes into the same MethodPath the full address form produces.
        ApplyCamelStyleAddress(options, uri.RawParameters, ref methodPath);
        ApplyCamelStyleAliases(options, uri.RawParameters);

        options.DefaultMethodAddress =
            string.IsNullOrEmpty(methodPath)
            && !uri.RawParameters.ContainsKey("methodPath")
            && string.IsNullOrEmpty(options.Service);

        // The built-in streaming address streams by default; any other address opts in explicitly.
        if (!uri.RawParameters.ContainsKey("streaming")
            && string.Equals(options.MethodPath, GrpcEndpointOptions.DefaultStreamMethodPath,
                StringComparison.OrdinalIgnoreCase))
        {
            options.Streaming = true;
        }

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

        if (options.AllowClientReservedHeaders)
            Logger?.LogWarning(
                "gRPC {Method}: allowClientReservedHeaders=true — callers can set redb* headers that " +
                "upstream processors trust (client IP, transport metadata). Only do this behind a trusted proxy.",
                options.MethodPath);

        return new GrpcEndpoint(uri, this, options);
    }

    /// <summary>
    /// camel-grpc addresses a service in the URI path and names the method with a parameter
    /// (<c>grpc://host:port/my.Service?method=Call</c>). Both spellings end up in
    /// <see cref="GrpcEndpointOptions.MethodPath"/>, so a Camel-shaped URI works unchanged.
    /// </summary>
    private static void ApplyCamelStyleAddress(
        GrpcEndpointOptions options, IReadOnlyDictionary<string, string> rawParams, ref string? methodPath)
    {
        // A single path segment after the authority is a service name, not a full method address.
        if (methodPath is not null && !methodPath.TrimStart('/').Contains('/') && string.IsNullOrEmpty(options.Service))
            options.Service = methodPath.TrimStart('/');

        if (string.IsNullOrEmpty(options.Service)) return;

        var method = string.IsNullOrEmpty(options.Method)
            ? SplitMethodName(options.MethodPath)
            : options.Method!;

        options.MethodPath = $"/{options.Service!.Trim('/')}/{method}";
        methodPath = options.MethodPath;

        static string SplitMethodName(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? path : path[(slash + 1)..];
        }
    }

    /// <summary>
    /// Applies the camel-grpc convenience parameters: <c>negotiationType</c> (PLAINTEXT/TLS) and
    /// <c>maxMessageSize</c>. An explicit redb-native parameter always wins, so the two spellings never
    /// fight.
    /// </summary>
    private static void ApplyCamelStyleAliases(GrpcEndpointOptions options, IReadOnlyDictionary<string, string> rawParams)
    {
        if (!string.IsNullOrEmpty(options.NegotiationType))
        {
            var tls = options.NegotiationType!.Equals("TLS", StringComparison.OrdinalIgnoreCase);
            if (!rawParams.ContainsKey("plaintext")) options.Plaintext = !tls;
            if (!rawParams.ContainsKey("ssl") && tls) options.Ssl = true;
        }

        // `ssl=true` and `plaintext` are two spellings of one decision, and only negotiationType used to
        // join them. On its own, .Ssl() left Plaintext at its default of true — and the producer builds
        // its address from Plaintext alone, so a client explicitly told to use TLS connected over http://
        // and said nothing about it. An explicit `plaintext` still wins: someone who asks for cleartext
        // by name is debugging, and taking that away would be its own surprise.
        if (options.Ssl && !rawParams.ContainsKey("plaintext"))
            options.Plaintext = false;

        if (options.MaxMessageSize > 0)
        {
            if (!rawParams.ContainsKey("maxSendMessageSize")) options.MaxSendMessageSize = options.MaxMessageSize;
            if (!rawParams.ContainsKey("maxReceiveMessageSize")) options.MaxReceiveMessageSize = options.MaxMessageSize;
            if (!rawParams.ContainsKey("maxRequestMessageSize")) options.MaxRequestMessageSize = options.MaxMessageSize;
        }
    }

    /// <summary>
    /// Splits "host:port/package.Service/Method" into the authority and the method address. The method
    /// address starts at the first slash after the authority; everything before it is host[:port].
    /// </summary>
    private static (string Authority, string? MethodPath) SplitPath(string path)
    {
        var segment = path.TrimStart('/');
        var slash = segment.IndexOf('/');
        return slash < 0
            ? (segment, null)
            : (segment[..slash], segment[slash..]);
    }

    /// <summary>
    /// Extracts host and port from the authority segment (host[:port]).
    /// Only overrides options if not already set via query parameters.
    /// </summary>
    private static void ParseHostPort(string authority, GrpcEndpointOptions options, IReadOnlyDictionary<string, string> rawParams)
    {
        var colonIdx = authority.LastIndexOf(':');
        if (colonIdx > 0)
        {
            var hostPart = authority[..colonIdx];
            var portPart = authority[(colonIdx + 1)..];

            if (!rawParams.ContainsKey("host") && !string.IsNullOrEmpty(hostPart))
                options.Host = hostPart;

            if (!rawParams.ContainsKey("port") && int.TryParse(portPart, out var port))
                options.Port = port;
        }
        else if (!rawParams.ContainsKey("host") && !string.IsNullOrEmpty(authority))
        {
            options.Host = authority;
        }
    }
}

/// <summary>
/// gRPC endpoint. Creates either a producer (gRPC client) or consumer (a path route on the shared
/// Kestrel host).
/// <para>URI path format: host[:port][/package.Service/Method]</para>
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

    /// <summary>The shared Kestrel host this endpoint serves on.</summary>
    internal SharedHttpServerManager Server =>
        (Component as GrpcComponent)?.Server
        ?? throw new InvalidOperationException("gRPC endpoint has no GrpcComponent/server.");

    /// <summary>
    /// Builds the target address for the producer: scheme + authority, without the method address
    /// (the method is addressed per call, not per channel).
    /// </summary>
    public string BuildProducerAddress()
    {
        var scheme = Options.Plaintext ? "http" : "https";
        return $"{scheme}://{Options.Host}:{Options.Port}";
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
