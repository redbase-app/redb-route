using Microsoft.Extensions.DependencyInjection;
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
    public void AddDataSource_RegistersFactoryInContextRegistry()
    {
        var mockContext = Substitute.For<IRouteContext>();
        var services = new ServiceCollection();
        services.AddSingleton(mockContext);

        services.AddRedbRouteSql(sql =>
        {
            sql.AddDataSource("test", Substitute.For<ISqlConnectionFactory>());
        });

        var sp = services.BuildServiceProvider();
        sp.GetService<ISqlComponentRegistrar>(); // trigger registrar

        mockContext.Received(1).AddToRegistry("test", Arg.Any<object>());
    }

    [Fact]
    public void AddDataSource_WithFactory_RegistersInContextRegistry()
    {
        var mockFactory = Substitute.For<ISqlConnectionFactory>();
        var mockContext = Substitute.For<IRouteContext>();
        var services = new ServiceCollection();
        services.AddSingleton(mockContext);

        services.AddRedbRouteSql(sql =>
        {
            sql.AddDataSource("custom", mockFactory);
        });

        var sp = services.BuildServiceProvider();
        sp.GetService<ISqlComponentRegistrar>(); // trigger registrar

        mockContext.Received(1).AddToRegistry("custom", mockFactory);
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
    public void AddMultipleDataSources_AllRegisteredInContext()
    {
        var mockContext = Substitute.For<IRouteContext>();
        var services = new ServiceCollection();
        services.AddSingleton(mockContext);

        services.AddRedbRouteSql(sql =>
        {
            sql.AddDataSource("db1", Substitute.For<ISqlConnectionFactory>());
            sql.AddDataSource("db2", Substitute.For<ISqlConnectionFactory>());
        });

        var sp = services.BuildServiceProvider();
        sp.GetService<ISqlComponentRegistrar>(); // trigger registrar

        mockContext.Received(1).AddToRegistry("db1", Arg.Any<object>());
        mockContext.Received(1).AddToRegistry("db2", Arg.Any<object>());
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
    public void RegistrarMarker_IsRegistered()
    {
        var services = new ServiceCollection();
        var mockContext = Substitute.For<IRouteContext>();
        services.AddSingleton(mockContext);

        services.AddRedbRouteSql();

        var sp = services.BuildServiceProvider();
        // ISqlComponentRegistrar should be registered as singleton factory
        var descriptor = services.FirstOrDefault(d => d.ServiceType.Name == "ISqlComponentRegistrar");
        descriptor.Should().NotBeNull();
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
    public void Registrar_AddsComponentToContext()
    {
        var mockContext = Substitute.For<IRouteContext>();
        var services = new ServiceCollection();
        services.AddSingleton(mockContext);

        services.AddRedbRouteSql(sql =>
        {
            sql.AddDataSource("main", Substitute.For<ISqlConnectionFactory>());
        });

        var sp = services.BuildServiceProvider();

        // Trigger the registrar factory
        var registrar = sp.GetService<ISqlComponentRegistrar>();
        registrar.Should().NotBeNull();

        // Verify AddComponent was called with a SqlComponent
        mockContext.Received(1).AddComponent(Arg.Is<SqlComponent>(c => c.Scheme == "sql"));
    }
}
