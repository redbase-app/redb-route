using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.MsSql;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "mssql")]
[Trait("SqlSuite", "Connector")]
public sealed class MsSqlBatchEngineE2ETests(ITestOutputHelper output) : BatchEngineE2ETestsBase(output)
{
    protected override SqlE2EProvider Provider { get; } = new MsSqlE2EProvider(xactAbortOn: false);
}
