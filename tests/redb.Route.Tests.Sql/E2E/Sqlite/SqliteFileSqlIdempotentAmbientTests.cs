using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Sqlite;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "sqlite-file")]
[Trait("SqlSuite", "Connector")]
public sealed class SqliteFileSqlIdempotentAmbientTests : SqlIdempotentAmbientTestsBase
{
    protected override SqlE2EProvider Provider { get; } = new SqliteFileE2EProvider();

    /// <summary>Microsoft.Data.Sqlite does not enlist in System.Transactions.</summary>
    protected override bool EnlistsByDefault => false;
}
