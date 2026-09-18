using redb.Route.Sql.Connection;
using redb.Route.Sql.Repositories;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.MsSql;

/// <summary>
/// The tables the raw-ADO repositories create on SQL Server keep their primary key inside the 900-byte limit of a clustered
/// index: a key past it is created with warning 1946 and refuses the rows that reach the limit.
/// </summary>
[Trait("Category", "Integration")]
[Trait("SqlProvider", "mssql")]
[Trait("SqlSuite", "Connector")]
public sealed class MsSqlRepositoryBootstrapE2ETests : IAsyncDisposable
{
    private const int ClusteredKeyLimit = 900;

    private readonly MsSqlE2EProvider _provider = new(xactAbortOn: false);
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _tables = [];

    private ISqlConnectionFactory Factory => new SqlConnectionFactory(new SqlConnectionOptions
    {
        ConnectionString = _provider.ConnectionString,
        ProviderFactory = _provider.Factory,
    });

    [Fact]
    public async Task Idempotent_PrimaryKey_FitsTheClusteredIndex()
    {
        var table = Track("rsql_idem_" + _suffix);
        var repository = new SqlIdempotentRepository(Factory, new SqlIdempotentOptions { ProcessorName = "route-1", TableName = table });

        (await repository.Add("msg-1")).Should().BeTrue();

        (await PrimaryKeyBytesAsync(table)).Should().BeLessThanOrEqualTo(ClusteredKeyLimit,
            "processor_name and message_key together must fit a clustered key");
    }

    [Fact]
    public async Task ClaimCheck_PrimaryKey_FitsTheClusteredIndex()
    {
        var table = Track("rsql_claim_" + _suffix);
        var repository = new SqlClaimCheckRepository(Factory, new SqlClaimCheckOptions { TableName = table, CleanupInterval = 0 });

        await repository.Store("claim-1", new byte[] { 1, 2, 3 });

        (await PrimaryKeyBytesAsync(table)).Should().BeLessThanOrEqualTo(ClusteredKeyLimit);
    }

    /// <summary>The maximum byte length of the table's primary key, as the catalog reports it.</summary>
    private async Task<int> PrimaryKeyBytesAsync(string table)
    {
        await using var connection = await Factory.CreateConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SUM(CAST(c.max_length AS int))
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND i.is_primary_key = 1
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private string Track(string table)
    {
        _tables.Add(table);
        return table;
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = await Factory.CreateConnectionAsync();
        foreach (var table in _tables)
            await SqlE2EDatabase.ExecuteAsync(connection, null, $"IF OBJECT_ID(N'{table}', N'U') IS NOT NULL DROP TABLE {table}");
    }
}
