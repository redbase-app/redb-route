namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// MariaDB 11 through MySqlConnector (tier 2). Container <c>route-mariadb</c>, compose in <c>C:\Work\yaml\mariadb</c>, brought
/// up for a run and removed after it.
/// </summary>
public sealed class MariaDbE2EProvider : MySqlFamilyE2EProvider
{
    public override string Name => "mariadb";

    protected override string EnvironmentVariable => "ROUTE_SQL_MARIADB_CS";

    protected override string DefaultConnectionString =>
        "Server=127.0.0.1;Port=3307;User ID=root;Password=1;Database=route_sql;Connection Timeout=15";

    public override string ReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (:#id, :#val) RETURNING id";

    public override string NativeReturningInsertSql(string table) =>
        $"INSERT INTO {table} (id, val) VALUES (@id, @val) RETURNING id";
}
