using System.Globalization;
using redb.Route.Abstractions;
using redb.Route.Expressions.Ast;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Route-XML Ф1.5: the three language additions the Ф0.3 exercise asked for — the modulo operator
/// <c>%</c>, <c>uuid([format])</c> and <c>datediff(a, b, unit)</c>. Every form is exercised in BOTH
/// engine branches (compiled via <see cref="StringExpression"/>, interpreted via the AST directly),
/// and the culture-sensitive paths are pinned invariant.
/// </summary>
[Collection("ExpressionResolver")]
public class LanguageAdditionsF15Tests
{
    private static IExchange Exchange(params (string Name, object? Value)[] headers)
    {
        var exchange = redb.Route.Core.Exchange.Create(new Message("body"), null);
        foreach (var (name, value) in headers) exchange.In.Headers[name] = value;
        return exchange;
    }

    private static object? Compiled(string expression, IExchange exchange)
        => new StringExpression(expression).Evaluate<object>(exchange);

    private static object? Interpreted(string expression, IExchange exchange)
    {
        var tokens = new Tokenizer(expression).GetAllTokens();
        var node = new Parser(tokens).Parse();
        return node.Evaluate(exchange);
    }

    // ── % (modulo) ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("${header.a % 3}", 1)]
    [InlineData("${header.a%3}", 1)] // whitespace never carries meaning
    [InlineData("${(header.a + 2) % 4}", 0)]
    public void Modulo_Compiled_ComputesAndCollapsesWholeResultsToInt(string form, int expected)
    {
        Compiled(form, Exchange(("a", 10))).Should().Be(expected);
    }

    [Fact]
    public void Modulo_Interpreted_AgreesWithCompiled()
    {
        var exchange = Exchange(("a", 10));

        Interpreted("header.a % 3", exchange).Should().Be(1);
        Interpreted("header.a%3", exchange).Should().Be(1);
    }

    [Fact]
    public void Modulo_ByZero_YieldsNull_LikeDivision()
    {
        var exchange = Exchange(("a", 10), ("z", 0));

        Compiled("${header.a % header.z}", exchange).Should().BeNull();
        Interpreted("header.a % header.z", exchange).Should().BeNull();
    }

    [Fact]
    public void Modulo_FractionalResult_StaysDouble()
    {
        Compiled("${header.p % 2}", Exchange(("p", 5.5))).Should().Be(1.5);
    }

    [Fact]
    public void Modulo_InACondition_Partitions()
    {
        var even = Exchange(("n", 42));
        var odd = Exchange(("n", 41));
        var expr = new StringExpression("${header.n % 2 == 0}");

        expr.Evaluate<object>(even).Should().Be(true);
        expr.Evaluate<object>(odd).Should().Be(false);
    }

    [Fact]
    public void Modulo_IsCultureInvariant_OnDecimalOperands()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            Compiled("${header.p % 2.5}", Exchange(("p", 7.5))).Should().Be(0);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ── uuid() ───────────────────────────────────────────────────────────────

    [Fact]
    public void Uuid_Compiled_ProducesAParseableGuid_DefaultDFormat()
    {
        var text = Compiled("${uuid()}", Exchange())?.ToString();

        Guid.TryParseExact(text, "D", out _).Should().BeTrue($"got '{text}'");
    }

    [Fact]
    public void Uuid_CompactNFormat_OnRequest()
    {
        var text = Compiled("${uuid('N')}", Exchange())?.ToString();

        Guid.TryParseExact(text, "N", out _).Should().BeTrue($"got '{text}'");
    }

    [Fact]
    public void Uuid_IsImpure_EveryEvaluationYieldsANewValue()
    {
        var exchange = Exchange();
        var expr = new StringExpression("${uuid()}"); // one compiled delegate, evaluated twice

        expr.Evaluate<object>(exchange).Should().NotBe(expr.Evaluate<object>(exchange));
    }

    [Fact]
    public void Uuid_Interpreted_AgreesWithCompiled()
    {
        Guid.TryParseExact(Interpreted("uuid()", Exchange())?.ToString(), "D", out _).Should().BeTrue();
        Guid.TryParseExact(Interpreted("uuid('N')", Exchange())?.ToString(), "N", out _).Should().BeTrue();
    }

    // ── datediff(a, b, unit) ─────────────────────────────────────────────────

    [Fact]
    public void DateDiff_Compiled_FirstMinusSecond_InTheRequestedUnit()
    {
        var exchange = Exchange(
            ("later", new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc)),
            ("earlier", new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc)));

        Compiled("${datediff(header.later, header.earlier, 'hours')}", exchange).Should().Be(30);
        Compiled("${datediff(header.earlier, header.later, 'hours')}", exchange).Should().Be(-30);
        Compiled("${datediff(header.later, header.earlier, 'minutes')}", exchange).Should().Be(1800);
        Compiled("${datediff(header.later, header.earlier, 'ms')}", exchange).Should().Be(108000000);
    }

    [Fact]
    public void DateDiff_FractionalDays_StayDouble()
    {
        var exchange = Exchange(
            ("later", new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc)),
            ("earlier", new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc)));

        Compiled("${datediff(header.later, header.earlier, 'days')}", exchange).Should().Be(1.25);
    }

    [Fact]
    public void DateDiff_ParsesStringDates_Invariantly()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            Compiled("${datediff('2026-01-03', '2026-01-01', 'days')}", Exchange()).Should().Be(2);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DateDiff_UnknownUnitOrUnparsableDate_YieldsNull_LikeDateAdd()
    {
        Compiled("${datediff('2026-01-03', '2026-01-01', 'fortnights')}", Exchange()).Should().BeNull();
        Compiled("${datediff('not a date', '2026-01-01', 'days')}", Exchange()).Should().BeNull();
    }

    [Fact]
    public void DateDiff_Interpreted_AgreesWithCompiled()
    {
        Interpreted("datediff('2026-01-03', '2026-01-01', 'days')", Exchange()).Should().Be(2);
        Interpreted("datediff('2026-01-01', '2026-01-03', 'days')", Exchange()).Should().Be(-2);
    }
}
