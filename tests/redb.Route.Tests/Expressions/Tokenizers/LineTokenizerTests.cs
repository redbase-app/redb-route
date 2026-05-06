using System.Text;
using redb.Route.Expressions.Tokenizers;
using FluentAssertions;

namespace redb.Route.Tests.Expressions.Tokenizers;

public class LineTokenizerTests
{
    [Fact]
    public async Task Stream_ThreeLines_ThreeElements()
    {
        var stream = new MemoryStream("line1\nline2\nline3"u8.ToArray());

        var result = await Collect(LineTokenizer.Tokenize(stream, "\n", false)).ConfigureAwait(false);

        result.Should().Equal("line1", "line2", "line3");
    }

    [Fact]
    public async Task String_WithNewlines_SplitsCorrectly()
    {
        var result = await Collect(LineTokenizer.Tokenize("a\nb\nc", "\n", false)).ConfigureAwait(false);

        result.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task ByteArray_SplitsCorrectly()
    {
        var bytes = Encoding.UTF8.GetBytes("x\ny\nz");

        var result = await Collect(LineTokenizer.Tokenize(bytes, "\n", false)).ConfigureAwait(false);

        result.Should().Equal("x", "y", "z");
    }

    [Fact]
    public async Task SkipEmpty_True_SkipsEmptyLines()
    {
        var result = await Collect(LineTokenizer.Tokenize("a\n\nb\n  \nc", "\n", true)).ConfigureAwait(false);

        result.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task EmptyStream_ZeroElements()
    {
        var stream = new MemoryStream(Array.Empty<byte>());

        var result = await Collect(LineTokenizer.Tokenize(stream, "\n", false)).ConfigureAwait(false);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task NullBody_ZeroElements()
    {
        var result = await Collect(LineTokenizer.Tokenize(null, "\n", false)).ConfigureAwait(false);

        result.Should().BeEmpty();
    }

    private static async Task<List<object?>> Collect(IAsyncEnumerable<object?> source)
    {
        var list = new List<object?>();
        await foreach (var item in source.ConfigureAwait(false))
            list.Add(item);
        return list;
    }
}
