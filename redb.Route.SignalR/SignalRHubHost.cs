using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using redb.Route.Extensions;
using redb.Route.Http;

namespace redb.Route.SignalR;

/// <summary>
/// The hub of one consumer endpoint on the shared listener. It has a service container of its own, so whatever SignalR
/// keeps per container stays with this hub: the connection manager, the lifetime manager behind <c>Clients.All</c>,
/// <c>Group</c> and <c>User</c>, the protocols, the hub options and a backplane. Two hubs on one listener, from one
/// route context or from two, never see each other's connections, and one of them can come and go while the other
/// serves.
/// </summary>
internal sealed class SignalRHubHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly HubLifetime _lifetime;
    private readonly IHostedService[] _hosted;
    private readonly EndpointRegistration _registration;

    private SignalRHubHost(ServiceProvider services, HubLifetime lifetime, IHostedService[] hosted,
        EndpointRegistration registration)
    {
        _services = services;
        _lifetime = lifetime;
        _hosted = hosted;
        _registration = registration;
    }

    /// <summary>
    /// The hub's container. The server-mode producer resolves <c>IHubContext</c> from here, and a hub request runs in
    /// a scope of it (<c>Context.GetHttpContext().RequestServices</c>).
    /// </summary>
    public IServiceProvider Services => _services;

    /// <summary>
    /// Builds the hub's container, starts the hosted services registered in it, the way a host would, and maps the
    /// hub onto the endpoint's listener, running or not. A hosted service that refuses to start (a backplane whose
    /// channels another hub of this process already holds) keeps the hub off the listener.
    /// </summary>
    public static async Task<SignalRHubHost> StartAsync(SignalREndpoint endpoint, CancellationToken ct)
    {
        var component = (SignalRComponent)endpoint.Component;
        var options = endpoint.EndpointOptions;
        var lifetime = new HubLifetime();
        var services = BuildServices(endpoint, component, options, lifetime);
        var started = new List<IHostedService>();

        try
        {
            foreach (var hosted in services.GetServices<IHostedService>())
            {
                await hosted.StartAsync(ct).ConfigureAwait(false);
                started.Add(hosted);
            }

            var registration = endpoint.ServerManager.RegisterEndpoints(options.Host, options.Port, services,
                routes => routes.MapHub<RedbBridgeHub>(endpoint.HubPath, dispatcher => ConfigureDispatcher(dispatcher, options))
                    .Add(hub => Gate(hub, component)),
                // A hub maps itself instead of calling RegisterRoute, so this is where the listener learns it has to
                // terminate TLS.
                options.Ssl, options.SslCertPath, options.SslCertPassword);
            return new SignalRHubHost(services, lifetime, [.. started], registration);
        }
        catch
        {
            lifetime.StopApplication();
            await StopAsync(started, CancellationToken.None).ConfigureAwait(false);
            await services.DisposeAsync().ConfigureAwait(false);
            lifetime.Dispose();
            throw;
        }
    }

    private static async Task StopAsync(IReadOnlyList<IHostedService> hosted, CancellationToken ct)
    {
        for (var i = hosted.Count - 1; i >= 0; i--)
            await hosted[i].StopAsync(ct).ConfigureAwait(false);
    }

    private static ServiceProvider BuildServices(SignalREndpoint endpoint, SignalRComponent component,
        SignalREndpointOptions options, HubLifetime lifetime)
    {
        var services = new ServiceCollection();

        // SignalR logs through the route context, like the rest of the connector. Registered before AddLogging, which
        // only fills in what is missing; the container does not dispose an instance it was handed.
        if (component.Context.Resolve<ILoggerFactory>() is { } loggerFactory)
            services.AddSingleton(loggerFactory);
        services.AddLogging();

        // The hub's own lifetime: the connection manager closes the hub's connections when it stops, and the listener's
        // lifetime is not the hub's to stop.
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var signalR = services.AddSignalR(hub =>
        {
            // Волна В4 плана лимитов: SignalR's own per-client invocation parallelism.
            // 0 keeps SignalR's default (1 = per-client serial).
            if (options.MaxParallelInvocationsPerClient > 0)
                hub.MaximumParallelInvocationsPerClient = options.MaxParallelInvocationsPerClient;
        });
        if (options.MessagePack)
            signalR.AddMessagePackProtocol();

        // The bridge hub is activated from this container and takes its endpoint from it.
        services.AddSingleton(endpoint);

        // Host-supplied extras: backplane for scale-out, custom protocols, a user id provider, and so on.
        component.HubServicesConfigurator?.Invoke(services, new SignalRHubDescriptor(options.Host, options.Port, endpoint.HubPath));

        return services.BuildServiceProvider();
    }

    private static void ConfigureDispatcher(HttpConnectionDispatcherOptions dispatcher, SignalREndpointOptions options)
    {
        dispatcher.Transports = options.Transport switch
        {
            SignalRTransport.ServerSentEvents => HttpTransportType.ServerSentEvents,
            SignalRTransport.LongPolling => HttpTransportType.LongPolling,
            _ => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling
        };
    }

    /// <summary>
    /// Authentication gate on the hub's endpoints (negotiate and the connection itself): the host's own validator
    /// decides, and a rejected handshake gets 401 before the connection is upgraded. Not ASP.NET authentication,
    /// because the process hosting the route context is a generic host, not an ASP.NET application. Without the
    /// component's own delegate, the caller is whoever the shared host's resolver identified (it runs earlier in the
    /// pipeline), and that principal is handed to SignalR the same way.
    /// </summary>
    private static void Gate(EndpointBuilder hub, SignalRComponent component)
    {
        var authenticate = component.Authenticate;
        if ((authenticate is null && component.EffectiveServerManager.Options.ResolvePrincipal is null)
            || hub.RequestDelegate is not { } next)
            return;

        hub.RequestDelegate = async context =>
        {
            if (authenticate is not null)
            {
                var principal = await authenticate(context).ConfigureAwait(false);
                if (principal is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                // SignalR reads UserIdentifier from HttpContext.User.
                context.User = principal;
            }
            else if (SharedHttpServerManager.GetResolvedPrincipal(context) is { } resolved)
            {
                context.User = resolved;
            }

            await next(context).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Takes the hub off the listener: the endpoints first, so no new connection reaches a hub that is going away, then
    /// the hub's lifetime, which closes its connections, then its hosted services in reverse order, then its container.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _registration.Dispose();
        _lifetime.StopApplication();
        try
        {
            await StopAsync(_hosted, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _services.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }
    }

    /// <summary>
    /// The lifetime of one hub. Started when it is created; stopping it is what makes the hub's connection manager
    /// close the hub's connections, and only those.
    /// </summary>
    private sealed class HubLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        private int _stop;

        public HubLifetime() => _started.Cancel();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            if (Interlocked.Exchange(ref _stop, 1) != 0) return;
            _stopping.Cancel();
            _stopped.Cancel();
        }

        public void Dispose()
        {
            StopApplication();
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
