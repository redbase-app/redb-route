using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// A path step names a member case-insensitively, and the exact case wins when several members differ only in case. A
/// redb object has both <c>Id</c> and the JSON-named <c>id</c>: <c>${body.Id}</c> failed with "Ambiguous match found".
/// </summary>
[Collection("ExpressionResolver")]
public class MemberNameCaseTests : IDisposable
{
    public MemberNameCaseTests() => ExpressionResolver.ClearAllCaches();

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
        GC.SuppressFinalize(this);
    }

    private static object? Evaluate(string expression, object body)
        => new StringExpression(expression).Evaluate<object>(new Exchange(new Message(body)));

    /// <summary>The shape of a redb object: a property and its JSON-named twin, and the props.</summary>
    public sealed class TwinNamed
    {
#pragma warning disable IDE1006 // the lower-case name is the point of the test
        public long id { get; set; } = 7;
#pragma warning restore IDE1006
        public long Id { get => id + 1000; set => id = value; }
        public Reading Props { get; set; } = new();
        public string Status = "open";
        public string status = "closed";
    }

    public sealed class Reading
    {
        public string Room { get; set; } = "kitchen";
    }

    public sealed class Plain
    {
        public long Id { get; set; } = 42;
    }

    [Theory]
    [InlineData("${body.Id}", 1007L)]
    [InlineData("${body.id}", 7L)]
    [InlineData("body.Id", 1007L)]
    [InlineData("body.id", 7L)]
    public void The_exact_case_picks_one_of_two_members_that_differ_only_in_case(string expression, long expected)
        => Evaluate(expression, new TwinNamed()).Should().Be(expected);

    [Fact]
    public void A_nested_step_goes_through_the_twin_named_type()
        => Evaluate("${body.Props.Room}", new TwinNamed()).Should().Be("kitchen");

    [Theory]
    [InlineData("${body.Status}", "open")]
    [InlineData("${body.status}", "closed")]
    public void Fields_follow_the_same_rule(string expression, string expected)
        => Evaluate(expression, new TwinNamed()).Should().Be(expected);

    [Theory]
    [InlineData("${body.Id}")]
    [InlineData("${body.id}")]
    [InlineData("${body.ID}")]
    public void A_single_member_is_found_in_any_case(string expression)
        => Evaluate(expression, new Plain()).Should().Be(42L);

    [Fact]
    public void A_case_that_matches_neither_twin_exactly_fails_naming_both()
    {
        var evaluate = () => Evaluate("${body.ID}", new TwinNamed());

        evaluate.Should().Throw<Exception>().Where(e => Describe(e).Contains("Id, id") || Describe(e).Contains("id, Id"));
    }

    private static string Describe(Exception e) => e.InnerException is null ? e.Message : e.Message + " " + Describe(e.InnerException);
}
