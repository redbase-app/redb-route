using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The format suffix <c>${expr:FORMAT}</c> (findings register row 34 point 4, variant a): <c>format(expr, 'FORMAT')</c>
/// with the invariant culture, for a standard .NET format of one letter and up to two digits. Until 4.7.0 the suffix
/// was dropped silently (<c>${header.n:F4}</c> gave <c>2.5</c>). Namespaces and ternaries are not formats.
/// </summary>
public sealed class FormatSuffixTests : IDisposable
{
    private readonly string _env = "REDB_FMT_TEST_" + Guid.NewGuid().ToString("N");

    public FormatSuffixTests() => Environment.SetEnvironmentVariable(_env, "E");

    public void Dispose() => Environment.SetEnvironmentVariable(_env, null);

    private static Exchange Message()
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["n"] = 2.5;
        exchange.In.Headers["big"] = 1234567.891m;
        exchange.In.Headers["id"] = 42;
        exchange.In.Headers["flag"] = true;
        exchange.In.Headers["total"] = new Dictionary<string, object> { ["CostUsd"] = 0.125 };
        return exchange;
    }

    private static string Render(string template) => ExpressionResolver.ProcessTemplate(template, Message());

    [Theory]
    [InlineData("${header.n:F4}", "2.5000")]
    [InlineData("${header.n:F0}", "2")]
    [InlineData("${header.big:N2}", "1,234,567.89")]
    [InlineData("${header.id:D8}", "00000042")]
    [InlineData("${header.n * 2:F1}", "5.0")]
    [InlineData("cost cap hit: ${header.total.CostUsd:F4}", "cost cap hit: 0.1250")]
    [InlineData("${header.n : F2}", "2.50")]
    public void TheSuffix_Formats_Invariantly(string template, string expected)
    {
        Render(template).Should().Be(expected);
    }

    [Fact]
    public void AWholePlaceholder_GivesTheFormattedText()
    {
        new StringExpression("${header.n:F2}").Evaluate<object>(Message()).Should().Be("2.50");
    }

    [Fact]
    public void AMissingValue_RendersEmpty()
    {
        Render("[${header.none:F2}]").Should().Be("[]");
    }

    [Theory]
    [InlineData("${header.flag ? 'a' : 'b'}", "a")]
    [InlineData("${header.none ?? 'd'}", "d")]
    [InlineData("${'a:F2'}", "a:F2")]
    [InlineData("${format(header.n, 'F3')}", "2.500")]
    public void TernariesQuotesAndCalls_AreNotSuffixes(string template, string expected)
    {
        Render(template).Should().Be(expected);
    }

    [Fact]
    public void ANamespaceAgainstItsName_IsNotASuffix()
    {
        Render("${env:" + _env + "}").Should().Be("E");
    }

    [Theory]
    [InlineData("header.n:F4", true)]
    [InlineData("header.n:yyyy", false)]
    [InlineData("env:X", false)]
    [InlineData("file:name", false)]
    [InlineData("date:now:yyyyMMdd", false)]
    [InlineData("header.a ? b : c", false)]
    [InlineData("round(header.n):N0", true)]
    public void TheSplitRule(string text, bool split)
    {
        ExpressionResolver.TrySplitFormatSuffix(text, out _, out _).Should().Be(split);
    }
}
