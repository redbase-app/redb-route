namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// MySQL 8.4 through MySqlConnector (tier 2). Container <c>route-mysql</c>, compose in <c>C:\Work\yaml\mysql</c>, brought up
/// for a run and removed after it.
/// </summary>
public sealed class MySqlE2EProvider : MySqlFamilyE2EProvider
{
    public override string Name => "mysql";

    protected override string EnvironmentVariable => "ROUTE_SQL_MYSQL_CS";

    protected override string DefaultConnectionString =>
        "Server=127.0.0.1;Port=3306;User ID=root;Password=1;Database=route_sql;Connection Timeout=15";

    public override string ReturningInsertSql(string table) =>
        throw new NotSupportedException("MySQL has no INSERT … RETURNING; its keys come from LAST_INSERT_ID(), which a batch does not collect.");

    public override string NativeReturningInsertSql(string table) => ReturningInsertSql(table);
}
