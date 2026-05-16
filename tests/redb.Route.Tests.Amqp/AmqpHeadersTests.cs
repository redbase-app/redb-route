using redb.Route.Amqp;

namespace redb.Route.Tests.Amqp;

public sealed class AmqpHeadersTests
{
    [Fact]
    public void Prefix_IsCorrect()
    {
        AmqpHeaders.Address.Should().StartWith("redbAmqp.");
        AmqpHeaders.MessageId.Should().StartWith("redbAmqp.");
        AmqpHeaders.CorrelationId.Should().StartWith("redbAmqp.");
    }

    [Fact]
    public void IsRedbHeader_WithRedbPrefix_ReturnsTrue()
    {
        AmqpHeaders.IsRedbHeader("redbAmqp.address").Should().BeTrue();
        AmqpHeaders.IsRedbHeader("redbAmqp.messageId").Should().BeTrue();
    }

    [Fact]
    public void IsRedbHeader_WithoutPrefix_ReturnsFalse()
    {
        AmqpHeaders.IsRedbHeader("X-Custom-Header").Should().BeFalse();
        AmqpHeaders.IsRedbHeader("traceparent").Should().BeFalse();
    }

    [Fact]
    public void IsRedbHeader_Empty_ReturnsFalse()
    {
        AmqpHeaders.IsRedbHeader("").Should().BeFalse();
    }

    [Fact]
    public void IsRedbHeader_Null_ThrowsOrReturnsFalse()
    {
        // null input is a programming error — either throws or returns false
        var act = () => AmqpHeaders.IsRedbHeader(null!);
        try
        {
            act().Should().BeFalse();
        }
        catch (NullReferenceException)
        {
            // also acceptable
        }
    }

    [Fact]
    public void AllConstants_AreDistinct()
    {
        var all = new[]
        {
            AmqpHeaders.Address, AmqpHeaders.MessageId, AmqpHeaders.CorrelationId,
            AmqpHeaders.ReplyTo, AmqpHeaders.ContentType, AmqpHeaders.Subject,
            AmqpHeaders.GroupId, AmqpHeaders.GroupSequence, AmqpHeaders.CreationTime,
            AmqpHeaders.AbsoluteExpiryTime, AmqpHeaders.Durable, AmqpHeaders.Priority,
            AmqpHeaders.Ttl, AmqpHeaders.DeliveryCount, AmqpHeaders.FirstAcquirer,
            AmqpHeaders.SenderSettleMode, AmqpHeaders.ReceiverSettleMode
        };

        all.Should().OnlyHaveUniqueItems();
    }
}
