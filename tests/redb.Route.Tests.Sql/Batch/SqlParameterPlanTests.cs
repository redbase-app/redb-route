using redb.Route.Sql;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>The parameter plan of a statement: its <c>:#name</c> placeholders, the explicit values they use, and the text sent to the provider.</summary>
public sealed class SqlParameterPlanTests
{
    [Fact]
    public void Plan_NamesAndExplicitParams_Classified()
    {
        var plan = SqlParameterPlan.Create("INSERT INTO t (a, b, c) VALUES (:#a, :#b, :#c)",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = "constant",
                ["B"] = "${header.b}",
                ["unused"] = "x",
            });

        plan.Names.Should().Equal("a", "b", "c");
        plan.HasExpressions.Should().BeTrue();

        plan.TryGetExplicit("a", out var constant).Should().BeTrue();
        constant.IsExpression.Should().BeFalse();
        constant.Resolve(null).Should().Be("constant");

        plan.TryGetExplicit("b", out var expression).Should().BeTrue("explicit names match placeholders ignoring case");
        expression.IsExpression.Should().BeTrue();

        plan.TryGetExplicit("c", out _).Should().BeFalse();
        plan.TryGetExplicit("unused", out _).Should().BeFalse("a param.* the statement does not use is not part of the plan");
    }

    [Fact]
    public void Plan_RepeatedNameInAnotherCase_IsOneParameterInTheTextToo()
    {
        var plan = SqlParameterPlan.Create("UPDATE t SET a = :#Id WHERE id = :#id",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        plan.Names.Should().Equal("Id");
        plan.Slots.Should().Equal(0);
        plan.Sql.Should().Be("UPDATE t SET a = @Id WHERE id = @Id",
            "a provider that binds names case-sensitively (SQLite) must see the one name the parameter has");
    }

    [Fact]
    public void Plan_TextForTheProvider_UsesItsOwnPlaceholders()
    {
        var plan = SqlParameterPlan.Create("UPDATE t SET a = :#a, note = ':#a' WHERE id = @id",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        plan.Sql.Should().Be("UPDATE t SET a = @a, note = ':#a' WHERE id = @id",
            "only the placeholder is rewritten; a literal and the database's own @id stay as written");
        plan.Names.Should().Equal("a");
    }

    [Theory]
    [InlineData(SqlPlaceholderStyle.At, "a = @x OR b = @y OR c = @x", new[] { 0, 1 })]
    [InlineData(SqlPlaceholderStyle.Colon, "a = :x OR b = :y OR c = :x", new[] { 0, 1, 0 })]
    [InlineData(SqlPlaceholderStyle.Question, "a = ? OR b = ? OR c = ?", new[] { 0, 1, 0 })]
    public void Plan_Style_TextAndOneParameterPerNameOrOccurrence(SqlPlaceholderStyle style, string expectedSql, int[] expectedSlots)
    {
        const string sql = "a = :#x OR b = :#y OR c = :#x";

        var plan = SqlParameterPlan.Create(sql, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), style);

        plan.SourceSql.Should().Be(sql);
        plan.Sql.Should().Be(expectedSql);
        plan.Names.Should().Equal("x", "y");
        plan.Slots.Should().Equal(expectedSlots, "@name binds by name; :name and ? take a parameter for every occurrence");
    }

    [Fact]
    public void Plan_ConstantsOnly_HasNoExpressions_AndEmptyConstantIsNull()
    {
        var plan = SqlParameterPlan.Create("UPDATE t SET a = :#a",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a"] = "" });

        plan.HasExpressions.Should().BeFalse();
        plan.TryGetExplicit("a", out var empty).Should().BeTrue();
        empty.Resolve(null).Should().Be(DBNull.Value);
    }
}
