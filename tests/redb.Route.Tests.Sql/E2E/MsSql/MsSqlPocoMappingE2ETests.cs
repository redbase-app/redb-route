using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.MsSql;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "mssql")]
public sealed class MsSqlPocoMappingE2ETests : PocoMappingE2ETestsBase
{
    protected override SqlE2EProvider Provider { get; } = new MsSqlE2EProvider(xactAbortOn: false);

    protected override string ZonedTimestampSql => "SELECT CAST('2024-01-02 03:04:05 +03:00' AS datetimeoffset) AS at";

    protected override string UnzonedTimestampSql => "SELECT CAST('2024-01-02 03:04:05' AS datetime2) AS at";
}
