using System.Data.Common;
using Microsoft.Data.Sqlite;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Sql.Connection;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql;

/// <summary>
/// <c>redbSql.executionTime</c> measures the statement itself: a statement that takes a known time to run reports at least
/// that time, in the producer and in the poll consumer. The delay comes from a SQLite function that sleeps while the
/// statement executes.
/// </summary>
public sealed class SqlExecutionTimeTests : IDisposable
{
    private const int SleepMs = 100;
    private const int AtLeastMs = 90;
    private readonly SqliteTestHelper _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Execute_ExecutionTime_IncludesCommandExecution()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new SleepFunctionFactory(_db.ConnectionString),
            $"SELECT redb_sleep({SleepMs}) AS v", new() { ["outputType"] = "Scalar" });
        var exchange = new Exchange(new Message());

        await endpoint.CreateProducer().Process(exchange, CancellationToken.None);

        Convert.ToInt64(exchange.In.Headers[SqlHeaders.ExecutionTime]).Should().BeGreaterThanOrEqualTo(AtLeastMs,
            "the statement sleeps for {0} ms while it executes", SleepMs);
    }

    [Fact]
    public async Task Poll_ExecutionTime_IncludesQueryExecution()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new SleepFunctionFactory(_db.ConnectionString),
            $"SELECT redb_sleep({SleepMs}) AS v", new() { ["mode"] = "Poll", ["repeatCount"] = "1" });
        var reported = new List<long>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                reported.Add(Convert.ToInt64(ci.Arg<IExchange>().In.Headers[SqlHeaders.ExecutionTime]));
                return Task.CompletedTask;
            });
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        await consumer.Poll(CancellationToken.None);

        reported.Should().ContainSingle().Which.Should().BeGreaterThanOrEqualTo(AtLeastMs,
            "the poll query sleeps for {0} ms while it executes", SleepMs);
    }

    /// <summary>Opens connections to the test database with <c>redb_sleep(ms)</c> registered.</summary>
    private sealed class SleepFunctionFactory(string connectionString) : ISqlConnectionFactory
    {
        public async Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default)
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            connection.CreateFunction("redb_sleep", (long ms) =>
            {
                Thread.Sleep((int)ms);
                return ms;
            });
            return connection;
        }
    }
}
