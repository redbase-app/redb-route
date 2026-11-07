using redb.Route.Tests.Sql.E2E.Base;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Postgres;

[Trait("Category", "Integration")]
[Trait("SqlProvider", "postgres")]
[Trait("SqlSuite", "Connector")]
public sealed class PostgresPollListDeliveryTests(ITestOutputHelper output) : PollListDeliveryTestsBase(output)
{
    protected override SqlE2EProvider Provider { get; } = new PostgresE2EProvider();
}
