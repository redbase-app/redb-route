using System.Text.Json.Nodes;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Core;

/// <summary>
/// A double or a float that becomes a decimal is the number that was written, on every runtime: the explicit
/// conversion rounded to 15 digits up to .NET 10 (12345678901234.56 became 12345678901234.6) and gives the exact
/// binary value from .NET 11 on (19.95 became 19.949999999999999289457264240).
/// </summary>
public class RouteDecimalTests
{
    [Theory]
    [InlineData(19.95, "19.95")]
    [InlineData(2.57, "2.57")]
    [InlineData(0.1, "0.1")]
    [InlineData(12345678901234.56, "12345678901234.56")]
    [InlineData(-1e-5, "-0.00001")]
    [InlineData(123456789.0, "123456789")]
    public void ADouble_BecomesTheDecimalItWasWrittenAs(double value, string expected)
    {
        RouteDecimal.FromDouble(value).Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        RouteDecimal.From(value).Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        RouteDecimal.ChangeType(value, typeof(decimal)).Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AFloat_BecomesItsOwnShortestText_NotThatOfItsWideningToDouble()
    {
        RouteDecimal.FromSingle(19.95f).Should().Be(19.95m);
        RouteDecimal.From(0.1f).Should().Be(0.1m);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e30)]
    public void NoDecimalValue_Throws_AsTheExplicitConversionDoes(double value)
    {
        var act = () => RouteDecimal.FromDouble(value);

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void EveryOtherPair_ConvertsAsConvertChangeTypeDoes()
    {
        RouteDecimal.ChangeType(42, typeof(decimal)).Should().Be(42m);
        RouteDecimal.ChangeType("19.95", typeof(decimal), System.Globalization.CultureInfo.InvariantCulture).Should().Be(19.95m);
        RouteDecimal.ChangeType(19.95m, typeof(double)).Should().Be(19.95);
        RouteDecimal.ChangeType(7L, typeof(int)).Should().Be(7);
    }

    [Theory]
    [InlineData("19.95", "19.95")]
    [InlineData("12345678901234.56", "12345678901234.56")]
    public void AJsonPathNumberReadAsDecimal_IsTheNumberInTheDocument(string number, string expected)
    {
        var exchange = new Exchange(new Message(JsonNode.Parse($$"""{ "price": {{number}} }""")));

        new JsonPathExpression("$.price").Evaluate<decimal>(exchange)
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnExpressionDoubleConvertedToDecimal_IsTheNumberItPrints()
    {
        new StringExpression("round(2.567, 2)").Evaluate<decimal>(new Exchange()).Should().Be(2.57m);
    }
}
