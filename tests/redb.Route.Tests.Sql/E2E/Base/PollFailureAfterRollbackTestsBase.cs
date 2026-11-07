using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// A transacted poll (<c>transacted=true</c>) with <c>onFailureAfterRollback=true</c> on a real provider: each row runs under a
/// savepoint, so a failed row is undone alone, keeps its <c>onFailure</c> mark and the rows around it commit in the same
/// poll. Without the option the whole poll rolls back, as in Apache Camel. SQL Server is the case that matters: SqlClient
/// reports no savepoint support and has it.
/// </summary>
public abstract class PollFailureAfterRollbackTestsBase(ITestOutputHelper output) : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _db = await SqlE2EDatabase.CreateAsync(Provider);
        for (var id = 1; id <= 3; id++)
            await Db.ExecuteAsync(Provider.InsertLiteral(Db.Table, id, "new"));
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_db is not null)
                await _db.DisposeAsync();
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    private async Task<string> PollAsync(bool afterRollback)
    {
        var factory = Db.CreateConnectionFactory();
        await using var context = new RouteContext();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Poll",
            ["transacted"] = "true",
            ["onSuccess"] = $"UPDATE {Db.Table} SET val = 'done' WHERE id = :#id",
            ["onFailure"] = $"UPDATE {Db.Table} SET val = 'failed' WHERE id = :#id",
            ["delay"] = "100",
            ["repeatCount"] = "1",
        };
        if (afterRollback)
            parameters["onFailureAfterRollback"] = "true";
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory,
            $"SELECT id, val FROM {Db.Table} WHERE val = 'new' ORDER BY id", parameters);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => Convert.ToInt64(ci.Arg<IExchange>().In.Headers["id"]) == 2
                ? Task.FromException(new InvalidOperationException("row 2 is bad"))
                : Task.CompletedTask);
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        var thrown = await Outcome.Of(() => consumer.Poll(CancellationToken.None));

        var rows = await Db.QueryAsync($"SELECT val FROM {Db.Table} ORDER BY id");
        var values = string.Join(",", rows.Select(r => r[0]));
        output.WriteLine($"[probe] {Provider.Name}: transacted poll, afterRollback={afterRollback}: poll={Outcome.Describe(thrown)}, rows={values}, open={factory.OpenConnections}");
        thrown.Should().BeNull(Outcome.Describe(thrown));
        factory.OpenConnections.Should().Be(0);
        return values;
    }

    [Fact]
    public async Task OnFailureAfterRollback_MarksTheFailedRow_AndCommitsTheOthers()
    {
        (await PollAsync(afterRollback: true)).Should().Be("done,failed,done");
    }

    [Fact]
    public async Task ByDefault_TheWholePollRollsBack()
    {
        (await PollAsync(afterRollback: false)).Should().Be("new,new,new");
    }
}
