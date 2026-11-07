using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Sqlite;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "sqlite-file")]
[Trait("SqlSuite", "Connector")]
public sealed class SqliteFileBatchEngineE2ETests(ITestOutputHelper output) : BatchEngineE2ETestsBase(output)
{
    protected override SqlE2EProvider Provider { get; } = new SqliteFileE2EProvider();
}
