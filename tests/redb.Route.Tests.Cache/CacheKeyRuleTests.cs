using AwesomeAssertions;
using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Cache;

/// <summary>
/// The four shapes of a cache key (the cache key rule after findings register row 29): a template with ${...}; a constant
/// that starts with no root of the exchange, kept as written even with a dash, a slash, a colon or a space; an
/// expression over a root, evaluated; broken arithmetic over a root, the row 29 error.
/// </summary>
public sealed class CacheKeyRuleTests
{
    private static Exchange Exchange()
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["k"] = "a";
        exchange.In.Headers["customerId"] = 42;
        return exchange;
    }

    [Theory]
    [InlineData("${header.k}-x", "a-x")]
    [InlineData("customer-${header.customerId}", "customer-42")]
    public void ATemplate_IsRendered(string key, string expected)
    {
        CacheDefinition.KeyFromTemplate(key)(Exchange()).Should().Be(expected);
    }

    [Theory]
    [InlineData("orders-all")]
    [InlineData("k-out")]
    [InlineData("b:c")]
    [InlineData("reports/daily")]
    [InlineData("plain text")]
    [InlineData("2024-11")]
    [InlineData("k")]
    public void AConstant_IsTheKeyAsWritten(string key)
    {
        CacheDefinition.KeyFromTemplate(key)(Exchange()).Should().Be(key);
    }

    [Theory]
    [InlineData("header.customerId", "42")]
    [InlineData("header.customerId + 1", "43")]
    [InlineData("upper(header.k)", "A")]
    [InlineData("'quoted'", "quoted")]
    public void AnExpressionOverARoot_IsEvaluated(string key, string expected)
    {
        CacheDefinition.KeyFromTemplate(key)(Exchange()).Should().Be(expected);
    }

    [Theory]
    [InlineData("header.a+b")]
    [InlineData("body-cache")]
    public void BrokenArithmeticOverARoot_StaysAnError(string key)
    {
        var act = () => CacheDefinition.KeyFromTemplate(key);

        act.Should().Throw<ExpressionCompilationException>().WithMessage("*is not a value*");
    }
}
