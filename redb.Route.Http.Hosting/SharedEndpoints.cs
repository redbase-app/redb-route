using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace redb.Route.Http;

/// <summary>
/// Endpoints a transport mapped onto a shared listener with
/// <see cref="SharedHttpServerManager.RegisterEndpoints"/>: framework endpoints of its own (a SignalR hub, a gRPC
/// service, minimal API handlers) served from a service container of its own. Holding the registration keeps the
/// listener alive; disposing it removes the endpoints from the running listener, and the listener stops once nothing
/// occupies it any more.
/// </summary>
public sealed class EndpointRegistration : IDisposable
{
    private readonly SharedHttpServerManager _manager;
    private int _disposed;

    internal EndpointRegistration(SharedHttpServerManager manager, string host, int port, IServiceProvider services,
        IReadOnlyList<Endpoint> endpoints)
    {
        _manager = manager;
        Host = host;
        Port = port;
        Services = services;
        Endpoints = endpoints;
    }

    /// <summary>Bind host of the listener the endpoints are on.</summary>
    public string Host { get; }

    /// <summary>Bind port of the listener the endpoints are on.</summary>
    public int Port { get; }

    /// <summary>The container the endpoints were mapped with and that serves their requests.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The endpoints, as the listener serves them.</summary>
    public IReadOnlyList<Endpoint> Endpoints { get; }

    /// <summary>Removes the endpoints from the listener. Disposing twice is safe.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _manager.UnregisterEndpoints(this);
    }
}

/// <summary>
/// The <see cref="IEndpointRouteBuilder"/> a registration maps on: its services are the registration's container, so
/// a framework that resolves its pieces from <see cref="ServiceProvider"/> (MapHub, MapConnections, MapGrpcService)
/// takes them from there and not from the listener.
/// </summary>
internal sealed class RegistrationRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider { get; } = services;

    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);

    /// <summary>
    /// The mapped endpoints, each served in a request scope of the registration's container: a handler, a hub
    /// (<c>Context.GetHttpContext().RequestServices</c>) or minimal API parameter binding sees the services the
    /// endpoints were mapped with.
    /// </summary>
    public IReadOnlyList<Endpoint> Build()
    {
        var scopes = ServiceProvider.GetRequiredService<IServiceScopeFactory>();
        return DataSources.SelectMany(source => source.Endpoints).Select(endpoint => endpoint is RouteEndpoint route
                && route.RequestDelegate is { } handler
                ? new RouteEndpoint(InScope(handler, scopes), route.RoutePattern, route.Order, route.Metadata, route.DisplayName)
                : endpoint).ToArray();
    }

    private static RequestDelegate InScope(RequestDelegate handler, IServiceScopeFactory scopes) => async context =>
    {
        var listenerServices = context.RequestServices;
        await using var scope = scopes.CreateAsyncScope();
        context.RequestServices = scope.ServiceProvider;
        try
        {
            await handler(context).ConfigureAwait(false);
        }
        finally
        {
            context.RequestServices = listenerServices;
        }
    };
}

/// <summary>
/// The endpoints of the registrations of one listener, added and removed while it runs: endpoint routing watches the
/// change token and rebuilds its matcher, so a registration reaches a running listener without touching the
/// connections it already serves.
/// </summary>
internal sealed class DynamicEndpointSource : EndpointDataSource
{
    private readonly object _gate = new();
    private readonly List<EndpointRegistration> _registrations = [];
    private Endpoint[] _endpoints = [];
    private CancellationTokenSource _changed = new();

    public int Count
    {
        get { lock (_gate) return _registrations.Count; }
    }

    public void Add(EndpointRegistration registration) => Change(() => _registrations.Add(registration));

    public bool Remove(EndpointRegistration registration)
    {
        var removed = false;
        Change(() => removed = _registrations.Remove(registration));
        return removed;
    }

    private void Change(Action change)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            change();
            _endpoints = _registrations.SelectMany(r => r.Endpoints).ToArray();
            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    public override IReadOnlyList<Endpoint> Endpoints => Volatile.Read(ref _endpoints);

    public override IChangeToken GetChangeToken()
    {
        lock (_gate)
            return new CancellationChangeToken(_changed.Token);
    }
}
