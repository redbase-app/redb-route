using redb.Route.Core;

namespace redb.Route.Tests.Fluent;

public class DirectBuilderTests
{
    [Fact]
    public void Endpoint_StartsWithDirectScheme()
    {
        var uri = Direct.Endpoint("myQueue").Build();
        uri.Should().Be("direct:myQueue");
    }

    [Fact]
    public void NullName_Throws()
    {
        var act = () => Direct.Endpoint(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyName_Throws()
    {
        var act = () => Direct.Endpoint("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = Direct.Endpoint("test");
        uri.Should().Be("direct:test");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = Direct.Endpoint("abc");
        builder.ToString().Should().Be(builder.Build());
    }

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = Direct.Endpoint("my-queue").Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("direct");
        parsed.Path.Should().Be("my-queue");
        parsed.ToUriString().Should().Be(original);
    }
}

public class SedaBuilderTests
{
    [Fact]
    public void Consume_StartsWithSedaScheme()
    {
        var uri = Seda.Consume("orders").Build();
        uri.Should().StartWith("seda:orders");
    }

    [Fact]
    public void Send_StartsWithSedaScheme()
    {
        var uri = Seda.Send("orders").Build();
        uri.Should().StartWith("seda:orders");
    }

    [Fact]
    public void NullName_Throws()
    {
        var act = () => Seda.Consume(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ConcurrentConsumers_SetsParam()
    {
        var uri = Seda.Consume("q").ConcurrentConsumers(4).Build();
        uri.Should().Contain("concurrentConsumers=4");
    }

    [Fact]
    public void Size_SetsParam()
    {
        var uri = Seda.Consume("q").Size(1000).Build();
        uri.Should().Contain("size=1000");
    }

    [Fact]
    public void Timeout_SetsParam()
    {
        var uri = Seda.Consume("q").Timeout(30000).Build();
        uri.Should().Contain("timeout=30000");
    }

    [Fact]
    public void FullChain_BuildsCorrectUri()
    {
        var uri = Seda.Consume("order-queue")
            .ConcurrentConsumers(4)
            .Size(500)
            .Timeout(60000)
            .Build();

        uri.Should().StartWith("seda:order-queue?");
        uri.Should().Contain("concurrentConsumers=4");
        uri.Should().Contain("size=500");
        uri.Should().Contain("timeout=60000");
    }

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = Seda.Consume("test").ConcurrentConsumers(2);
        uri.Should().StartWith("seda:test?");
        uri.Should().Contain("concurrentConsumers=2");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = Seda.Send("q").Size(100);
        builder.ToString().Should().Be(builder.Build());
    }

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = Seda.Consume("q").ConcurrentConsumers(4).Size(500).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("seda");
        parsed.Path.Should().Be("q");
        parsed.RawParameters.Should().ContainKey("concurrentConsumers");
        parsed.RawParameters.Should().ContainKey("size");
    }

    [Fact]
    public void NoParams_ProducesCleanUri()
    {
        var uri = Seda.Consume("simple").Build();
        uri.Should().Be("seda:simple");
    }
}

public class TimerBuilderTests
{
    [Fact]
    public void Every_StartsWithTimerScheme()
    {
        var uri = TimerDsl.Every("tick").Build();
        uri.Should().StartWith("timer:tick");
    }

    [Fact]
    public void Once_SetsRepeatCountToOne()
    {
        var uri = TimerDsl.Once("startup").Build();
        uri.Should().Contain("repeatCount=1");
    }

    [Fact]
    public void Period_SetsParam()
    {
        var uri = TimerDsl.Every("poll").Period(5000).Build();
        uri.Should().Contain("period=5000");
    }

    [Fact]
    public void Delay_SetsParam()
    {
        var uri = TimerDsl.Every("poll").Delay(2000).Build();
        uri.Should().Contain("delay=2000");
    }

    [Fact]
    public void RepeatCount_SetsParam()
    {
        var uri = TimerDsl.Every("poll").RepeatCount(10).Build();
        uri.Should().Contain("repeatCount=10");
    }

    [Fact]
    public void FullChain_BuildsCorrectUri()
    {
        var uri = TimerDsl.Every("heartbeat")
            .Period(5000)
            .Delay(1000)
            .Build();

        uri.Should().StartWith("timer:heartbeat?");
        uri.Should().Contain("period=5000");
        uri.Should().Contain("delay=1000");
    }

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = TimerDsl.Every("t").Period(1000).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("timer");
        parsed.Path.Should().Be("t");
        parsed.RawParameters["period"].Should().Be("1000");
    }
}

public class LogBuilderTests
{
    [Fact]
    public void Info_SetsLogSchemeAndLevel()
    {
        var uri = LogDsl.Info("myLogger").Build();
        uri.Should().StartWith("log:myLogger");
        uri.Should().Contain("level=Info");
    }

    [Fact]
    public void Debug_SetsLevel()
    {
        var uri = LogDsl.Debug("test").Build();
        uri.Should().Contain("level=Debug");
    }

    [Fact]
    public void Warn_SetsLevel()
    {
        var uri = LogDsl.Warn("test").Build();
        uri.Should().Contain("level=Warn");
    }

    [Fact]
    public void ShowHeaders_SetsParam()
    {
        var uri = LogDsl.Info("test").ShowHeaders().Build();
        uri.Should().Contain("showHeaders=true");
    }

    [Fact]
    public void ShowBody_SetsParam()
    {
        var uri = LogDsl.Info("test").ShowBody().Build();
        uri.Should().Contain("showBody=true");
    }

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = LogDsl.Info("app").ShowHeaders().ShowBody().Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("log");
        parsed.RawParameters["level"].Should().Be("Information");
        parsed.RawParameters["showHeaders"].Should().Be("true");
    }
}

public class MockBuilderTests
{
    [Fact]
    public void Endpoint_StartsWithMockScheme()
    {
        var uri = MockDsl.Endpoint("test").Build();
        uri.Should().StartWith("mock:test");
    }

    [Fact]
    public void ExpectedMessageCount_SetsParam()
    {
        var uri = MockDsl.Endpoint("test").ExpectedMessageCount(5).Build();
        uri.Should().Contain("expectedMessageCount=5");
    }

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = MockDsl.Endpoint("verify").ExpectedMessageCount(10).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("mock");
        parsed.Path.Should().Be("verify");
        parsed.RawParameters["expectedMessageCount"].Should().Be("10");
    }
}
