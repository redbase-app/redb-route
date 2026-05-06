using redb.Route.Abstractions;
using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class MessageTests
{
    [Fact]
    public void NewMessage_HasNullBody()
    {
        var msg = new Message();
        msg.Body.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithBody_SetsBody()
    {
        var msg = new Message("hello");
        msg.Body.Should().Be("hello");
    }

    [Fact]
    public void Headers_AreCaseInsensitive()
    {
        var msg = new Message();
        msg.Headers["Content-Type"] = "application/json";
        msg.Headers["content-type"].Should().Be("application/json");
    }

    [Fact]
    public void GetHeader_ReturnsTypedValue()
    {
        var msg = new Message();
        msg.Headers["count"] = 42;
        msg.GetHeader<int>("count").Should().Be(42);
    }

    [Fact]
    public void GetHeader_ConvertsStringToInt()
    {
        var msg = new Message();
        msg.Headers["count"] = "42";
        msg.GetHeader<int>("count").Should().Be(42);
    }

    [Fact]
    public void GetHeader_MissingKey_ReturnsDefault()
    {
        var msg = new Message();
        msg.GetHeader<int>("missing").Should().Be(0);
    }

    [Fact]
    public void GetHeader_NullValue_ReturnsDefault()
    {
        var msg = new Message();
        msg.Headers["key"] = null;
        msg.GetHeader<string>("key").Should().BeNull();
    }

    [Fact]
    public void Clone_CopiesBodyAndHeaders()
    {
        var msg = new Message("data");
        msg.Headers["region"] = "us-east";

        var clone = msg.Clone();

        clone.Body.Should().Be("data");
        clone.Headers["region"].Should().Be("us-east");
        clone.Should().NotBeSameAs(msg);
    }

    [Fact]
    public void Clone_IsDeepCopy_HeadersAreIndependent()
    {
        var msg = new Message();
        msg.Headers["key"] = "original";

        var clone = msg.Clone();
        clone.Headers["key"] = "changed";

        msg.Headers["key"].Should().Be("original");
    }

    // ── Java-style API tests ──

    [Fact]
    public void JavaStyle_GetBody_ReturnsBody()
    {
        IMessage msg = new Message("test");
        msg.getBody().Should().Be("test");
    }

    [Fact]
    public void JavaStyle_SetBody_SetsBody()
    {
        IMessage msg = new Message();
        msg.setBody("hello");
        msg.Body.Should().Be("hello");
    }

    [Fact]
    public void JavaStyle_GetSetHeader()
    {
        IMessage msg = new Message();
        msg.setHeader("key", "value");
        msg.getHeader("key").Should().Be("value");
    }

    [Fact]
    public void JavaStyle_GetHeaders_ReturnsSameInstance()
    {
        IMessage msg = new Message();
        msg.getHeaders().Should().BeSameAs(msg.Headers);
    }

    [Fact]
    public void JavaStyle_Copy_ClonesMessage()
    {
        IMessage msg = new Message("data");
        var clone = msg.copy();
        clone.Body.Should().Be("data");
        clone.Should().NotBeSameAs(msg);
    }

    // ── ContentType tests ──

    [Fact]
    public void ContentType_DefaultIsNull()
    {
        var msg = new Message();
        msg.ContentType.Should().BeNull();
    }

    [Fact]
    public void ContentType_SetAndGet()
    {
        var msg = new Message("x") { ContentType = "application/json" };
        msg.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void Clone_CopiesContentType()
    {
        var msg = new Message("data") { ContentType = "text/xml" };
        msg.Headers["key"] = "val";

        var clone = msg.Clone();

        clone.ContentType.Should().Be("text/xml");
        clone.Body.Should().Be("data");
        clone.Headers["key"].Should().Be("val");
    }

    [Fact]
    public void Clone_ContentType_IsIndependent()
    {
        var msg = new Message { ContentType = "application/json" };
        var clone = msg.Clone();
        clone.ContentType = "text/plain";

        msg.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void Clone_NullContentType_StaysNull()
    {
        var msg = new Message("body");
        msg.ContentType.Should().BeNull();

        var clone = msg.Clone();
        clone.ContentType.Should().BeNull();
    }

    [Fact]
    public void JavaStyle_GetSetContentType()
    {
        IMessage msg = new Message();
        msg.setContentType("application/xml");
        msg.getContentType().Should().Be("application/xml");
    }
}
