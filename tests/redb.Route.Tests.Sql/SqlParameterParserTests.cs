using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

public class SqlParameterParserTests
{
    // ── ExtractParameterNames ───────────────────────────────────────

    [Fact]
    public void ExtractParameterNames_SingleParam()
    {
        var names = SqlParameterParser.ExtractParameterNames("SELECT * FROM t WHERE id = @id");

        names.Should().ContainSingle().Which.Should().Be("id");
    }

    [Fact]
    public void ExtractParameterNames_MultipleParams()
    {
        var names = SqlParameterParser.ExtractParameterNames(
            "INSERT INTO t(name, age) VALUES(@name, @age)");

        names.Should().HaveCount(2);
        names.Should().Contain("name");
        names.Should().Contain("age");
    }

    [Fact]
    public void ExtractParameterNames_DuplicatesRemoved()
    {
        var names = SqlParameterParser.ExtractParameterNames(
            "UPDATE t SET name = @name WHERE name = @name AND id = @id");

        names.Should().HaveCount(2);
        names.Should().Contain("name");
        names.Should().Contain("id");
    }

    [Fact]
    public void ExtractParameterNames_NoParams()
    {
        var names = SqlParameterParser.ExtractParameterNames("SELECT 1");

        names.Should().BeEmpty();
    }

    [Fact]
    public void ExtractParameterNames_EmptyString()
    {
        var names = SqlParameterParser.ExtractParameterNames("");

        names.Should().BeEmpty();
    }

    [Fact]
    public void ExtractParameterNames_NullString()
    {
        var names = SqlParameterParser.ExtractParameterNames(null!);

        names.Should().BeEmpty();
    }

    [Fact]
    public void ExtractParameterNames_SkipsDoubleAt()
    {
        // @@IDENTITY should not be extracted
        var names = SqlParameterParser.ExtractParameterNames(
            "SELECT @@IDENTITY, @id FROM t");

        names.Should().ContainSingle().Which.Should().Be("id");
    }

    [Fact]
    public void ExtractParameterNames_UnderscoreInName()
    {
        var names = SqlParameterParser.ExtractParameterNames(
            "SELECT * FROM t WHERE user_id = @user_id");

        names.Should().ContainSingle().Which.Should().Be("user_id");
    }

    [Fact]
    public void ExtractParameterNames_NumbersInName()
    {
        var names = SqlParameterParser.ExtractParameterNames(
            "SELECT @param1, @param2");

        names.Should().HaveCount(2);
        names.Should().Contain("param1");
        names.Should().Contain("param2");
    }

    [Fact]
    public void ExtractParameterNames_ComplexSql()
    {
        var sql = """
            INSERT INTO orders(customer_id, product_id, qty, price)
            VALUES(@customerId, @productId, @qty, @price);
            UPDATE inventory SET stock = stock - @qty WHERE product_id = @productId;
            """;

        var names = SqlParameterParser.ExtractParameterNames(sql);

        names.Should().HaveCount(4);
        names.Should().Contain("customerId");
        names.Should().Contain("productId");
        names.Should().Contain("qty");
        names.Should().Contain("price");
    }

    [Fact]
    public void ExtractParameterNames_CaseInsensitiveDedupe()
    {
        var names = SqlParameterParser.ExtractParameterNames(
            "SELECT @Id, @ID, @id FROM t");

        names.Should().HaveCount(1);
    }

    // ── TranslateToOracle ───────────────────────────────────────────

    [Fact]
    public void TranslateToOracle_SingleParam()
    {
        var result = SqlParameterParser.TranslateToOracle(
            "SELECT * FROM t WHERE id = @id");

        result.Should().Be("SELECT * FROM t WHERE id = :id");
    }

    [Fact]
    public void TranslateToOracle_MultipleParams()
    {
        var result = SqlParameterParser.TranslateToOracle(
            "INSERT INTO t(name, age) VALUES(@name, @age)");

        result.Should().Be("INSERT INTO t(name, age) VALUES(:name, :age)");
    }

    [Fact]
    public void TranslateToOracle_SkipsDoubleAt()
    {
        var result = SqlParameterParser.TranslateToOracle(
            "SELECT @@IDENTITY, @id");

        result.Should().Contain("@@IDENTITY");
        result.Should().Contain(":id");
    }

    [Fact]
    public void TranslateToOracle_NoParams_ReturnsSame()
    {
        var sql = "SELECT 1 FROM DUAL";
        SqlParameterParser.TranslateToOracle(sql).Should().Be(sql);
    }
}
