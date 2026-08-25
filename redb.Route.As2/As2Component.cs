using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.As2;

/// <summary>
/// AS2 (RFC 4130) transport component for redb.Route. Schemes: <c>as2</c> (HTTP) and <c>as2s</c> (HTTPS).
/// <para>
/// Consumer URI (receive server): <c>as2:/inbound/orders?host=0.0.0.0&amp;port=4080&amp;connectionFactory=partner</c>.
/// Producer URI (send to partner): <c>as2s://partner.example.com:443/as2?connectionFactory=partner</c>.
/// </para>
/// Consumer vs producer is chosen by the DSL (<c>.From(...)</c> → consumer, <c>.To(...)</c> → producer),
/// not here — see <see cref="As2Endpoint"/>. Partner certificates/IDs/algorithms are resolved from a named
/// <see cref="As2ConnectionFactory"/> in the registry. See <c>docs/as2/02-DESIGN.md</c>.
/// </summary>
public sealed class As2Component : ComponentBase
{
    // Standalone fallback server, used only when no shared instance was injected from DI (e.g. tests, or an
    // AS2-only host that did not call AddRedbRouteHttpHosting).
    private readonly Lazy<SharedHttpServerManager> _ownServer = new(() => new SharedHttpServerManager());

    /// <summary>
    /// The shared Kestrel receive server, injected from DI so that HTTP-based connectors (redb.Route.Http,
    /// redb.Route.As2, …) in the same worker share ONE server — one Kestrel per host:port, routes
    /// multiplexed, no port fights. Set by the DI registrar; owned by the container.
    /// </summary>
    public SharedHttpServerManager? ServerManager { get; set; }

    /// <summary>Correlates outgoing async messages with the MDN receipts partners post back later.</summary>
    internal As2CorrelationStore Correlation { get; } = new();

    /// <summary>
    /// The server to host AS2 receive endpoints on. Resolution order:
    /// <list type="number">
    ///   <item><see cref="ServerManager"/> — set by the DI registrar (<c>AddRedbRouteAs2</c>).</item>
    ///   <item>the shared instance from the route context's service provider — this is the path that matters
    ///     under Tsak, where connectors are loaded from the shared folder via <c>Activator.CreateInstance</c>
    ///     (no DI injection), so the component shares the one <see cref="SharedHttpServerManager"/> the host
    ///     registered (via <c>AddRedbRouteHttp</c>/<c>AddRedbRouteHttpHosting</c>) instead of spinning its own.</item>
    ///   <item>a private fallback — standalone/tests with no DI at all.</item>
    /// </list>
    /// </summary>
    internal SharedHttpServerManager Server =>
        ServerManager
        ?? Context?.GetServiceProvider()?.GetService<SharedHttpServerManager>()
        ?? _ownServer.Value;

    /// <inheritdoc />
    public override string Scheme => "as2";

    /// <summary>HTTPS variant of the AS2 scheme.</summary>
    public override IReadOnlyList<string> AlternateSchemes => ["as2s"];

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new As2EndpointOptions();
        options.BindFromUri(uri.RawParameters);

        // The scheme carries the TLS decision: as2s ⇒ HTTPS. Applied after binding, before validation.
        if (string.Equals(uri.Scheme, "as2s", StringComparison.OrdinalIgnoreCase))
            options.UseTls = true;

        options.Validate();

        return new As2Endpoint(uri, this, options);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        Correlation.Dispose();   // stop the eviction timer
        // Only the private fallback is ours to dispose; a DI-injected shared server is owned by the container.
        if (_ownServer.IsValueCreated)
            await _ownServer.Value.DisposeAsync().ConfigureAwait(false);
    }
}
