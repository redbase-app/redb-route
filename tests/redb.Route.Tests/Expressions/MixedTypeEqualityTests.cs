using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Equality of numbers of different types, and of a number with a numeric string (findings register row 31): equal
/// only when the values are equal. Until the fix a mixed pair was equal whenever the left value was the smaller one.
/// Covered for ==, !=, in, not in and the collection contains(), each pair equal and unequal, in both orders.
/// </summary>
public sealed class MixedTypeEqualityTests
{
    /// <summary>Mixed pairs: the left header value, the right header value, whether they are equal.</summary>
    public static TheoryData<object, object, bool> Pairs => new()
    {
        { 3, 3.0, true }, { 3, 5.0, false }, { 5, 3.0, false },
        { 3, 3m, true }, { 3, 5m, false }, { 5, 3m, false },
        { 3L, 3, true }, { 3L, 5, false }, { 5L, 3, false },
        { 3, "3", true }, { 3, "5", false }, { 5, "3", false },
        { 2.5, "2.5", true }, { 2.5, "7.5", false }, { 7.5, "2.5", false },
        { 2.5m, "2.50", true }, { 2.5m, "7.5", false },
        { 9007199254740993L, 9007199254740992m, false }, // beyond double precision: decimal keeps the values apart
        { 0.1, 0.1f, false },                            // a float widens to a double that is not 0.1
    };

    private static Exchange Message(object left, object right)
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["l"] = left;
        exchange.In.Headers["r"] = right;
        exchange.In.Headers["list"] = new List<object> { right };
        return exchange;
    }

    private static bool Condition(string condition, Exchange exchange) => PredicateFactory.FromString(condition).Matches(exchange);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Equals_BothOrders(object left, object right, bool equal)
    {
        Condition("header.l == header.r", Message(left, right)).Should().Be(equal);
        Condition("header.r == header.l", Message(left, right)).Should().Be(equal);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void NotEquals_BothOrders(object left, object right, bool equal)
    {
        Condition("header.l != header.r", Message(left, right)).Should().Be(!equal);
        Condition("header.r != header.l", Message(left, right)).Should().Be(!equal);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void In_AndNotIn(object left, object right, bool equal)
    {
        Condition("header.l in header.list", Message(left, right)).Should().Be(equal);
        Condition("header.l not in header.list", Message(left, right)).Should().Be(!equal);
    }

    [Theory]
    [InlineData("${header.ints.contains('5')}", false)]
    [InlineData("${header.ints.contains('3')}", true)]
    [InlineData("${header.doubles.contains(1)}", false)]
    [InlineData("${header.doubles.contains(7)}", true)]
    public void CollectionContains(string template, bool expected)
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["ints"] = new List<int> { 3 };
        exchange.In.Headers["doubles"] = new List<double> { 7.0 };

        new StringExpression(template).Evaluate<object>(exchange).Should().Be(expected);
    }

    [Theory]
    [InlineData("header.b == 5.0", false)]
    [InlineData("header.b == 3.0", true)]
    [InlineData("header.b == '5'", false)]
    [InlineData("header.b == '3'", true)]
    [InlineData("header.b in (5.0, '7')", false)]
    [InlineData("header.b in (5.0, '3')", true)]
    public void AgainstLiterals(string condition, bool expected)
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["b"] = 3;

        Condition(condition, exchange).Should().Be(expected);
    }
}
