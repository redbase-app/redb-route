using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Abstractions;
using redb.Route.Sql;
using redb.Route.Sql.Connection;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Tests for the DI registration extension method AddRedbRouteSql.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteSql_RegistersSqlComponent()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteSql();

        var sp = services.BuildServiceProvider();
        sp.GetService<SqlComponent>().Should().NotBeNull();
    }

    [Fact]
    public void AddRedbRouteSql_RegistersNamedQueryRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteSql();

        var sp = services.BuildServiceProvider();
        sp.GetService<ISqlNamedQueryRegistry>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddDataSource_RegistersFactoryInContextRegistry()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSql(sql =>
        {
            sql.AddDataSource("test", Substitute.For<ISqlConnectionFactory>());
        });

        await using var sp = services.BuildServiceProvider();
        await using var context = new RouteContext();
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        context.GetFromRegistry<ISqlConnectionFactory>("test").Should().NotBeNull();
    }

    [Fact]
    public async Task AddDataSource_WithFactory_RegistersInContextRegistry()
    {
        var mockFactory = Substitute.For<ISqlConnectionFactory>();
        var services = new ServiceCollection();
        services.AddRedbRouteSql(sql =>
        {
            sql.AddDataSource("custom", mockFactory);
        });

        await using var sp = services.BuildServiceProvider();
        await using var context = new RouteContext();
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        context.GetFromRegistry<ISqlConnectionFactory>("custom").Should().BeSameAs(mockFactory);
    }

    [Fact]
    public void AddNamedQuery_RegistersQuery()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteSql(sql =>
        {
            sql.AddNamedQuery("getOrders", "SELECT * FROM orders");
        });

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<ISqlNamedQueryRegistry>();
        registry.Resolve("getOrders").Should().Be("SELECT * FROM orders");
    }

    [Fact]
    public async Task AddMultipleDataSources_AllRegisteredInContext()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSql(sql =>
        {
            sql.AddDataSource("db1", Substitute.For<ISqlConnectionFactory>());
            sql.AddDataSource("db2", Substitute.For<ISqlConnectionFactory>());
        });

        await using var sp = services.BuildServiceProvider();
        await using var context = new RouteContext();
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        context.GetFromRegistry<ISqlConnectionFactory>("db1").Should().NotBeNull();
        context.GetFromRegistry<ISqlConnectionFactory>("db2").Should().NotBeNull();
    }

    [Fact]
    public void SqlConfigurationBuilder_FluentChaining()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteSql(sql =>
        {
            var result = sql
                .AddDataSource("ds", Substitute.For<ISqlConnectionFactory>())
                .AddNamedQuery("q1", "SELECT 1")
                .AddNamedQuery("q2", "SELECT 2");

            result.Should().BeSameAs(sql);
        });
    }

    [Fact]
    public void Configurator_IsRegistered()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSql();

        // The startup hook RouteHostedService applies must be present
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IRouteContextConfigurator));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddRedbRouteSql_NullConfigure_Works()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        // Should not throw
        services.AddRedbRouteSql(null);

        var sp = services.BuildServiceProvider();
        sp.GetService<SqlComponent>().Should().NotBeNull();
    }

    [Fact]
    public void AddRedbRouteSql_NoConfigure_Works()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteSql();

        var sp = services.BuildServiceProvider();
        sp.GetService<SqlComponent>().Should().NotBeNull();
    }

    [Fact]
    public async Task Configurator_AddsComponentToContext()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSql(sql =>
        {
            sql.AddDataSource("main", Substitute.For<ISqlConnectionFactory>());
        });

        await using var sp = services.BuildServiceProvider();
        await using var context = new RouteContext();
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        context.HasComponent("sql").Should().BeTrue();
    }
}
