using System.Text;
using redb.Route.Expressions.Tokenizers;
using AwesomeAssertions;

namespace redb.Route.Tests.Expressions.Tokenizers;

public class JsonArrayTokenizerTests
{
    [Fact]
    public async Task PrimitiveArray_ThreeElements()
    {
        var result = await Collect(JsonArrayTokenizer.Tokenize("[1, 2, 3]")).ConfigureAwait(false);

        result.Should().Equal("1", "2", "3");
    }

    [Fact]
    public async Task ObjectArray_TwoRawJsonStrings()
    {
        const string json = "[{\"a\":1}, {\"b\":2}]";

        var result = await Collect(JsonArrayTokenizer.Tokenize(json)).ConfigureAwait(false);

        result.Should().HaveCount(2);
        ((string)result[0]!).Should().Contain("\"a\"");
        ((string)result[1]!).Should().Contain("\"b\"");
    }

    [Fact]
    public async Task EmptyArray_ZeroElements()
    {
        var result = await Collect(JsonArrayTokenizer.Tokenize("[]")).ConfigureAwait(false);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task NotArray_ThrowsInvalidOperation()
    {
        var act = async () => await Collect(JsonArrayTokenizer.Tokenize("{\"a\":1}")).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a JSON array*").ConfigureAwait(false);
    }

    [Fact]
    public async Task Stream_ParsesCorrectly()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("[10, 20]"));

        var result = await Collect(JsonArrayTokenizer.Tokenize(stream)).ConfigureAwait(false);

        result.Should().Equal("10", "20");
    }

    [Fact]
    public async Task ByteArray_ParsesCorrectly()
    {
        var bytes = Encoding.UTF8.GetBytes("[\"x\", \"y\"]");

        var result = await Collect(JsonArrayTokenizer.Tokenize(bytes)).ConfigureAwait(false);

        result.Should().Equal("\"x\"", "\"y\"");
    }

    [Fact]
    public async Task ScalarBody_IsNotAJsonArray()
    {
        var act = async () => await Collect(JsonArrayTokenizer.Tokenize(12345)).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a JSON array*").ConfigureAwait(false);
    }

    [Fact]
    public async Task NullBody_Throws()
    {
        var act = async () => await Collect(JsonArrayTokenizer.Tokenize(null)).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*body is null*").ConfigureAwait(false);
    }

    [Fact]
    public async Task JpathResult_ObjectArray_SplitsIntoElementJson()
    {
        // <setBody expr="${jpath('$.lines')}"/> then <split><tokenizeJsonArray/>: the body is the object[] jpath returns.
        var exchange = new redb.Route.Core.Exchange();
        exchange.In.Body = """{"id":"o-1","lines":[{"sku":"A-1","qty":1},{"sku":"B-2","qty":2}]}""";
        var lines = new redb.Route.Expressions.JsonPathExpression("$.lines").Evaluate<object>(exchange);

        var result = await Collect(JsonArrayTokenizer.Tokenize(lines)).ConfigureAwait(false);

        lines.Should().BeOfType<object[]>();
        result.Should().Equal("""{"sku":"A-1","qty":1}""", """{"sku":"B-2","qty":2}""");
    }

    [Fact]
    public async Task TypedScalarArray_SplitsLikeTheText()
    {
        var result = await Collect(JsonArrayTokenizer.Tokenize(new[] { 1, 2 })).ConfigureAwait(false);
        var strings = await Collect(JsonArrayTokenizer.Tokenize(new[] { "x", "y" })).ConfigureAwait(false);

        result.Should().Equal("1", "2");
        strings.Should().Equal("\"x\"", "\"y\"");
    }

    [Fact]
    public async Task JsonTree_SplitsAndKeepsNonAsciiAsWritten()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""[{"name":"Склад"},null]""");
        using var document = System.Text.Json.JsonDocument.Parse("""[{"name":"Склад"}]""");

        var fromNode = await Collect(JsonArrayTokenizer.Tokenize(node)).ConfigureAwait(false);
        var fromDocument = await Collect(JsonArrayTokenizer.Tokenize(document)).ConfigureAwait(false);

        fromNode.Should().Equal("""{"name":"Склад"}""", "null");
        fromDocument.Should().Equal("""{"name":"Склад"}""");
    }

    private static async Task<List<object?>> Collect(IAsyncEnumerable<object?> source)
    {
        var list = new List<object?>();
        await foreach (var item in source.ConfigureAwait(false))
            list.Add(item);
        return list;
    }
}
