using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

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
        services.AddSingleton<GrpcComponent>();

        services.AddSingleton<IGrpcComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<GrpcComponent>();
            context.AddComponent(component);
            return new GrpcComponentRegistrar();
        });

        return services;
    }
}

internal interface IGrpcComponentRegistrar;
internal sealed class GrpcComponentRegistrar : IGrpcComponentRegistrar;
