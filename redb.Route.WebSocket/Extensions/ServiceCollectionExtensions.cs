using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Extensions;
using redb.Route.Http;

namespace redb.Route.WebSocket;

/// <summary>
/// Extension methods for registering the WebSocket component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the WS and WSS components with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteWebSocket(this IServiceCollection services)
        => AddRedbRouteWebSocket(services, null);

    /// <summary>
    /// Registers the WS and WSS components and lets the host authenticate incoming connections.
    /// <example><code>
    /// services.AddRedbRouteWebSocket(o =>
    ///     o.Authenticate = async ctx => await myJwtValidator.ValidateAsync(
    ///         ctx.Request.Query["access_token"]));
    /// </code></example>
    /// </summary>
    public static IServiceCollection AddRedbRouteWebSocket(
        this IServiceCollection services,
        Action<WsRegistrationOptions>? configure)
    {
        var registration = new WsRegistrationOptions();
        configure?.Invoke(registration);

        // ws endpoints multiplex onto the same Kestrel as HTTP/gRPC/SOAP/AS2 (idempotent call).
        services.AddRedbRouteHttpHosting();

        services.AddSingleton<WsComponent>();
        services.AddSingleton<WssComponent>();
        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            var serverManager = sp.GetRequiredService<SharedHttpServerManager>();

            var ws = sp.GetRequiredService<WsComponent>();
            ws.ServerManager = serverManager;
            ws.Authenticate = registration.Authenticate;
            context.AddComponent(ws);

            var wss = sp.GetRequiredService<WssComponent>();
            wss.ServerManager = serverManager;
            wss.Authenticate = registration.Authenticate;
            context.AddComponent(wss);
        });

        return services;
    }
}

/// <summary>
/// Host-supplied configuration for WebSocket consumers. The route host is a generic host, not an
/// ASP.NET application, so authentication arrives as a delegate the host writes rather than
/// through an authentication stack the connector would have to depend on.
/// </summary>
public sealed class WsRegistrationOptions
{
    /// <summary>
    /// Authenticates a WebSocket handshake. Returns the principal to attach to the request (its
    /// <c>NameIdentifier</c> claim reaches the route as the <c>redbWs.UserId</c> header), or null
    /// to reject the upgrade with 401.
    /// <para>
    /// Browsers cannot set headers on a WebSocket handshake, so a token normally arrives in the
    /// <c>access_token</c> query parameter: read it from there and from the Authorization header.
    /// </para>
    /// </summary>
    public Func<HttpContext, Task<ClaimsPrincipal?>>? Authenticate { get; set; }
}
