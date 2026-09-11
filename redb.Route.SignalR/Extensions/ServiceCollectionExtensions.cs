using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;
using redb.Route.Http;

namespace redb.Route.SignalR;

/// <summary>
/// Extension methods for registering the SignalR component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SignalR component with the route context. The hub is served by the shared
    /// Kestrel host, so it can share a port with HTTP, gRPC, SOAP and AS2 routes.
    /// </summary>
    public static IServiceCollection AddRedbRouteSignalR(this IServiceCollection services)
        => AddRedbRouteSignalR(services, null);

    /// <summary>
    /// Registers the SignalR component and lets the host configure the hub's listener.
    /// <para>
    /// This is the seam for everything the connector deliberately does not depend on: a backplane
    /// for scale-out, and authentication. A hub keeps its connections and groups in the memory of
    /// one process, so <b>with more than one replica a backplane is required</b> — without it a
    /// broadcast only reaches the clients attached to the replica that sent it.
    /// </para>
    /// <example><code>
    /// services.AddRedbRouteSignalR(o =>
    /// {
    ///     o.ConfigureHubServices(s => s.AddStackExchangeRedis("redis:6379"));
    ///     o.Authenticate = async ctx => await myJwtValidator.ValidateAsync(
    ///         ctx.Request.Query["access_token"]);
    /// });
    /// </code></example>
    /// </summary>
    public static IServiceCollection AddRedbRouteSignalR(
        this IServiceCollection services,
        Action<SignalRRegistrationOptions>? configure)
    {
        var registration = new SignalRRegistrationOptions();
        configure?.Invoke(registration);

        // The hub multiplexes onto the same Kestrel as HTTP/gRPC/SOAP/AS2 (idempotent call).
        services.AddRedbRouteHttpHosting();
        services.AddSingleton<SignalRComponent>();

        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            var component = sp.GetRequiredService<SignalRComponent>();
            component.ServerManager = sp.GetRequiredService<SharedHttpServerManager>();
            component.HubServicesConfigurator = registration.HubServices;
            component.Authenticate = registration.Authenticate;
            context.AddComponent(component);
        });

        return services;
    }
}

/// <summary>
/// Host-supplied configuration for the SignalR hub listener. Everything here is a delegate on
/// purpose: the connector stays free of Redis, Azure and authentication dependencies, and the
/// host, which knows its own infrastructure, supplies them.
/// </summary>
public sealed class SignalRRegistrationOptions
{
    internal Action<IServiceCollection>? HubServices { get; private set; }

    /// <summary>
    /// Authenticates a hub connection. Returns the principal to attach to the request (its
    /// <c>NameIdentifier</c> claim becomes SignalR's <c>UserIdentifier</c>, which is what makes
    /// <c>Clients.User(...)</c> and the <c>redbSignalR.UserId</c> header work), or null to reject
    /// the handshake with 401 before the connection is upgraded.
    /// <para>
    /// Browsers cannot set headers on a WebSocket handshake, so a token normally arrives in the
    /// <c>access_token</c> query parameter: read it from there and from the Authorization header.
    /// </para>
    /// </summary>
    public Func<Microsoft.AspNetCore.Http.HttpContext, Task<System.Security.Claims.ClaimsPrincipal?>>? Authenticate { get; set; }

    /// <summary>
    /// Adds services to the hub's listener container: a SignalR backplane
    /// (<c>AddStackExchangeRedis</c>, Azure SignalR), extra protocols, and so on. Note that this
    /// container is built by the shared host and does NOT see the services of the application
    /// hosting the route context, so anything needed here must be registered here.
    /// </summary>
    public SignalRRegistrationOptions ConfigureHubServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var previous = HubServices;
        HubServices = previous is null
            ? configure
            : s => { previous(s); configure(s); };
        return this;
    }
}
