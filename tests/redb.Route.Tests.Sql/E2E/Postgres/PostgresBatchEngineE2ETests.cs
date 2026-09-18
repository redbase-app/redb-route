using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Postgres;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "postgres")]
[Trait("SqlSuite", "Connector")]
public sealed class PostgresBatchEngineE2ETests(ITestOutputHelper output) : BatchEngineE2ETestsBase(output)
{
    protected override SqlE2EProvider Provider { get; } = new PostgresE2EProvider();
}
