using System.Text;
using redb.Route.Sql.Connection;
using redb.Route.Sql.Repositories;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Tier2;

/// <summary>
/// The raw-ADO repositories bootstrap their tables on MySQL 8.4 (tier 2, the temporary <c>route-mysql</c> container): the key
/// columns must be sized, since MySQL refuses a <c>TEXT</c> column in a primary key (error 1170).
/// </summary>
[Trait("Category", "SqlE2ETier2")]
[Trait("SqlProvider", "mysql")]
public sealed class MySqlRepositoriesE2ETests : IAsyncDisposable
{
    private readonly MySqlE2EProvider _provider = new();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _tables = [];

    private ISqlConnectionFactory Factory => new SqlConnectionFactory(new SqlConnectionOptions
    {
        ConnectionString = _provider.ConnectionString,
        ProviderFactory = _provider.Factory,
    });

    [Fact]
    public async Task Idempotent_BootstrapsAndRoundTrips()
    {
        var table = Track("rsql_idem_" + _suffix);
        var repository = new SqlIdempotentRepository(Factory, new SqlIdempotentOptions { ProcessorName = "route-1", TableName = table });

        var added = await Outcome.Of(() => repository.Add("msg-1"));

        added.Should().BeNull("the table is created with sized key columns, which MySQL accepts in a primary key: " + Outcome.Describe(added));
        (await repository.Add("msg-1")).Should().BeFalse();
        (await repository.Contains("msg-1")).Should().BeTrue();
        await repository.Confirm("msg-1");
        await repository.Remove("msg-1");
        (await repository.Contains("msg-1")).Should().BeFalse();
    }

    [Fact]
    public async Task ClaimCheck_BootstrapsAndRoundTrips()
    {
        var table = Track("rsql_claim_" + _suffix);
        var repository = new SqlClaimCheckRepository(Factory, new SqlClaimCheckOptions { TableName = table, CleanupInterval = 0 });
        var payload = Encoding.UTF8.GetBytes("payload");

        var stored = await Outcome.Of(() => repository.Store("claim-1", payload));

        stored.Should().BeNull("the claim key column is sized, which MySQL accepts in a primary key: " + Outcome.Describe(stored));
        (await repository.Retrieve("claim-1")).Should().Equal(payload);
        (await repository.RetrieveAndRemove("claim-1")).Should().Equal(payload);
        (await repository.Retrieve("claim-1")).Should().BeNull();
    }

    private string Track(string table)
    {
        _tables.Add(table);
        return table;
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = await Factory.CreateConnectionAsync();
        foreach (var table in _tables)
            await SqlE2EDatabase.ExecuteAsync(connection, null, $"DROP TABLE IF EXISTS {table}");
    }
}
