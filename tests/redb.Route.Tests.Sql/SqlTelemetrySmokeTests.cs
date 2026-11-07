using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Sql;

/// <summary>Smoke test for the P1 transport span opened by <see cref="SqlProducer"/>.</summary>
/// <remarks>
/// The in-memory exporter listens to the process-wide route activity source, so spans of SQL producers in tests running
/// in parallel land in the same list. Each test therefore gives its endpoint a unique marker and picks its span by the
/// <c>redb.route.endpoint</c> tag — never by position — and stops the tracer before reading the list.
/// </remarks>
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

        var marker = $"telemetry-{Guid.NewGuid():N}";
        var sql = $"SELECT * FROM t /* {marker} */";
        var pars = new Dictionary<string, string>
        {
            ["mode"] = "Execute",
            ["dataSource"] = "main",
            ["outputType"] = "SelectList"
        };
        var uri = new EndpointUri("sql", sql, $"sql:{sql}", pars);
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
        tracer.Dispose();
        var activity = activities.Should().ContainSingle(a => HasMarker(a, marker)).Which;
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("db.system").Should().NotBeNull();
        activity.DisplayName.Should().Be("sql.execute");
    }

    [Fact]
    public async Task SqlProcedureProducer_EmitsTransportSpanWithProcedureDestination()
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        var procName = $"sp_smoke_{Guid.NewGuid():N}";
        var pars = new Dictionary<string, string>
        {
            ["mode"] = "Procedure",
            ["dataSource"] = "main",
            ["procedureName"] = procName,
            ["noop"] = "true"
        };
        var uri = new EndpointUri("sql", procName, $"sql:{procName}", pars);
        var endpoint = (SqlEndpoint)component.CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        await producer.Process(new Exchange(new Message(null)), CancellationToken.None);

        tracer.ForceFlush(1000);
        tracer.Dispose();
        var activity = activities.Should().ContainSingle(a => HasMarker(a, procName)).Which;
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.DisplayName.Should().Be("sql.procedure");
        activity.GetTagItem("db.system").Should().NotBeNull();
        activity.GetTagItem("messaging.destination.name").Should().Be(procName);
    }

    private static bool HasMarker(Activity activity, string marker) =>
        activity.GetTagItem("redb.route.endpoint") is string endpoint && endpoint.Contains(marker, StringComparison.Ordinal);
}
