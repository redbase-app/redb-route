using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Placeholders are written as in Apache Camel, <c>:#name</c>, and are found only in the SQL itself — not inside string
/// literals, quoted identifiers, comments or PostgreSQL dollar-quoted bodies. <c>@</c> belongs to the database (T-SQL variables
/// and <c>EXEC</c> argument names, MySQL user variables, <c>@@IDENTITY</c>) and is never a placeholder.
/// </summary>
public class SqlParameterParserTests
{
    // ── Names ───────────────────────────────────────────────────────

    [Fact]
    public void ExtractParameterNames_SingleParam()
    {
        SqlParameterParser.ExtractParameterNames("SELECT * FROM t WHERE id = :#id").Should().Equal("id");
    }

    [Fact]
    public void ExtractParameterNames_MultipleParams_InOrder()
    {
        SqlParameterParser.ExtractParameterNames("INSERT INTO t(name, age) VALUES(:#name, :#age)").Should().Equal("name", "age");
    }

    [Fact]
    public void ExtractParameterNames_DuplicatesRemoved_FirstOccurrenceKept()
    {
        SqlParameterParser.ExtractParameterNames("UPDATE t SET name = :#name WHERE name = :#name AND id = :#id")
            .Should().Equal("name", "id");
    }

    [Fact]
    public void ExtractParameterNames_NoParams() =>
        SqlParameterParser.ExtractParameterNames("SELECT 1").Should().BeEmpty();

    [Fact]
    public void ExtractParameterNames_EmptyString() =>
        SqlParameterParser.ExtractParameterNames("").Should().BeEmpty();

    [Fact]
    public void ExtractParameterNames_NullString() =>
        SqlParameterParser.ExtractParameterNames(null!).Should().BeEmpty();

    [Fact]
    public void ExtractParameterNames_UnderscoreAndDigitsInName() =>
        SqlParameterParser.ExtractParameterNames("SELECT * FROM t WHERE user_id = :#user_id OR x = :#param2")
            .Should().Equal("user_id", "param2");

    [Fact]
    public void ExtractParameterNames_ComplexSql()
    {
        var sql = """
            INSERT INTO orders(customer_id, product_id, qty, price)
            VALUES(:#customerId, :#productId, :#qty, :#price);
            UPDATE inventory SET stock = stock - :#qty WHERE product_id = :#productId;
            """;

        SqlParameterParser.ExtractParameterNames(sql).Should().Equal("customerId", "productId", "qty", "price");
    }

    [Fact]
    public void ExtractParameterNames_CaseInsensitiveDedupe() =>
        SqlParameterParser.ExtractParameterNames("SELECT :#Id, :#ID, :#id FROM t").Should().ContainSingle();

    [Fact]
    public void ExtractParameterNames_CastAfterPlaceholder() =>
        SqlParameterParser.ExtractParameterNames("SELECT :#details::jsonb, CAST(:#id AS int)").Should().Equal("details", "id");

    // ── What is not a placeholder ───────────────────────────────────

    [Fact]
    public void AtSigns_BelongToTheDatabase() =>
        SqlParameterParser.ExtractParameterNames("DECLARE @n int = :#v; EXEC p @arg = @n; SELECT @@IDENTITY, @user_var")
            .Should().Equal("v");

    [Fact]
    public void SkipsSingleQuotedLiteral_WithEscapedQuote() =>
        SqlParameterParser.ExtractParameterNames("SELECT 'O''Brien :#x', :#y").Should().Equal("y");

    [Fact]
    public void SkipsLineAndBlockComments() =>
        SqlParameterParser.ExtractParameterNames("SELECT :#a -- :#b\n, /* :#c */ :#d").Should().Equal("a", "d");

    [Fact]
    public void SkipsDoubleQuotedAndBacktickIdentifiers() =>
        SqlParameterParser.ExtractParameterNames("SELECT \"col:#x\", `c:#y`, :#z").Should().Equal("z");

