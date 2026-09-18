using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Tier2;

/// <summary>MariaDB 11 (tier 2): the temporary <c>route-mariadb</c> container; unlike MySQL it returns keys with <c>RETURNING</c>.</summary>
[Trait("Category", "SqlE2ETier2")]
[Trait("SqlProvider", "mariadb")]
public sealed class MariaDbE2ETests(ITestOutputHelper output) : MySqlFamilyE2ETestsBase(new MariaDbE2EProvider(), output)
{
    [Fact]
    public async Task Connector_ReturningKeys_CollectedInHeader()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 21, ["val"] = "a" }, new() { ["id"] = 22, ["val"] = "b" } };

        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.ReturningInsertSql(Db.Table), items, breakOnError: null,
            batchSize: 10, new() { ["outputType"] = "SelectList" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        KeyIds(exchange).Should().Equal(21, 22);
    }
}
