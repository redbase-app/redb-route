using redb.Route.Abstractions;
using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class ExchangeTests
{
    [Fact]
    public void NewExchange_HasEmptyMessage()
    {
        var ex = new Exchange();
        ex.In.Should().NotBeNull();
        ex.In.Body.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithMessage_SetsIn()
    {
        var msg = new Message("hello");
        var ex = new Exchange(msg);
        ex.In.Body.Should().Be("hello");
    }

    [Fact]
    public void Out_IsNullByDefault()
    {
        var ex = new Exchange();
        ex.Out.Should().BeNull();
        ex.HasOut.Should().BeFalse();
    }

    [Fact]
    public void Out_WhenSet_HasOutIsTrue()
    {
        var ex = new Exchange();
        ex.Out = new Message("response");
        ex.HasOut.Should().BeTrue();
        ex.Out!.Body.Should().Be("response");
    }

    [Fact]
    public void Pattern_DefaultIsInOnly()
    {
        var ex = new Exchange();
        ex.Pattern.Should().Be(ExchangePattern.InOnly);
    }

    [Fact]
    public void Properties_AreCaseInsensitive()
    {
        var ex = new Exchange();
        ex.Properties["RouteId"] = "test";
        ex.Properties["routeid"].Should().Be("test");
    }

    [Fact]
    public void GetProperty_ReturnsTypedValue()
    {
        var ex = new Exchange();
        ex.Properties["retries"] = 3;
        ex.GetProperty<int>("retries").Should().Be(3);
    }

    [Fact]
    public void GetProperty_ConvertsStringToInt()
    {
        var ex = new Exchange();
        ex.Properties["retries"] = "5";
        ex.GetProperty<int>("retries").Should().Be(5);
    }

    [Fact]
    public void GetProperty_MissingKey_ReturnsDefault()
    {
        var ex = new Exchange();
        ex.GetProperty<int>("missing").Should().Be(0);
    }

    [Fact]
    public void Stop_SetsIsStopped()
    {
        var ex = new Exchange();
        ex.IsStopped.Should().BeFalse();
        ex.Stop();
        ex.IsStopped.Should().BeTrue();
    }

    [Fact]
    public void Exception_DefaultIsNull()
    {
        var ex = new Exchange();
        ex.Exception.Should().BeNull();
        ex.ExceptionHandled.Should().BeFalse();
    }

    [Fact]
    public void Clone_CopiesAllState()
    {
        var ex = new Exchange(new Message("data"));
        ex.Pattern = ExchangePattern.InOut;
        ex.RouteId = "route-1";
        ex.Properties["key"] = "value";
        ex.Out = new Message("response");

        var clone = ex.Clone();

        clone.In.Body.Should().Be("data");
        clone.Out!.Body.Should().Be("response");
        clone.Pattern.Should().Be(ExchangePattern.InOut);
        clone.RouteId.Should().Be("route-1");
        clone.Properties["key"].Should().Be("value");
        clone.Should().NotBeSameAs(ex);
    }

    [Fact]
    public void Clone_IsDeepCopy_PropertiesIndependent()
    {
        var ex = new Exchange();
        ex.Properties["key"] = "original";

        var clone = ex.Clone();
        clone.Properties["key"] = "changed";

        ex.Properties["key"].Should().Be("original");
    }

    // ── Java-style API tests ──

    [Fact]
    public void JavaStyle_GetIn_ReturnsIn()
    {
        IExchange ex = new Exchange(new Message("test"));
        ex.getIn().Body.Should().Be("test");
    }

    [Fact]
    public void JavaStyle_GetOut_ReturnsOut()
    {
        IExchange ex = new Exchange();
        ex.Out = new Message("resp");
        ex.getOut()!.Body.Should().Be("resp");
    }

    [Fact]
    public void JavaStyle_GetSetProperty()
    {
        IExchange ex = new Exchange();
        ex.setProperty("key", "val");
        ex.getProperty("key").Should().Be("val");
    }

    [Fact]
    public void JavaStyle_IsStop_ReturnsFalse()
    {
        IExchange ex = new Exchange();
        ex.isStop().Should().BeFalse();
    }

    [Fact]
    public void JavaStyle_RouteStop_StopsExchange()
    {
        IExchange ex = new Exchange();
        ex.RouteStop();
        ex.IsStopped.Should().BeTrue();
        ex.isStop().Should().BeTrue();
    }

    [Fact]
    public void JavaStyle_Copy_ClonesExchange()
    {
        IExchange ex = new Exchange(new Message("data"));
        var clone = ex.copy();
        clone.In.Body.Should().Be("data");
        clone.Should().NotBeSameAs(ex);
    }
}
