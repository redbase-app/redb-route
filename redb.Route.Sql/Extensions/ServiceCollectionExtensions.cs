using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Extensions;
using redb.Route.Sql.Connection;

namespace redb.Route.Sql;

/// <summary>
/// Extension methods for registering the SQL transport in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="SqlComponent"/> in the route context so that
    /// <c>sql:</c> URIs are resolved.
    /// Named data sources are registered in the context registry via
    /// <c>context.AddToRegistry(name, ISqlConnectionFactory)</c>.
    /// The method can be called more than once (one module, one call): every call adds its data sources and named queries
    /// to the same component and the same registry.
    /// <example>
    /// <code>
    /// services.AddRedbRoute(route =&gt;
    /// {
    ///     route.Services.AddRedbRouteSql(sql =&gt;
    ///     {
    ///         sql.AddDataSource("main", opts =&gt;
    ///         {
    ///             opts.ConnectionString = "Server=...";
    ///             opts.ProviderName = "Microsoft.Data.SqlClient";
    ///         });
    ///     });
    ///     route.AddRouteBuilder&lt;MyRoutes&gt;();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteSql(
        this IServiceCollection services,
        Action<SqlConfigurationBuilder>? configure = null)
    {
        // One registry per container: a later call registers into it instead of replacing it, so the named queries of
        // every call resolve. A name registered twice fails right here, in the second call.
        var registry = services.FirstOrDefault(d => d.ServiceType == typeof(ISqlNamedQueryRegistry))?.ImplementationInstance
            as ISqlNamedQueryRegistry;
        if (registry is null)
        {
            registry = new SqlNamedQueryRegistry();
            services.AddSingleton(registry);
        }

        var builder = new SqlConfigurationBuilder(services, registry);
        configure?.Invoke(builder);

        services.TryAddSingleton<SqlComponent>();

        // Capture data sources from builder
        var dataSources = builder.BuildDataSources();

        // IRouteContextConfigurator is applied by RouteHostedService at startup --
        // the correct registration hook (a lazy marker singleton never fires).
        services.AddRouteContextConfigurator((sp, context) =>
        {
            if (!context.HasComponent("sql"))
                context.AddComponent(sp.GetRequiredService<SqlComponent>());

            // The producer and the consumer resolve ref: queries from the context's services.
            context.AddService(typeof(ISqlNamedQueryRegistry), sp.GetRequiredService<ISqlNamedQueryRegistry>());

            // Register all named data sources in the context registry
            foreach (var (name, factory) in dataSources)
                context.AddToRegistry(name, factory);
        });

        return services;
    }
}

/// <summary>Fluent builder for configuring the SQL connector.</summary>
public sealed class SqlConfigurationBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<KeyValuePair<string, ISqlConnectionFactory>> _dataSources = [];
    private readonly ISqlNamedQueryRegistry _queryRegistry;

    internal SqlConfigurationBuilder(IServiceCollection services, ISqlNamedQueryRegistry queryRegistry)
    {
        _services = services;
        _queryRegistry = queryRegistry;
    }

    /// <summary>The service collection.</summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Adds a named data source that will be resolved via <c>dataSource=name</c> in SQL URIs.
    /// </summary>
    public SqlConfigurationBuilder AddDataSource(string name, Action<SqlConnectionOptions> configure)
    {
        var options = new SqlConnectionOptions();
        configure(options);
        var factory = new SqlConnectionFactory(options);
        _dataSources.Add(new(name, factory));
        return this;
    }

    /// <summary>
    /// Adds a named data source with a pre-built connection factory.
    /// </summary>
    public SqlConfigurationBuilder AddDataSource(string name, ISqlConnectionFactory factory)
    {
        _dataSources.Add(new(name, factory));
        return this;
    }

    /// <summary>
    /// Registers a named query for reuse via <c>ref:queryName</c> protocol.
    /// </summary>
    /// <exception cref="ArgumentException">The name is already registered, by this call or an earlier one.</exception>
    public SqlConfigurationBuilder AddNamedQuery(string name, string sql)
    {
        _queryRegistry.Register(name, sql);
        return this;
    }

    internal IReadOnlyList<KeyValuePair<string, ISqlConnectionFactory>> BuildDataSources() => _dataSources;
}
