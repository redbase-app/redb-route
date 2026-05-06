using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
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
        var builder = new SqlConfigurationBuilder(services);
        configure?.Invoke(builder);

        services.AddSingleton<ISqlNamedQueryRegistry>(builder.BuildQueryRegistry());
        services.AddSingleton<SqlComponent>();

        // Capture data sources from builder
        var dataSources = builder.BuildDataSources();

        services.AddSingleton<ISqlComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<SqlComponent>();
            component.NamedQueryRegistry = sp.GetRequiredService<ISqlNamedQueryRegistry>();
            context.AddComponent(component);

            // Register all named data sources in the context registry
            foreach (var (name, factory) in dataSources)
                context.AddToRegistry(name, factory);

            return new SqlComponentRegistrar();
        });

        return services;
    }
}

/// <summary>Marker interface for DI registration.</summary>
internal interface ISqlComponentRegistrar;

/// <summary>Marker registration for DI.</summary>
internal sealed class SqlComponentRegistrar : ISqlComponentRegistrar;

/// <summary>Fluent builder for configuring the SQL connector.</summary>
public sealed class SqlConfigurationBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<KeyValuePair<string, ISqlConnectionFactory>> _dataSources = [];
    private readonly SqlNamedQueryRegistry _queryRegistry = new();

    internal SqlConfigurationBuilder(IServiceCollection services) => _services = services;

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
    public SqlConfigurationBuilder AddNamedQuery(string name, string sql)
    {
        _queryRegistry.Register(name, sql);
        return this;
    }

    internal IReadOnlyList<KeyValuePair<string, ISqlConnectionFactory>> BuildDataSources() => _dataSources;
    internal ISqlNamedQueryRegistry BuildQueryRegistry() => _queryRegistry;
}
