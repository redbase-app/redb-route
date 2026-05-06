using System.Text;
using redb.Route.Expressions.Tokenizers;
using FluentAssertions;

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
    public async Task UnsupportedBody_Throws()
    {
        var act = async () => await Collect(JsonArrayTokenizer.Tokenize(12345)).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unsupported body type*").ConfigureAwait(false);
    }

    private static async Task<List<object?>> Collect(IAsyncEnumerable<object?> source)
    {
        var list = new List<object?>();
        await foreach (var item in source.ConfigureAwait(false))
            list.Add(item);
        return list;
    }
}
