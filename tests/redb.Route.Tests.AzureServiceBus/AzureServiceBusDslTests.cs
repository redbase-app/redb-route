using redb.Route.AzureServiceBus;
using redb.Route.AzureServiceBus.Fluent;

namespace redb.Route.Tests.AzureServiceBus;

public sealed class AzureServiceBusDslTests
{
    [Fact]
    public void Queue_BuildsCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionString("Endpoint=sb://test")
            .Build();

        uri.Should().StartWith("asb://orders?");
        uri.Should().Contain("connectionString=");
    }

    [Fact]
    public void Topic_BuildsCorrectUri()
    {
        var uri = Asb.Topic("events", "my-sub")
            .ConnectionString("Endpoint=sb://test")
            .Build();

        uri.Should().StartWith("asb://events?");
        uri.Should().Contain("subscriptionName=my-sub");
        uri.Should().Contain("connectionString=");
    }

    [Fact]
    public void Queue_WithSessions_BuildsCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionString("Endpoint=sb://test")
            .EnableSessions()
            .SessionId("session-1")
            .MaxConcurrentSessions(3)
            .Build();

        uri.Should().Contain("enableSessions=True");
        uri.Should().Contain("sessionId=session-1");
        uri.Should().Contain("maxConcurrentSessions=3");
    }

    [Fact]
    public void Queue_WithConsumerOptions_BuildsCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionString("Endpoint=sb://test")
            .ReceiveMode("ReceiveAndDelete")
            .MaxConcurrentCalls(5)
            .PrefetchCount(10)
            .Build();

        uri.Should().Contain("receiveMode=ReceiveAndDelete");
        uri.Should().Contain("maxConcurrentCalls=5");
        uri.Should().Contain("prefetchCount=10");
    }

    [Fact]
    public void Queue_WithProducerOptions_BuildsCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionString("Endpoint=sb://test")
            .ScheduleDelaySeconds(30)
            .TimeToLive("00:05:00")
            .EnableBatch()
            .BatchMaxMessages(50)
            .Build();

        uri.Should().Contain("scheduleDelaySeconds=30");
        uri.Should().Contain("timeToLive=00%3A05%3A00");
        uri.Should().Contain("enableBatch=True");
        uri.Should().Contain("batchMaxMessages=50");
    }

    [Fact]
    public void Queue_WithDeadLetter_BuildsCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionString("Endpoint=sb://test")
            .SubQueue("deadletter")
            .Build();

        uri.Should().Contain("subQueue=deadletter");
    }

    [Fact]
    public void Queue_WithTransacted_BuildsCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionString("Endpoint=sb://test")
            .Transacted()
            .Build();

        uri.Should().Contain("transacted=True");
    }

    [Fact]
    public void Queue_WithAutoDeadLetter_BuildsCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionString("Endpoint=sb://test")
            .AutoDeadLetter()
            .DeadLetterReason("test-reason")
            .Build();

        uri.Should().Contain("autoDeadLetter=True");
        uri.Should().Contain("deadLetterReason=test-reason");
    }

    [Fact]
    public void Queue_Minimal_NoQueryString()
    {
        var uri = Asb.Queue("orders").Build();
        uri.Should().Be("asb://orders");
    }

    [Fact]
    public void ImplicitStringConversion_Works()
    {
        string uri = Asb.Queue("test-q").ConnectionString("Endpoint=sb://x");
        uri.Should().StartWith("asb://test-q?");
    }

    [Fact]
    public void ToString_ReturnsBuild()
    {
        var builder = Asb.Queue("test-q").ConnectionString("Endpoint=sb://x");
        builder.ToString().Should().Be(builder.Build());
    }

    [Fact]
    public void ConnectionFactory_BuildsCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionFactory("myFactory")
            .Build();

        uri.Should().Contain("connectionFactory=myFactory");
    }

    [Fact]
    public void RetryOptions_BuildCorrectUri()
    {
        var uri = Asb.Queue("orders")
            .ConnectionString("Endpoint=sb://test")
            .RetryMaxRetries(5)
            .RetryMode("Fixed")
            .Build();

        uri.Should().Contain("retryMaxRetries=5");
        uri.Should().Contain("retryMode=Fixed");
    }
}
