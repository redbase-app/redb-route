using redb.Route.AzureServiceBus;

namespace redb.Route.Tests.AzureServiceBus;

public sealed class AzureServiceBusHeadersTests
{
    [Theory]
    [InlineData(AzureServiceBusHeaders.MessageId)]
    [InlineData(AzureServiceBusHeaders.CorrelationId)]
    [InlineData(AzureServiceBusHeaders.SessionId)]
    [InlineData(AzureServiceBusHeaders.PartitionKey)]
    [InlineData(AzureServiceBusHeaders.Subject)]
    [InlineData(AzureServiceBusHeaders.ContentType)]
    [InlineData(AzureServiceBusHeaders.ReplyTo)]
    [InlineData(AzureServiceBusHeaders.DeliveryCount)]
    [InlineData(AzureServiceBusHeaders.EnqueuedTime)]
    [InlineData(AzureServiceBusHeaders.SequenceNumber)]
    [InlineData(AzureServiceBusHeaders.DeadLetterSource)]
    [InlineData(AzureServiceBusHeaders.SessionState)]
    [InlineData(AzureServiceBusHeaders.BatchMessageCount)]
    public void AllHeaders_HaveCorrectPrefix(string headerKey)
    {
        headerKey.Should().StartWith(AzureServiceBusHeaders.Prefix);
    }

    [Theory]
    [InlineData("redbAsb.MessageId", true)]
    [InlineData("redbAsb.CustomHeader", true)]
    [InlineData("SomeOtherHeader", false)]
    [InlineData("redbEs.Index", false)]
    [InlineData("", false)]
    public void IsAsbHeader_DetectsPrefix(string key, bool expected)
    {
        AzureServiceBusHeaders.IsAsbHeader(key).Should().Be(expected);
    }

    [Fact]
    public void Prefix_IsRedbAsb()
    {
        AzureServiceBusHeaders.Prefix.Should().Be("redbAsb.");
    }
}
