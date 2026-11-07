using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Http;

namespace redb.Route.As4;

/// <summary>
/// AS4 (OASIS ebMS 3.0, AS4 profile; eDelivery AS4 1.16 common profile) transport component for redb.Route.
/// Schemes: <c>as4</c> (HTTP) and <c>as4s</c> (HTTPS).
/// <para>
/// Receive: <c>as4:/as4/in?host=0.0.0.0&amp;port=4090&amp;connectionFactory=node</c>.
/// Send: <c>as4s://ap.partner.example/as4?connectionFactory=node&amp;partner=acme</c>.
/// </para>
/// Consumer or producer is chosen by the DSL side (<c>.From</c> / <c>.To</c>). The node and its trading
/// partners are resolved from the registry when the endpoint starts — see <see cref="As4ConnectionFactory"/>.
/// </summary>
public sealed class As4Component : ComponentBase
{
    // Standalone fallback server, used only when no shared instance was injected (tests, or a host without
    // AddRedbRouteHttpHosting).
    private readonly Lazy<SharedHttpServerManager> _ownServer = new(() => new SharedHttpServerManager());

    /// <summary>The shared Kestrel receive server, set by the DI registrar; owned by the container.</summary>
    public SharedHttpServerManager? ServerManager { get; set; }

    /// <summary>
    /// The server to host receive endpoints on: the DI-injected one; else the shared instance from the route
    /// context's service provider (the path under Tsak, which loads connectors with
    /// <c>Activator.CreateInstance</c> and injects nothing); else a private fallback.
    /// </summary>
    internal SharedHttpServerManager Server =>
        ServerManager
        ?? Context.Resolve<SharedHttpServerManager>()
        ?? _ownServer.Value;

    /// <inheritdoc />
    public override string Scheme => "as4";

    /// <summary>HTTPS variant of the AS4 scheme.</summary>
    public override IReadOnlyList<string> AlternateSchemes => ["as4s"];

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new As4EndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // The scheme carries the TLS decision: as4s means HTTPS. Applied after binding, before validation.
        if (string.Equals(uri.Scheme, "as4s", StringComparison.OrdinalIgnoreCase))
            options.UseTls = true;

        options.Validate();

        return new As4Endpoint(uri, this, options);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        // Only the private fallback is ours to dispose; a shared server belongs to the container.
        if (_ownServer.IsValueCreated)
            await _ownServer.Value.DisposeAsync().ConfigureAwait(false);
    }
}
