using Microsoft.Data.SqlClient;

namespace SerialNumbers.Worker;

/// <summary>Creates the demo database when the SQL Server does not have it yet.</summary>
public static class DemoDatabase
{
    public static async Task EnsureCreatedAsync(string connectionString)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        var database = target.InitialCatalog;
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };

        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@name) IS NULL
            BEGIN
                DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
            END
            """;
        command.Parameters.AddWithValue("@name", database);
        await command.ExecuteNonQueryAsync();
    }
}
