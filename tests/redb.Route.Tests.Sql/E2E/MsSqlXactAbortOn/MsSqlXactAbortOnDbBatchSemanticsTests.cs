using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.MsSqlXactAbortOn;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "mssql-xact-on")]
[Trait("SqlSuite", "Characterization")]
public sealed class MsSqlXactAbortOnDbBatchSemanticsTests : DbBatchSemanticsTestsBase
{
    protected override SqlE2EProvider Provider { get; } = new MsSqlE2EProvider(xactAbortOn: true);
}
