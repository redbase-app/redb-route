using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Sqlite;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "sqlite-file")]
[Trait("SqlSuite", "Characterization")]
public sealed class SqliteFileProviderSemanticsTests : ProviderSemanticsTestsBase
{
    protected override SqlE2EProvider Provider { get; } = new SqliteFileE2EProvider();
}
