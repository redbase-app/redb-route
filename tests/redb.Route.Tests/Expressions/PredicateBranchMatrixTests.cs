using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Characterisation matrix for the ways a string becomes a boolean in this library.
/// It is a permanent part of the suite: it pins down what each path does, so a change to one of
/// them shows up here instead of surfacing as a silently mis-routed message.
/// <list type="bullet">
///   <item><b>Value position</b> — <see cref="StringExpression"/> plus the DSL truthiness rule.
///     This is what <c>Expr(...)</c> in <c>SetBody</c> / <c>SetHeader</c> does: an explicit
///     expression, evaluated and read for truthiness. Since 2026-08-28 it reads operators
///     whatever the spacing, like every other position.</item>
///   <item><b>Condition position</b> — <see cref="PredicateFactory"/>, what <c>Filter</c>,
///     <c>When</c>, <c>LoopWhile</c> and <c>Validate</c> use for a string. A <c>${...}</c>
///     template stays a template; anything else is compiled by the AST parser.</item>
/// </list>
/// </summary>
[Collection("ExpressionResolver")]
public class PredicateBranchMatrixTests
{
    /// <summary>What a path did with a condition.</summary>
    public enum Outcome
    {
        /// <summary>The path produced false.</summary>
        False,

        /// <summary>The path produced true.</summary>
        True,

        /// <summary>The path refused to compile the condition.</summary>
        Throws
    }

    private static IExchange CreateExchange()
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["a"] = 42;
        exchange.In.Headers["b"] = 3;
        exchange.In.Headers["flag"] = true;
        exchange.In.Headers["s"] = "hello";
        exchange.In.Headers["obj"] = new List<int> { 1, 2, 3, 4, 5 };
        exchange.Properties["count"] = 7;
        exchange.Properties["flag"] = false;
        exchange.Properties["items"] = new List<string> { "p", "q", "r" };
        exchange.Properties["name"] = "abcd";
        return exchange;
    }

    /// <summary>
    /// condition, value position, condition position, the answer a reader of the condition would
    /// expect. The hand-written logical branch used to be a fourth column; it was removed with
    /// the branch itself on 2026-08-28 (one language, one parser).
    /// </summary>
    public static TheoryData<string, Outcome, Outcome, Outcome> Matrix() => new()
    {
        //                                             value          condition      expected
        { "header.a > 10",                             Outcome.True,  Outcome.True,  Outcome.True },
        { "header.a>10",                               Outcome.True,  Outcome.True,  Outcome.True },
        { "header.a >= 10",                            Outcome.True,  Outcome.True,  Outcome.True },
        { "header.a>=10",                              Outcome.True,  Outcome.True,  Outcome.True },
        { "header.a == 'x'",                           Outcome.False, Outcome.False, Outcome.False },
        { "header.a=='x'",                             Outcome.False, Outcome.False, Outcome.False },
        { "header.a != 'x'",                           Outcome.True,  Outcome.True,  Outcome.True },
        { "header.a>10 AND header.b<5",                Outcome.True,  Outcome.True,  Outcome.True },
        { "header.a > 10 AND header.b < 5",            Outcome.True,  Outcome.True,  Outcome.True },
        { "property.count > 5 OR property.flag",       Outcome.True,  Outcome.True,  Outcome.True },
        { "count(property.items) > 2",                 Outcome.True,  Outcome.True,  Outcome.True },
        { "count(property.items)>2",                   Outcome.True,  Outcome.True,  Outcome.True },
        { "length(property.name) > 3 ? true : false",  Outcome.True,  Outcome.True,  Outcome.True },
        { "NOT header.flag",                           Outcome.False, Outcome.False, Outcome.False },
        { "!header.flag",                              Outcome.False, Outcome.False, Outcome.False },
        { "header.flag",                               Outcome.True,  Outcome.True,  Outcome.True },
        { "header.missing",                            Outcome.False, Outcome.False, Outcome.False },
        { "${header.flag}",                            Outcome.True, Outcome.True,  Outcome.True },
        { "header.a\t>\t10",                           Outcome.True,  Outcome.True,  Outcome.True },
        { "header.a  >  10",                           Outcome.True,  Outcome.True,  Outcome.True },
        { "(header.a > 10) AND (header.b < 5)",        Outcome.True, Outcome.True,  Outcome.True },
        { "(header.a>10) AND (header.b<5)",            Outcome.True, Outcome.True,  Outcome.True },
        { "header.s == 'a > b'",                       Outcome.False, Outcome.False, Outcome.False },
        { "header.s == 'a AND b'",                     Outcome.False, Outcome.False, Outcome.False },
        { "contains(header.s, 'ell')",                 Outcome.True, Outcome.True,  Outcome.True },
        { "header.a + header.b > 40",                  Outcome.True, Outcome.True,  Outcome.True },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ValuePosition_MatchesRecordedBehaviour(string condition, Outcome value, Outcome _, Outcome __)
        => Run(() => RouteTruthiness.ToBoolean(new StringExpression(condition).Evaluate<object?>(CreateExchange())))
            .Should().Be(value);

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ConditionPosition_MatchesRecordedBehaviour(string condition, Outcome _, Outcome actual, Outcome __)
        => Run(() => PredicateFactory.FromString(condition).Matches(CreateExchange()))
            .Should().Be(actual);

    /// <summary>
    /// The point of the whole exercise: the condition position answers what a reader of the
    /// condition expects, for every row of the matrix.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void ConditionPosition_AnswersWhatTheConditionSays(string condition, Outcome _, Outcome actual, Outcome expected)
    {
        actual.Should().Be(expected,
            "the condition position is the one path that has to be right; " +
            $"'{condition}' is documented as {actual} but reads as {expected}");

        Run(() => PredicateFactory.FromString(condition).Matches(CreateExchange())).Should().Be(expected);
    }

    /// <summary>
    /// A comparison against a CLR member behind a header or property resolves through the smart
    /// resolvers (literal name first, then the member path) in the condition position. This used
    /// to be an AST gap — the identifier resolver did a flat dictionary lookup of the whole
    /// dotted tail — and this test pinned the gap until it was closed; now it pins the fix.
    /// </summary>
    [Theory]
    [InlineData("header.obj.Count > 3")]
    [InlineData("property.items.Count > 2")]
    [InlineData("header.s.Length > 3")]
    public void MemberAccessOnObject_IsResolvedInTheConditionPosition(string condition)
        => Run(() => PredicateFactory.FromString(condition).Matches(CreateExchange())).Should().Be(Outcome.True);

    private static Outcome Run(Func<bool> evaluate)
    {
        try
        {
            return evaluate() ? Outcome.True : Outcome.False;
        }
        catch (Exception)
        {
            return Outcome.Throws;
        }
    }
}
