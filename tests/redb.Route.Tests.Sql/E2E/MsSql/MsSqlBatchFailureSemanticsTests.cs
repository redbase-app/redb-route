using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.MsSql;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "mssql")]
[Trait("SqlSuite", "Connector")]
public sealed class MsSqlBatchFailureSemanticsTests : BatchFailureSemanticsTestsBase
{
    protected override SqlE2EProvider Provider { get; } = new MsSqlE2EProvider(xactAbortOn: false);
}
