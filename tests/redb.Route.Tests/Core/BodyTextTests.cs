using System.Text.Json.Nodes;
using AwesomeAssertions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>The text a producer sends for a body it has no wire form for: the body's own text, never its type name.</summary>
public class BodyTextTests
{
    private sealed class NoText { }

    private sealed record WithText(int Id);

    [Theory]
    [MemberData(nameof(Texted))]
    public void A_body_with_a_text_form_of_its_own_is_its_text(object body, string text)
        => BodyText.Of(body).Should().Be(text);

    public static TheoryData<object, string> Texted => new()
    {
        { 42, "42" },
        { new JsonArray(1, 2), "[\n  1,\n  2\n]".Replace("\n", Environment.NewLine) },
        { new WithText(7), "WithText { Id = 7 }" },
        { new System.Text.StringBuilder("sb"), "sb" },
    };

    [Fact]
    public void Bytes_and_a_stream_are_their_UTF8_text()
    {
        BodyText.Of(System.Text.Encoding.UTF8.GetBytes("héllo")).Should().Be("héllo");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("from a stream"));
        stream.Position = 5;
        BodyText.Of(stream).Should().Be("from a stream", "a seekable stream is read from its start");
    }

    [Fact]
    public void A_list_is_refused_naming_the_endpoint_and_the_way_out()
    {
        var act = () => BodyText.Of(new List<int> { 1 }, "mqtt://t");

        act.Should().Throw<InvalidOperationException>().WithMessage("*mqtt://t*List*Serialize the list*");
    }

    [Fact]
    public void An_object_without_a_text_form_is_refused()
    {
        var act = () => BodyText.Of(new NoText());

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoText*type name*Convert it*");
    }
}
