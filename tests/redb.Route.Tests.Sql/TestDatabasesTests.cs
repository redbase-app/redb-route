using Microsoft.Data.SqlClient;
using Npgsql;
using redb.Route.Tests.Shared;

namespace redb.Route.Tests.Sql;

/// <summary>
/// REDB_TEST_PG / REDB_TEST_MSSQL move the database suites to another server (the release gate's fresh test databases)
/// without touching any other part of a connection string; without them nothing changes.
/// </summary>
public class TestDatabasesTests
{
    private const string Pg =
        "Host=pgdb;Port=5432;Username=postgres;Password=1;Database=redb;Pooling=true;Include Error Detail=true;Options=-c jit=off";
    private const string Ms = "Server=msdb;Database=redb;User Id=sa;Password=1;TrustServerCertificate=true;Command Timeout=600;";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_target_the_strings_come_back_unchanged(string? target)
    {
        TestDatabases.Postgres(Pg, target).Should().BeSameAs(Pg);
        TestDatabases.MsSql(Ms, target).Should().BeSameAs(Ms);
    }

    [Fact]
    public void A_postgres_target_replaces_host_and_port_only()
    {
        var moved = new NpgsqlConnectionStringBuilder(TestDatabases.Postgres(Pg, "192.0.2.9:15432"));
        var original = new NpgsqlConnectionStringBuilder(Pg);

        moved.Host.Should().Be("192.0.2.9");
        moved.Port.Should().Be(15432);
        moved.Database.Should().Be(original.Database);
        moved.Username.Should().Be(original.Username);
        moved.Password.Should().Be(original.Password);
        moved.Options.Should().Be("-c jit=off");
        moved.IncludeErrorDetail.Should().BeTrue();
    }

    [Fact]
    public void A_postgres_target_without_a_port_uses_5432()
    {
        var moved = new NpgsqlConnectionStringBuilder(TestDatabases.Postgres("Host=localhost;Database=postgres", "pg-test"));

        moved.Host.Should().Be("pg-test");
        moved.Port.Should().Be(5432);
    }

    [Fact]
    public void A_sql_server_target_replaces_the_server_only()
    {
        var moved = new SqlConnectionStringBuilder(TestDatabases.MsSql(Ms, "192.0.2.9,11433"));
        var original = new SqlConnectionStringBuilder(Ms);

        moved.DataSource.Should().Be("192.0.2.9,11433");
        moved.InitialCatalog.Should().Be(original.InitialCatalog);
        moved.UserID.Should().Be(original.UserID);
        moved.Password.Should().Be(original.Password);
        moved.TrustServerCertificate.Should().BeTrue();
        moved.CommandTimeout.Should().Be(600);
    }

    [Fact]
    public void A_sql_server_string_written_with_data_source_is_moved_too()
    {
        var moved = new SqlConnectionStringBuilder(
            TestDatabases.MsSql("Data Source=127.0.0.1,1433;Initial Catalog=tempdb;User Id=sa;Password=1", "db-test,11433"));

        moved.DataSource.Should().Be("db-test,11433");
        moved.InitialCatalog.Should().Be("tempdb");
    }
}
