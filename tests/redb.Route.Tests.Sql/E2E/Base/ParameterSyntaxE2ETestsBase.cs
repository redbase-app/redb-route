using redb.Route.Core;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// On a real server, <c>:#name</c> is the only placeholder: an <c>@</c> inside a string literal reaches the table as written.
/// </summary>
public abstract class ParameterSyntaxE2ETestsBase : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    /// <summary>The database of the test, created in <see cref="InitializeAsync"/>.</summary>
    protected SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(Provider);

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

    [Fact]
    public async Task LiteralContainingAt_PersistedAsWritten()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, Db.CreateConnectionFactory(),
            $"INSERT INTO {Db.Table} (id, val) VALUES (:#id, 'user@example.com')", new() { ["outputType"] = "None" });
        var exchange = new Exchange(new Message());
        exchange.In.Headers["id"] = 5;

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        var rows = await Db.QueryAsync($"SELECT id, val FROM {Db.Table}");
        rows.Should().ContainSingle();
        Convert.ToInt32(rows[0][0]).Should().Be(5);
        rows[0][1].Should().Be("user@example.com");
    }
}
