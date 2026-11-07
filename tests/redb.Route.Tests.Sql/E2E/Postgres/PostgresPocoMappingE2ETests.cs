using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Postgres;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "postgres")]
public sealed class PostgresPocoMappingE2ETests : PocoMappingE2ETestsBase
{
    protected override SqlE2EProvider Provider { get; } = new PostgresE2EProvider();

    protected override string ZonedTimestampSql => "SELECT CAST('2024-01-02 03:04:05+03' AS timestamptz) AS at";

    protected override string UnzonedTimestampSql => "SELECT CAST('2024-01-02 03:04:05' AS timestamp) AS at";
}
