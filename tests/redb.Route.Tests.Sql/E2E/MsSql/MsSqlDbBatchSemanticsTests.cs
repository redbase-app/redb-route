using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.MsSql;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "mssql")]
[Trait("SqlSuite", "Characterization")]
public sealed class MsSqlDbBatchSemanticsTests : DbBatchSemanticsTestsBase
{
    protected override SqlE2EProvider Provider { get; } = new MsSqlE2EProvider(xactAbortOn: false);
}
