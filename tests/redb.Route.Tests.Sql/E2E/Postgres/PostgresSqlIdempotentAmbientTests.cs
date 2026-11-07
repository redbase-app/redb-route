using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Postgres;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "postgres")]
[Trait("SqlSuite", "Connector")]
public sealed class PostgresSqlIdempotentAmbientTests : SqlIdempotentAmbientTestsBase
{
    protected override SqlE2EProvider Provider { get; } = new PostgresE2EProvider();

    protected override bool EnlistsByDefault => true;

    [Fact]
    public Task EnlistOff_DoesNotJoin() => AssertEnlistOffDoesNotJoin();
}
