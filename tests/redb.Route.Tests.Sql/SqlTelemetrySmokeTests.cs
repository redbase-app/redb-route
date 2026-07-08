using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Sql;

/// <summary>Smoke test for the P1 transport span opened by <see cref="SqlProducer"/>.</summary>
public sealed class SqlTelemetrySmokeTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlTelemetrySmokeTests()
    {
        _db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)");
        _db.Execute("INSERT INTO t(v) VALUES('a')");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SqlProducer_EmitsTransportSpanWithDbSystemTag()
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        var pars = new Dictionary<string, string>
        {
            ["mode"] = "Execute",
            ["dataSource"] = "main",
            ["outputType"] = "SelectList"
        };
        var uri = new EndpointUri("sql", "SELECT * FROM t", "sql:SELECT * FROM t", pars);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        var exchange = new Exchange(new Message(null));
        await producer.Process(exchange, CancellationToken.None);

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("db.system").Should().NotBeNull();
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
        activity.DisplayName.Should().Be("sql.execute");
    }

    [Fact]
    public async Task SqlProcedureProducer_EmitsTransportSpanWithProcedureDestination()
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        const string ProcName = "sp_smoke";
        var pars = new Dictionary<string, string>
        {
            ["mode"] = "Procedure",
            ["dataSource"] = "main",
            ["procedureName"] = ProcName,
            ["noop"] = "true"
        };
        var uri = new EndpointUri("sql", ProcName, $"sql:{ProcName}", pars);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        await producer.Process(new Exchange(new Message(null)), CancellationToken.None);

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.DisplayName.Should().Be("sql.procedure");
        activity.GetTagItem("db.system").Should().NotBeNull();
        activity.GetTagItem("messaging.destination.name").Should().Be(ProcName);
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }
}
