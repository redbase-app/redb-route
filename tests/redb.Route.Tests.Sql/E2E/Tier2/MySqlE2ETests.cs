using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Tier2;

/// <summary>MySQL 8.4 (tier 2): the temporary <c>route-mysql</c> container.</summary>
[Trait("Category", "SqlE2ETier2")]
[Trait("SqlProvider", "mysql")]
public sealed class MySqlE2ETests(ITestOutputHelper output) : MySqlFamilyE2ETestsBase(new MySqlE2EProvider(), output);