    [Fact]
    public void SkipsDollarQuotedBodies() =>
        SqlParameterParser.ExtractParameterNames("SELECT $$ :#a $$, $fn$ :#b $fn$, :#c").Should().Equal("c");

    [Fact]
    public void PostgresPositionalParameter_IsNotADollarQuote() =>
        SqlParameterParser.ExtractParameterNames("SELECT $1, :#x, $2").Should().Equal("x");

    [Fact]
    public void UnterminatedLiteral_HidesTheRest() =>
        SqlParameterParser.ExtractParameterNames("SELECT :#a, 'open :#b").Should().Equal("a");

    // ── Occurrences and rewrite ─────────────────────────────────────

    [Fact]
    public void FindPlaceholders_InOrder_WithRepeats_AndPositions()
    {
        var placeholders = SqlParameterParser.FindPlaceholders("a = :#a OR b = :#bb OR c = :#a::int");

        placeholders.Select(p => (p.Name, p.Offset, p.Length)).Should().Equal(("a", 4, 3), ("bb", 15, 4), ("a", 27, 3));
    }

    [Theory]
    [InlineData("SELECT :#${header.x}")]
    [InlineData("SELECT * FROM t WHERE id IN (:#in:ids)")]
    [InlineData("SELECT :# x")]
    public void FindPlaceholders_UnsupportedForms_Throw(string sql)
    {
        var act = () => SqlParameterParser.FindPlaceholders(sql);

        act.Should().Throw<InvalidOperationException>().WithMessage("*:#*");
    }

    [Fact]
    public void FindPlaceholders_NamedInWithCast_IsAPlaceholder() =>
        SqlParameterParser.FindPlaceholders("SELECT :#in::int").Should().ContainSingle().Which.Name.Should().Be("in");

    [Fact]
    public void BackslashEscapes_EscapedQuoteStaysInsideTheLiteral() =>
        SqlParameterParser.FindPlaceholders(@"VALUES ('it\'s :#a', "":#b \"" :#c"", :#d)", backslashEscapes: true)
            .Select(p => p.Name).Should().Equal("d");

    [Fact]
    public void BackslashEscapes_EscapedBackslashBeforeTheClosingQuote() =>
        SqlParameterParser.FindPlaceholders(@"VALUES ('C:\\', :#a)", backslashEscapes: true)
            .Should().ContainSingle().Which.Name.Should().Be("a");

    [Fact]
    public void BackslashEscapes_BackQuotedIdentifier_BackslashIsOrdinary() =>
        SqlParameterParser.FindPlaceholders(@"SELECT `c\`, :#a", backslashEscapes: true)
            .Should().ContainSingle().Which.Name.Should().Be("a");

    [Fact]
    public void BackslashEscapes_Default_BackslashIsOrdinary() =>
        SqlParameterParser.FindPlaceholders(@"VALUES ('C:\', :#a)")
            .Should().ContainSingle().Which.Name.Should().Be("a");

    [Theory]
    [InlineData(SqlPlaceholderStyle.At, "WHERE a = @a AND n = ':#a' -- :#a\nOR b = @b OR c = @a")]
    [InlineData(SqlPlaceholderStyle.Colon, "WHERE a = :a AND n = ':#a' -- :#a\nOR b = :b OR c = :a")]
    [InlineData(SqlPlaceholderStyle.Question, "WHERE a = ? AND n = ':#a' -- :#a\nOR b = ? OR c = ?")]
    public void Rewrite_ReplacesOnlyPlaceholders(SqlPlaceholderStyle style, string expected)
    {
        const string sql = "WHERE a = :#a AND n = ':#a' -- :#a\nOR b = :#b OR c = :#a";

        SqlParameterParser.Rewrite(sql, SqlParameterParser.FindPlaceholders(sql), style).Should().Be(expected);
    }
}
