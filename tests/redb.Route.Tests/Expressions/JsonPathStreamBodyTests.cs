using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>Code review 2026-09-01 (В6): a stream body can be evaluated more than once; the expression leaves the stream where it found it.</summary>
[Collection("ExpressionResolver")]
public class JsonPathStreamBodyTests
{
    [Fact]
    public void SeekableStreamBody_CanBeEvaluatedTwice()
    {
        var exchange = new Exchange(new Message(new MemoryStream("""{"id":"DRV-001","age":35}"""u8.ToArray())));

        new JsonPathExpression("$.id").Evaluate<string>(exchange).Should().Be("DRV-001");
        new JsonPathExpression("$.age").Evaluate<int>(exchange).Should().Be(35, "the first evaluation must leave the stream where it found it");
    }

    [Fact]
    public void SeekableStreamBody_IsReadFromItsCurrentPosition_AndThePositionIsRestored()
    {
        var stream = new MemoryStream("xx{\"id\":1}"u8.ToArray());
        stream.Position = 2;
        var exchange = new Exchange(new Message(stream));

        new JsonPathExpression("$.id").Evaluate<int>(exchange).Should().Be(1);
        stream.Position.Should().Be(2);
    }
}
