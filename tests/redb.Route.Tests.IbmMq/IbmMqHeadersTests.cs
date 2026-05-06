using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

public sealed class IbmMqHeadersTests
{
    [Fact]
    public void Prefix_IsCorrect()
    {
        IbmMqHeaders.Prefix.Should().Be("redbIbmMq.");
    }

    [Fact]
    public void AllConstants_StartWithPrefix()
    {
        IbmMqHeaders.Destination.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.QueueManager.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.MsgId.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.CorrelId.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.Format.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.CCSID.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.Encoding.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.Persistence.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.Priority.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.Expiry.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.ReplyToQueue.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.ReplyToQueueManager.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.MsgType.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.PutApplName.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.PutApplType.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.GroupId.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.MsgSeqNumber.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.Feedback.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.ReportOptions.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.UserIdentifier.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.BackoutCount.Should().StartWith("redbIbmMq.");
        IbmMqHeaders.PutDateTime.Should().StartWith("redbIbmMq.");
    }

    [Fact]
    public void IsRedbHeader_WithRedbPrefix_ReturnsTrue()
    {
        IbmMqHeaders.IsRedbHeader("redbIbmMq.MsgId").Should().BeTrue();
        IbmMqHeaders.IsRedbHeader("redbIbmMq.CorrelId").Should().BeTrue();
        IbmMqHeaders.IsRedbHeader("redbIbmMq.Destination").Should().BeTrue();
    }

    [Fact]
    public void IsRedbHeader_CaseInsensitive_ReturnsTrue()
    {
        IbmMqHeaders.IsRedbHeader("REDBIBMMQ.MsgId").Should().BeTrue();
        IbmMqHeaders.IsRedbHeader("redBibmMq.something").Should().BeTrue();
    }

    [Fact]
    public void IsRedbHeader_WithoutPrefix_ReturnsFalse()
    {
        IbmMqHeaders.IsRedbHeader("X-Custom-Header").Should().BeFalse();
        IbmMqHeaders.IsRedbHeader("traceparent").Should().BeFalse();
        IbmMqHeaders.IsRedbHeader("Content-Type").Should().BeFalse();
    }

    [Fact]
    public void IsRedbHeader_Empty_ReturnsFalse()
    {
        IbmMqHeaders.IsRedbHeader("").Should().BeFalse();
    }

    [Fact]
    public void IsRedbHeader_Null_ThrowsOrReturnsFalse()
    {
        var act = () => IbmMqHeaders.IsRedbHeader(null!);
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
            IbmMqHeaders.Destination, IbmMqHeaders.QueueManager,
            IbmMqHeaders.MsgId, IbmMqHeaders.CorrelId,
            IbmMqHeaders.Format, IbmMqHeaders.CCSID,
            IbmMqHeaders.Encoding, IbmMqHeaders.Persistence,
            IbmMqHeaders.Priority, IbmMqHeaders.Expiry,
            IbmMqHeaders.ReplyToQueue, IbmMqHeaders.ReplyToQueueManager,
            IbmMqHeaders.MsgType, IbmMqHeaders.PutApplName,
            IbmMqHeaders.PutApplType, IbmMqHeaders.GroupId,
            IbmMqHeaders.MsgSeqNumber, IbmMqHeaders.Feedback,
            IbmMqHeaders.ReportOptions, IbmMqHeaders.UserIdentifier,
            IbmMqHeaders.BackoutCount, IbmMqHeaders.PutDateTime
        };

        all.Should().OnlyHaveUniqueItems();
    }
}
