using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Abstractions;
using redb.Route.Sql;
using redb.Route.Sql.Connection;
using redb.Route.Tests.Sql.E2E.Infrastructure;

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
    public async Task AddRedbRouteSql_Twice_KeepsNamedQueriesAndDataSourcesOfBothCalls()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSql(sql => sql.AddNamedQuery("q1", "SELECT 1").AddDataSource("a", Substitute.For<ISqlConnectionFactory>()));
        services.AddRedbRouteSql(sql => sql.AddNamedQuery("q2", "SELECT 2").AddDataSource("b", Substitute.For<ISqlConnectionFactory>()));

        await using var sp = services.BuildServiceProvider();
        await using var context = new RouteContext();
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        var registry = context.GetService<ISqlNamedQueryRegistry>();
        registry.Should().NotBeNull();
        registry!.Resolve("q1").Should().Be("SELECT 1", "a second registration (another module) must not drop the first one's queries");
        registry.Resolve("q2").Should().Be("SELECT 2");
        context.GetFromRegistry<ISqlConnectionFactory>("a").Should().NotBeNull();
        context.GetFromRegistry<ISqlConnectionFactory>("b").Should().NotBeNull();
        context.HasComponent("sql").Should().BeTrue();
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

    // ── Named queries through the host path ─────────────────────────

    [Fact]
    public async Task AddNamedQuery_RefResolvesInProducer()
    {
        using var db = NamedItemsDatabase();
        await using var sp = NamedQueryServices(db).BuildServiceProvider();
        await using var context = ConfiguredContext(sp);
        var endpoint = RefEndpoint(sp, new() { ["mode"] = "Execute", ["outputType"] = "SelectList" });
        var exchange = new Exchange(new Message("untouched"));

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Body.Should().BeAssignableTo<IList<Dictionary<string, object?>>>()
            .Which.Select(r => r["val"]).Should().Equal("a", "b");
    }

    [Fact]
    public async Task AddNamedQuery_RefResolvesInPollConsumer()
    {
        using var db = NamedItemsDatabase();
        await using var sp = NamedQueryServices(db).BuildServiceProvider();
        await using var context = ConfiguredContext(sp);
        var endpoint = RefEndpoint(sp, new() { ["mode"] = "Poll", ["repeatCount"] = "1" });
        var received = new List<object?>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(((Dictionary<string, object?>)ci.Arg<IExchange>().In.Body!)["val"]);
                return Task.CompletedTask;
            });
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        var thrown = await Outcome.Of(() => consumer.Poll(CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        received.Should().Equal("a", "b");
    }

    private static SqliteTestHelper NamedItemsDatabase()
    {
        var db = new SqliteTestHelper();
        db.Execute("CREATE TABLE named_items (id INTEGER PRIMARY KEY, val TEXT)");
        db.Execute("INSERT INTO named_items (id, val) VALUES (1, 'a'), (2, 'b')");
        return db;
    }

    private static ServiceCollection NamedQueryServices(SqliteTestHelper db)
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSql(sql => sql
            .AddDataSource("main", db.CreateFactory())
            .AddNamedQuery("allItems", "SELECT id, val FROM named_items ORDER BY id"));
        return services;
    }

    /// <summary>A context configured the way <c>RouteHostedService</c> configures it at startup.</summary>
    private static RouteContext ConfiguredContext(IServiceProvider sp)
    {
        var context = new RouteContext();
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);
        return context;
    }

    private static SqlEndpoint RefEndpoint(IServiceProvider sp, Dictionary<string, string> parameters)
    {
        parameters["dataSource"] = "main";
        var uri = new EndpointUri("sql", "ref:allItems", "sql:ref:allItems", parameters);
        return (SqlEndpoint)sp.GetRequiredService<SqlComponent>().CreateEndpoint(uri);
    }
}
