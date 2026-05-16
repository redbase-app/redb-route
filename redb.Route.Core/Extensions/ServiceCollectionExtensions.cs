using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.RedbCore.Repositories;

namespace redb.Route.RedbCore;

/// <summary>
/// Extension methods for registering the redb.Route.Core bridge in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the redb.Route ↔ redb.Core bridge services.
    /// Optionally configures <see cref="RedbIdempotentRepository"/> as the
    /// <see cref="IIdempotentRepository"/> implementation.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteCore(core =&gt;
    ///     {
    ///         core.UseIdempotentRepository(o =&gt; o.Ttl = TimeSpan.FromDays(7));
    ///     });
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteCore(
        this IServiceCollection services,
        Action<RedbCoreConfigurationBuilder>? configure = null)
    {
        var builder = new RedbCoreConfigurationBuilder(services);
        configure?.Invoke(builder);
        return services;
    }
}

/// <summary>
/// Fluent builder for configuring the redb.Route.Core bridge.
/// </summary>
public sealed class RedbCoreConfigurationBuilder
{
    private readonly IServiceCollection _services;

    internal RedbCoreConfigurationBuilder(IServiceCollection services) => _services = services;

    /// <summary>The service collection.</summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Registers <see cref="RedbIdempotentRepository"/> as <see cref="IIdempotentRepository"/>.
    /// Requires <c>IRedbService</c> to be registered (via <c>AddRedb()</c> or <c>AddRedbPro()</c>).
    /// </summary>
    /// <param name="configure">Configure processor name and TTL.</param>
    public RedbCoreConfigurationBuilder UseIdempotentRepository(
        Action<RedbIdempotentOptions>? configure = null)
    {
        var options = new RedbIdempotentOptions();
        configure?.Invoke(options);

        _services.AddSingleton(options);
        _services.AddSingleton<IIdempotentRepository, RedbIdempotentRepository>();

        return this;
    }
}
