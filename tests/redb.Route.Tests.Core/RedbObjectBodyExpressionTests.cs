using redb.Core.Models.Entities;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Core;

/// <summary>
/// A <see cref="RedbObject{TProps}"/> body carries both <c>Id</c> and the JSON-named <c>id</c>: <c>${body.Id}</c> used to
/// fail with "Ambiguous match found" because the member lookup ignored case.
/// </summary>
public sealed class RedbObjectBodyExpressionTests
{
    public sealed class SensorProps
    {
        public string Room { get; set; } = "";
    }

    private static object? Evaluate(string expression)
    {
        var body = new RedbObject<SensorProps> { Props = new SensorProps { Room = "kitchen" } };
        body.Id = 1021;
        return new StringExpression(expression).Evaluate<object>(new Exchange(new Message(body)));
    }

    [Theory]
    [InlineData("${body.Id}")]
    [InlineData("${body.id}")]
    public void The_id_reads_in_either_spelling(string expression)
        => Evaluate(expression).Should().Be(1021L);

    [Fact]
    public void The_props_read_through_the_body()
        => Evaluate("${body.Props.Room}").Should().Be("kitchen");
}
