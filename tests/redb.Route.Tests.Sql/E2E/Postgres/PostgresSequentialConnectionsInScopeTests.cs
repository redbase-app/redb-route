using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Postgres;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "postgres")]
[Trait("SqlSuite", "Characterization")]
public sealed class PostgresSequentialConnectionsInScopeTests : SequentialConnectionsInScopeTestsBase
{
    protected override SqlE2EProvider Provider { get; } = new PostgresE2EProvider();
}
