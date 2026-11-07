using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Sqlite;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "sqlite-file")]
[Trait("SqlSuite", "Connector")]
public sealed class SqliteFileBatchBindingE2ETests : BatchBindingE2ETestsBase
{
    protected override SqlE2EProvider Provider { get; } = new SqliteFileE2EProvider();
}
