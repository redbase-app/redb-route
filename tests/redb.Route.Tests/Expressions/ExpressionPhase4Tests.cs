using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Tests for Expression Language Phase 4 features:
/// String functions (contains, startsWith, endsWith, replace),
/// Date functions (now, dateFormat, dateAdd),
/// Aggregate functions (sum, avg, count).
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionPhase4Tests : IDisposable
{
    public ExpressionPhase4Tests()
    {
        ExpressionResolver.ClearAllCaches();
    }

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
    }

    private static IExchange CreateExchange(object? body = null)
        => new Exchange(new Message(body));

    // ═══════════════════════════════════════════════════
    // CONTAINS()
    // ═══════════════════════════════════════════════════

    #region contains()

    [Fact]
    public void Contains_Found()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("contains(property.text, 'world')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void Contains_NotFound()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("contains(property.text, 'xyz')", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void Contains_NullProperty()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("contains(property.missing, 'test')", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void Contains_CaseInsensitive()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("contains(property.text, 'HELLO')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void Contains_InLogicalExpression()
    {
        var exchange = CreateExchange();
        exchange.Properties["name"] = "alice.smith";
        var result = ExpressionResolver.ResolveExpression("contains(property.name, '.') == true", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void Contains_LiteralStrings()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("contains('hello world', 'world')", exchange);
        result.Should().Be(true);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // STARTSWITH()
    // ═══════════════════════════════════════════════════

    #region startsWith()

    [Fact]
    public void StartsWith_True()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("startsWith(property.text, 'hello')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void StartsWith_False()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("startsWith(property.text, 'world')", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void StartsWith_NullProperty()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("startsWith(property.missing, 'test')", exchange);
        result.Should().Be(false);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // ENDSWITH()
    // ═══════════════════════════════════════════════════

    #region endsWith()

    [Fact]
    public void EndsWith_True()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("endsWith(property.text, 'world')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void EndsWith_False()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("endsWith(property.text, 'hello')", exchange);
        result.Should().Be(false);
    }

    [Fact]
    public void EndsWith_NullProperty()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("endsWith(property.missing, 'test')", exchange);
        result.Should().Be(false);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // REPLACE()
    // ═══════════════════════════════════════════════════

    #region replace()

    [Fact]
    public void Replace_Simple()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("replace(property.text, 'World', 'Earth')", exchange);
        result.Should().Be("Hello Earth");
    }

    [Fact]
    public void Replace_MultipleOccurrences()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "aaa-bbb-aaa";
        var result = ExpressionResolver.ResolveExpression("replace(property.text, 'aaa', 'xxx')", exchange);
        result.Should().Be("xxx-bbb-xxx");
    }

    [Fact]
    public void Replace_NullProperty()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("replace(property.missing, 'old', 'new')", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void Replace_InTemplate()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("Result: ${replace(property.text, 'World', 'Earth')}", exchange);
        result.Should().Be("Result: Hello Earth");
    }

    [Fact]
    public void Replace_LiteralStrings()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("replace('foo-bar-baz', '-', '/')", exchange);
        result.Should().Be("foo/bar/baz");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // NOW()
    // ═══════════════════════════════════════════════════

    #region now()

    [Fact]
    public void Now_ReturnsDateTime()
    {
        var exchange = CreateExchange();
        var before = DateTime.UtcNow;
        var result = ExpressionResolver.ResolveExpression("now()", exchange);
        var after = DateTime.UtcNow;

        result.Should().BeOfType<DateTime>();
        var dt = (DateTime)result!;
        dt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Now_InTemplate()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("Time: ${now()}", exchange)?.ToString();
        result.Should().StartWith("Time: ");
        result!.Length.Should().BeGreaterThan(6);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // DATEFORMAT()
    // ═══════════════════════════════════════════════════

    #region dateFormat()

    [Fact]
    public void DateFormat_DateTime()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 6, 14, 30, 0);
        var result = ExpressionResolver.ResolveExpression("dateFormat(property.date, 'yyyy-MM-dd')", exchange);
        result.Should().Be("2026-03-06");
    }

    [Fact]
    public void DateFormat_CustomFormat()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 6, 14, 30, 0);
        var result = ExpressionResolver.ResolveExpression("dateFormat(property.date, 'dd.MM.yyyy HH:mm')", exchange);
        result.Should().Be("06.03.2026 14:30");
    }

    [Fact]
    public void DateFormat_StringDate()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = "2026-03-06";
        var result = ExpressionResolver.ResolveExpression("dateFormat(property.date, 'dd/MM/yyyy')", exchange);
        result.Should().Be("06/03/2026");
    }

    [Fact]
    public void DateFormat_InTemplate()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 6);
        var result = ExpressionResolver.ResolveExpression("Date: ${dateFormat(property.date, 'yyyy-MM-dd')}", exchange);
        result.Should().Be("Date: 2026-03-06");
    }

    [Fact]
    public void DateFormat_DateTimeOffset()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTimeOffset(2026, 3, 6, 14, 30, 0, TimeSpan.FromHours(3));
        var result = ExpressionResolver.ResolveExpression("dateFormat(property.date, 'yyyy-MM-dd')", exchange);
        result.Should().Be("2026-03-06");
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // DATEADD()
    // ═══════════════════════════════════════════════════

    #region dateAdd()

    [Fact]
    public void DateAdd_Days()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 1);
        var result = ExpressionResolver.ResolveExpression("dateAdd(property.date, 5, 'days')", exchange);
        result.Should().Be(new DateTime(2026, 3, 6));
    }

    [Fact]
    public void DateAdd_Hours()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 6, 10, 0, 0);
        var result = ExpressionResolver.ResolveExpression("dateAdd(property.date, 3, 'hours')", exchange);
        result.Should().Be(new DateTime(2026, 3, 6, 13, 0, 0));
    }

    [Fact]
    public void DateAdd_Months()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 1, 15);
        var result = ExpressionResolver.ResolveExpression("dateAdd(property.date, 2, 'months')", exchange);
        result.Should().Be(new DateTime(2026, 3, 15));
    }

    [Fact]
    public void DateAdd_NegativeDays()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 6);
        var result = ExpressionResolver.ResolveExpression("dateAdd(property.date, -1, 'days')", exchange);
        result.Should().Be(new DateTime(2026, 3, 5));
    }

    [Fact]
    public void DateAdd_Minutes()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 6, 10, 0, 0);
        var result = ExpressionResolver.ResolveExpression("dateAdd(property.date, 30, 'minutes')", exchange);
        result.Should().Be(new DateTime(2026, 3, 6, 10, 30, 0));
    }

    [Fact]
    public void DateAdd_Years()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 6);
        var result = ExpressionResolver.ResolveExpression("dateAdd(property.date, 1, 'years')", exchange);
        result.Should().Be(new DateTime(2027, 3, 6));
    }

    [Fact]
    public void DateAdd_NullDate_ReturnsNull()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("dateAdd(property.missing, 1, 'days')", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void DateAdd_StringDate()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = "2026-03-01";
        var result = ExpressionResolver.ResolveExpression("dateAdd(property.date, 5, 'days')", exchange);
        result.Should().Be(new DateTime(2026, 3, 6));
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // SUM()
    // ═══════════════════════════════════════════════════

    #region sum()

    [Fact]
    public void Sum_IntList()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<int> { 1, 2, 3, 4, 5 };
        var result = ExpressionResolver.ResolveExpression("sum(property.items)", exchange);
        Convert.ToDouble(result).Should().Be(15);
    }

    [Fact]
    public void Sum_DoubleArray()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new double[] { 1.5, 2.5, 3.0 };
        var result = ExpressionResolver.ResolveExpression("sum(property.items)", exchange);
        Convert.ToDouble(result).Should().Be(7.0);
    }

    [Fact]
    public void Sum_NullProperty()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("sum(property.missing)", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void Sum_EmptyList()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<int>();
        var result = ExpressionResolver.ResolveExpression("sum(property.items)", exchange);
        Convert.ToDouble(result).Should().Be(0);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // AVG()
    // ═══════════════════════════════════════════════════

    #region avg()

    [Fact]
    public void Avg_IntList()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<int> { 2, 4, 6 };
        var result = ExpressionResolver.ResolveExpression("avg(property.items)", exchange);
        Convert.ToDouble(result).Should().Be(4);
    }

    [Fact]
    public void Avg_DoubleArray()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new double[] { 1.0, 2.0, 3.0 };
        var result = ExpressionResolver.ResolveExpression("avg(property.items)", exchange);
        Convert.ToDouble(result).Should().Be(2.0);
    }

    [Fact]
    public void Avg_NullProperty()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("avg(property.missing)", exchange);
        result.Should().BeNull();
    }

    [Fact]
    public void Avg_EmptyList()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<int>();
        var result = ExpressionResolver.ResolveExpression("avg(property.items)", exchange);
        result.Should().BeNull();
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // COUNT()
    // ═══════════════════════════════════════════════════

    #region count()

    [Fact]
    public void Count_List()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<string> { "a", "b", "c" };
        var result = ExpressionResolver.ResolveExpression("count(property.items)", exchange);
        Convert.ToInt32(result).Should().Be(3);
    }

    [Fact]
    public void Count_Array()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new int[] { 10, 20 };
        var result = ExpressionResolver.ResolveExpression("count(property.items)", exchange);
        Convert.ToInt32(result).Should().Be(2);
    }

    [Fact]
    public void Count_EmptyList()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<int>();
        var result = ExpressionResolver.ResolveExpression("count(property.items)", exchange);
        Convert.ToInt32(result).Should().Be(0);
    }

    [Fact]
    public void Count_NullProperty()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("count(property.missing)", exchange);
        Convert.ToInt32(result).Should().Be(0);
    }

    [Fact]
    public void Count_SingleValue()
    {
        var exchange = CreateExchange();
        exchange.Properties["val"] = 42;
        var result = ExpressionResolver.ResolveExpression("count(property.val)", exchange);
        Convert.ToInt32(result).Should().Be(1);
    }

    #endregion

    // ═══════════════════════════════════════════════════
    // COMBINED / CROSS-FEATURE EXPRESSIONS
    // ═══════════════════════════════════════════════════

    #region Combined expressions

    [Fact]
    public void Contains_WithNot()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression("NOT contains(property.text, 'xyz')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void Replace_WithUpper()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "hello world";
        var result = ExpressionResolver.ResolveExpression("upper(replace(property.text, 'world', 'earth'))", exchange);
        result.Should().Be("HELLO EARTH");
    }

    [Fact]
    public void DateFormat_Now()
    {
        var exchange = CreateExchange();
        var result = ExpressionResolver.ResolveExpression("dateFormat(now(), 'yyyy')", exchange);
        result.Should().Be(DateTime.UtcNow.Year.ToString());
    }

    [Fact]
    public void Sum_InComparison()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<int> { 1, 2, 3 };
        var result = ExpressionResolver.ResolveExpression("sum(property.items) > 5", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void Count_WithTernary()
    {
        var exchange = CreateExchange();
        exchange.Properties["items"] = new List<int> { 1, 2, 3 };
        var result = ExpressionResolver.ResolveExpression("count(property.items) > 2 ? 'many' : 'few'", exchange);
        result.Should().Be("many");
    }

    [Fact]
    public void Avg_InArithmetic()
    {
        var exchange = CreateExchange();
        exchange.Properties["scores"] = new List<int> { 80, 90, 100 };
        var result = ExpressionResolver.ResolveExpression("avg(property.scores) + 10", exchange);
        Convert.ToDouble(result).Should().Be(100);
    }

    [Fact]
    public void StartsWith_AND_EndsWith()
    {
        var exchange = CreateExchange();
        exchange.Properties["file"] = "report.pdf";
        var result = ExpressionResolver.ResolveExpression(
            "startsWith(property.file, 'report') AND endsWith(property.file, '.pdf')", exchange);
        result.Should().Be(true);
    }

    [Fact]
    public void DateAdd_DateFormat_Chained()
    {
        var exchange = CreateExchange();
        exchange.Properties["date"] = new DateTime(2026, 3, 1);
        var result = ExpressionResolver.ResolveExpression(
            "dateFormat(dateAdd(property.date, 5, 'days'), 'yyyy-MM-dd')", exchange);
        result.Should().Be("2026-03-06");
    }

    [Fact]
    public void Replace_Template()
    {
        var exchange = CreateExchange();
        exchange.Properties["path"] = "/api/v1/users";
        var result = ExpressionResolver.ResolveExpression(
            "Path: ${replace(property.path, '/api/v1', '/api/v2')}", exchange);
        result.Should().Be("Path: /api/v2/users");
    }

    [Fact]
    public void Contains_Template()
    {
        var exchange = CreateExchange();
        exchange.Properties["text"] = "Hello World";
        var result = ExpressionResolver.ResolveExpression(
            "Has world: ${contains(property.text, 'world')}", exchange);
        result.Should().Be("Has world: True");
    }

    #endregion
}
