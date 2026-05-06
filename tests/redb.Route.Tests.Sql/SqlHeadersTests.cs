using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

public class SqlHeadersTests
{
    [Fact]
    public void Prefix_IsRedbSql()
    {
        SqlHeaders.Prefix.Should().Be("redbSql.");
    }

    [Theory]
    [InlineData(nameof(SqlHeaders.Query), "redbSql.query")]
    [InlineData(nameof(SqlHeaders.UpdateCount), "redbSql.updateCount")]
    [InlineData(nameof(SqlHeaders.RowCount), "redbSql.rowCount")]
    [InlineData(nameof(SqlHeaders.DataSource), "redbSql.dataSource")]
    [InlineData(nameof(SqlHeaders.OutputType), "redbSql.outputType")]
    [InlineData(nameof(SqlHeaders.GeneratedKeys), "redbSql.generatedKeys")]
    [InlineData(nameof(SqlHeaders.Error), "redbSql.error")]
    [InlineData(nameof(SqlHeaders.TransactionId), "redbSql.transactionId")]
    [InlineData(nameof(SqlHeaders.StoredProcedure), "redbSql.storedProcedure")]
    [InlineData(nameof(SqlHeaders.ExecutionTime), "redbSql.executionTime")]
    public void Header_HasCorrectValue(string fieldName, string expectedValue)
    {
        var field = typeof(SqlHeaders).GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull();
        field!.GetValue(null).Should().Be(expectedValue);
    }

    [Fact]
    public void AllHeaders_StartWithPrefix()
    {
        var fields = typeof(SqlHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.Name != nameof(SqlHeaders.Prefix))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        fields.Should().NotBeEmpty();
        fields.Should().AllSatisfy(h => h.Should().StartWith(SqlHeaders.Prefix));
    }

    [Fact]
    public void AllHeaders_AreUnique()
    {
        var fields = typeof(SqlHeaders)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        fields.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Prefix_FollowsNamingConvention()
    {
        SqlHeaders.Prefix.Should().EndWith(".");
        SqlHeaders.Prefix.Should().NotContain(" ");
    }
}
