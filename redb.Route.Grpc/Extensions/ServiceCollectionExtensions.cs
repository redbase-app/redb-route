using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Http;

namespace redb.Route.Grpc;

/// <summary>
/// Extension methods for registering the gRPC component with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gRPC component with the route context.
    /// </summary>
    public static IServiceCollection AddRedbRouteGrpc(this IServiceCollection services)
    {
        // gRPC serves on the shared Kestrel host, same as Http, As2 and Soap. Idempotent, so calling
        // several transport registrations in one host still yields one server manager.
        services.AddRedbRouteHttpHosting();

        services.AddSingleton<GrpcComponent>();

        services.AddSingleton<IGrpcComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<GrpcComponent>();
            component.ServerManager = sp.GetRequiredService<redb.Route.Http.SharedHttpServerManager>();
            context.AddComponent(component);
            return new GrpcComponentRegistrar();
        });

        return services;
    }
}

internal interface IGrpcComponentRegistrar;
internal sealed class GrpcComponentRegistrar : IGrpcComponentRegistrar;
