using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

public class SqlComponentTests
{
    [Fact]
    public void Scheme_ReturnsSql()
    {
        var component = new SqlComponent();
        component.Scheme.Should().Be("sql");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsSqlEndpoint()
    {
        var component = new SqlComponent();
        var uri = new EndpointUri("sql", "SELECT 1", "sql:SELECT 1",
            new Dictionary<string, string> { ["dataSource"] = "main" });

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<SqlEndpoint>();
        endpoint.Uri.Should().BeSameAs(uri);
        endpoint.Component.Should().BeSameAs(component);
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new SqlComponent();

        var act = () => component.CreateEndpoint(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_WithOptions_BindsCorrectly()
    {
        var component = new SqlComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Poll",
            ["dataSource"] = "main",
            ["delay"] = "2000",
            ["transacted"] = "true",
            ["outputType"] = "Scalar",
            ["commandTimeout"] = "60"
        };
        var uri = new EndpointUri("sql", "SELECT COUNT(*) FROM users", "sql:SELECT...", parameters);

        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Mode.Should().Be(SqlMode.Poll);
        endpoint.EndpointOptions.DataSource.Should().Be("main");
        endpoint.EndpointOptions.Delay.Should().Be(2000);
        endpoint.EndpointOptions.Transacted.Should().BeTrue();
        endpoint.EndpointOptions.OutputType.Should().Be(SqlOutputType.Scalar);
        endpoint.EndpointOptions.CommandTimeout.Should().Be(60);
    }

    [Fact]
    public void CreateEndpoint_InvalidOptions_Throws()
    {
        var component = new SqlComponent();
        // No DataSource or ConnectionString → Validate throws
        var uri = new EndpointUri("sql", "SELECT 1", "sql:SELECT 1", new Dictionary<string, string>());

        var act = () => component.CreateEndpoint(uri);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Endpoint_QueryOrProcedure_ReturnsParsedPath()
    {
        var component = new SqlComponent();
        var sqlText = "SELECT * FROM orders WHERE status = 'new'";
        var uri = new EndpointUri("sql", sqlText, $"sql:{sqlText}",
            new Dictionary<string, string> { ["dataSource"] = "main" });

        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);

        endpoint.QueryOrProcedure.Should().Be(sqlText);
    }

    // ── Endpoint CreateConsumer ─────────────────────────────────────

    [Fact]
    public void Endpoint_CreateConsumer_PollMode_ReturnsSqlConsumer()
    {
        var component = new SqlComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Poll",
            ["dataSource"] = "main"
        };
        var uri = new EndpointUri("sql", "SELECT 1", "sql:SELECT 1", parameters);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().BeOfType<SqlConsumer>();
    }

    [Fact]
    public void Endpoint_CreateConsumer_ExecuteMode_Throws()
    {
        var component = new SqlComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Execute",
            ["dataSource"] = "main"
        };
        var uri = new EndpointUri("sql", "INSERT INTO t(x) VALUES(1)", "sql:INSERT...", parameters);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var act = () => endpoint.CreateConsumer(processor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Poll*");
    }

    [Fact]
    public void Endpoint_CreateConsumer_NullProcessor_Throws()
    {
        var component = new SqlComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Poll",
            ["dataSource"] = "main"
        };
        var uri = new EndpointUri("sql", "SELECT 1", "sql:SELECT 1", parameters);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);

        var act = () => endpoint.CreateConsumer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Endpoint CreateProducer ─────────────────────────────────────

    [Fact]
    public void Endpoint_CreateProducer_ExecuteMode_ReturnsSqlProducer()
    {
        var component = new SqlComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Execute",
            ["dataSource"] = "main"
        };
        var uri = new EndpointUri("sql", "INSERT INTO t(x) VALUES(@x)", "sql:INSERT...", parameters);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);

        var producer = endpoint.CreateProducer();

        producer.Should().BeOfType<SqlProducer>();
    }

    [Fact]
    public void Endpoint_CreateProducer_ProcedureMode_ReturnsSqlProcedureProducer()
    {
        var component = new SqlComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Procedure",
            ["dataSource"] = "main",
            ["procedureName"] = "sp_Test"
        };
        var uri = new EndpointUri("sql", "sp_Test", "sql:sp_Test", parameters);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);

        var producer = endpoint.CreateProducer();

        producer.Should().BeOfType<SqlProcedureProducer>();
    }

    [Fact]
    public void Endpoint_CreateProducer_PollMode_ReturnsSqlProducer()
    {
        // Poll mode still creates a producer (for bi-directional routes)
        var component = new SqlComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Poll",
            ["dataSource"] = "main"
        };
        var uri = new EndpointUri("sql", "SELECT 1", "sql:SELECT 1", parameters);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);

        var producer = endpoint.CreateProducer();

        producer.Should().BeOfType<SqlProducer>();
    }

    // ── NamedQueryRegistry ─────────────────────────────────────────

    [Fact]
    public void Component_NamedQueryRegistry_InitiallyNull()
    {
        var component = new SqlComponent();
        component.NamedQueryRegistry.Should().BeNull();
    }
}
