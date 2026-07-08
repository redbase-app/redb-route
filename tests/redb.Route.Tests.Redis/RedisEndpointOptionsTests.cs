using redb.Route.Redis;

namespace redb.Route.Tests.Redis;

public sealed class RedisEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var opts = new RedisEndpointOptions();

        opts.ConnectionString.Should().Be("localhost:6379");
        opts.Database.Should().Be(0);
        opts.Password.Should().BeNull();
        opts.Key.Should().BeNull();
        opts.Channel.Should().BeNull();
        opts.StreamName.Should().BeNull();
        opts.ConsumerGroup.Should().BeNull();
        opts.ConsumerName.Should().BeNull();
        opts.UsePattern.Should().BeFalse();
        opts.Ttl.Should().Be(0);
        opts.Score.Should().BeNull();
        opts.Field.Should().BeNull();
        opts.GeoUnit.Should().Be("m");
        opts.StreamMaxLength.Should().Be(0);
        opts.StreamApproximate.Should().BeTrue();
        opts.StreamReadCount.Should().Be(10);
        opts.StreamBlockTimeMs.Should().Be(1000);
        opts.StreamAutoAck.Should().BeTrue();
        opts.StreamStartPosition.Should().Be(">");
        opts.Transacted.Should().BeFalse();
        opts.PollDelayMs.Should().Be(1000);
    }

    [Fact]
    public void Validate_InvalidStreamReadCount_Throws()
    {
        var opts = new RedisEndpointOptions { StreamReadCount = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*StreamReadCount*");
    }

    [Fact]
    public void Validate_NegativeStreamReadCount_Throws()
    {
        var opts = new RedisEndpointOptions { StreamReadCount = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var opts = new RedisEndpointOptions { StreamReadCount = 100 };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }
}
