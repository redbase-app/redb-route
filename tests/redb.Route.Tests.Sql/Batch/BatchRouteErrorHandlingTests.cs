using System.Data.Common;
using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// A failed batch inside a route: the route's <c>OnException&lt;DbException&gt;</c> must match it. Handlers are found by the
/// thrown exception's type and its base types, so this holds only if the provider's exception reaches the route unwrapped.
/// </summary>
public sealed class BatchRouteErrorHandlingTests : IAsyncDisposable
{
    private readonly SqliteTestHelper _db = new();
    private readonly RouteContext _context = new();

    public BatchRouteErrorHandlingTests() =>
        _db.Execute("CREATE TABLE route_items (id INTEGER PRIMARY KEY, val TEXT NOT NULL)");

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        _db.Dispose();
    }

    [Fact]
    public async Task Break_OnExceptionDbException_HandlerRuns()
    {
        Exception? handled = null;
        _context.AddComponent(new SqlComponent());
        _context.AddToRegistry("main", _db.CreateFactory());
        _context.AddRoutes(r =>
        {
            r.From("direct://sql-batch-errors")
                .OnException<DbException>()
                    .Handled()
                    .Process(e => handled = e.Exception)
                .EndOnException()
                .To("sql:INSERT INTO route_items (id, val) VALUES (:#id, :#val)?dataSource=main&batchSize=10&outputType=None");
        });
        await _context.Start();

        var producer = _context.GetEndpoint("direct://sql-batch-errors").CreateProducer();
        await producer.Start();
        var items = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["val"] = "a" },
            new() { ["id"] = 1, ["val"] = "dup" },
        };

        var escaped = await Record.ExceptionAsync(() => producer.Process(new Exchange(new Message(items))));

        handled.Should().BeAssignableTo<DbException>(
            $"the route's OnException<DbException> handles the failed batch (escaped the route: {escaped?.GetType().Name ?? "nothing"})");
        Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM route_items")).Should().Be(0);
    }
}
