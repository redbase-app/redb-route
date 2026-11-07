using System.Globalization;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.TestKit;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// <c>format(value, pattern[, culture])</c> — the explicit locale escape hatch the V4 breaking change
/// points at: since <c>${...}</c> renders culture-invariant, this is how a route asks for local text on
/// purpose. It was documented (CHANGELOG, expressions guide) before it existed; these tests are the
/// contract.
/// </summary>
[Collection("ExpressionResolver")]
public class FormatFunctionTests
{
    private static IExchange Exchange(params (string Name, object? Value)[] headers)
    {
        var exchange = redb.Route.Core.Exchange.Create(new Message("body"), null);
        foreach (var (name, value) in headers) exchange.In.Headers[name] = value;
        return exchange;
    }

    private static object? Eval(string expression, IExchange exchange)
        => new StringExpression(expression).Evaluate<object>(exchange);

    [Fact]
    public void NamedCulture_FormatsForThatLocale()
    {
        var exchange = Exchange(("price", 19.95m));

        Eval("${format(header.price, 'N2', 'ru-RU')}", exchange).Should().Be("19,95");
        Eval("${format(header.price, 'N2', 'en-US')}", exchange).Should().Be("19.95");
    }

    [Fact]
    public void WithoutACulture_TheResultIsInvariant()
    {
        var exchange = Exchange(("price", 19.95m));
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            Eval("${format(header.price, 'N2')}", exchange).Should().Be("19.95", "the ambient culture of the server never decides");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Dates_UseTheSameFunction()
    {
        var exchange = Exchange(("when", new DateTime(2026, 9, 1, 10, 30, 0, DateTimeKind.Utc)));

        Eval("${format(header.when, 'dd.MM.yyyy', 'ru-RU')}", exchange).Should().Be("01.09.2026");
    }

    [Fact]
    public void AStringThatHoldsANumber_IsParsedInvariantlyFirst()
    {
        var exchange = Exchange(("price", "19.95"));

        Eval("${format(header.price, 'N2', 'ru-RU')}", exchange).Should().Be("19,95");
    }

    [Fact]
    public void AStringThatIsJustText_ComesBackAsItself()
    {
        var exchange = Exchange(("name", "Ada"));

        Eval("${format(header.name, 'N2')}", exchange).Should().Be("Ada");
    }

    [Fact]
    public void NullValue_IsNull()
        => Eval("${format(header.missing, 'N2')}", Exchange()).Should().BeNull();

    [Fact]
    public void AnUnknownCultureName_FailsAndSaysSo()
    {
        var exchange = Exchange(("price", 1m));

        var act = () => Eval("${format(header.price, 'N2', 'kl-INGON')}", exchange);

        act.Should().Throw<Exception>().WithMessage("*culture*");
    }

    [Fact]
    public async Task InsideARoute_TheResultIsTheLocalText()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://fmt").SetBody(new StringExpression("Итого: ${format(header.total, 'N2', 'ru-RU')} ₽")).To("mock://fmt"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://fmt", null, "total", 1234.5m);

        // Built from the culture, not typed out: the group separator of ru-RU is a no-break space whose
        // exact code point is an ICU detail, and the test is about the mechanism, not about that.
        var expected = $"Итого: {1234.5m.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} ₽";
        ctx.Mock("mock://fmt").ReceivedExchanges[0].In.Body.Should().Be(expected);
        expected.Should().Contain("234,50", "ru-RU uses a comma for the decimal separator");
    }
}
