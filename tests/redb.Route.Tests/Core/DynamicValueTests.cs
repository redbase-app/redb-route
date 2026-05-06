using redb.Route.Abstractions;
using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class DynamicValueTests
{
    [Fact]
    public void FromStatic_ResolvesConstant()
    {
        var dv = DynamicValue<string>.FromStatic("hello");
        dv.IsDynamic.Should().BeFalse();
        dv.Resolve(new Exchange()).Should().Be("hello");
    }

    [Fact]
    public void FromExpression_ResolvesPerMessage()
    {
        var dv = DynamicValue<string>.FromExpression(ex => ex.In.GetHeader<string>("region"));

        var exchange = new Exchange(new Message());
        exchange.In.Headers["region"] = "us-east";

        dv.IsDynamic.Should().BeTrue();
        dv.Resolve(exchange).Should().Be("us-east");
    }

    [Fact]
    public void ImplicitConversion_CreatesStatic()
    {
        DynamicValue<int> dv = 42;
        dv.IsDynamic.Should().BeFalse();
        dv.Resolve(new Exchange()).Should().Be(42);
    }

    [Fact]
    public void FromExpression_NullResolver_Throws()
    {
        var act = () => DynamicValue<string>.FromExpression((Func<IExchange, string?>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToString_Static_ReturnsValue()
    {
        var dv = DynamicValue<string>.FromStatic("test");
        dv.ToString().Should().Be("test");
    }

    [Fact]
    public void ToString_Dynamic_ReturnsPlaceholder()
    {
        var dv = DynamicValue<string>.FromExpression(_ => "x");
        dv.ToString().Should().Be("${expression}");
    }

    [Fact]
    public void FromStatic_NullValue_ReturnsNull()
    {
        var dv = DynamicValue<string>.FromStatic(null!);
        dv.Resolve(new Exchange()).Should().BeNull();
    }

    [Fact]
    public void DynamicInt_ResolvesCorrectly()
    {
        var dv = DynamicValue<int>.FromExpression(ex => ex.In.GetHeader<int>("ttl"));

        var exchange = new Exchange(new Message());
        exchange.In.Headers["ttl"] = 60;

        dv.Resolve(exchange).Should().Be(60);
    }
}
