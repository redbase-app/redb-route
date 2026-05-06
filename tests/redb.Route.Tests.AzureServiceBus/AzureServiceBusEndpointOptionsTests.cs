using Azure.Messaging.ServiceBus;
using redb.Route.AzureServiceBus;

namespace redb.Route.Tests.AzureServiceBus;

public sealed class AzureServiceBusEndpointOptionsTests
{
    // ── Validate ──

    [Fact]
    public void Validate_MissingConnectionStringAndFactory_Throws()
    {
        var options = new AzureServiceBusEndpointOptions();
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ConnectionString");
    }

    [Fact]
    public void Validate_ConnectionString_IsValid()
    {
        var options = new AzureServiceBusEndpointOptions { ConnectionString = "Endpoint=sb://test" };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ConnectionFactory_AllowsMissingConnectionString()
    {
        var options = new AzureServiceBusEndpointOptions { ConnectionFactory = "myFactory" };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MaxConcurrentCallsZero_Throws()
    {
        var options = new AzureServiceBusEndpointOptions
        {
            ConnectionString = "Endpoint=sb://test",
            MaxConcurrentCalls = 0
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("MaxConcurrentCalls");
    }

    [Fact]
    public void Validate_NegativePrefetchCount_Throws()
    {
        var options = new AzureServiceBusEndpointOptions
        {
            ConnectionString = "Endpoint=sb://test",
            PrefetchCount = -1
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("PrefetchCount");
    }

    [Fact]
    public void Validate_InvalidReceiveMode_Throws()
    {
        var options = new AzureServiceBusEndpointOptions
        {
            ConnectionString = "Endpoint=sb://test",
            ReceiveMode = "Invalid"
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ReceiveMode");
    }

    [Fact]
    public void Validate_InvalidSubQueue_Throws()
    {
        var options = new AzureServiceBusEndpointOptions
        {
            ConnectionString = "Endpoint=sb://test",
            SubQueue = "invalid"
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("SubQueue");
    }

    [Fact]
    public void Validate_MaxConcurrentSessionsZero_Throws()
    {
        var options = new AzureServiceBusEndpointOptions
        {
            ConnectionString = "Endpoint=sb://test",
            MaxConcurrentSessions = 0
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("MaxConcurrentSessions");
    }

    [Fact]
    public void Validate_InvalidRetryMode_Throws()
    {
        var options = new AzureServiceBusEndpointOptions
        {
            ConnectionString = "Endpoint=sb://test",
            RetryMode = "invalid"
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("RetryMode");
    }

    [Fact]
    public void Validate_BatchMaxMessagesZero_Throws()
    {
        var options = new AzureServiceBusEndpointOptions
        {
            ConnectionString = "Endpoint=sb://test",
            BatchMaxMessages = 0
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("BatchMaxMessages");
    }

    // ── BindFromUri ──

    [Fact]
    public void BindFromUri_AllProperties_Bound()
    {
        var options = new AzureServiceBusEndpointOptions();
        var parameters = new Dictionary<string, string>
        {
            ["connectionString"] = "Endpoint=sb://test",
            ["subscriptionName"] = "sub1",
            ["receiveMode"] = "ReceiveAndDelete",
            ["maxConcurrentCalls"] = "5",
            ["prefetchCount"] = "10",
            ["maxAutoLockRenewalDuration"] = "600",
            ["subQueue"] = "deadletter",
            ["enableSessions"] = "true",
            ["sessionId"] = "session-1",
            ["maxConcurrentSessions"] = "3",
            ["sessionIdleTimeout"] = "120",
            ["autoDeadLetter"] = "true",
            ["deadLetterReason"] = "test-reason",
            ["transacted"] = "true",
            ["partitionKey"] = "pk-1",
            ["scheduleDelaySeconds"] = "30",
            ["timeToLive"] = "00:05:00",
            ["enableBatch"] = "true",
            ["batchMaxMessages"] = "50",
            ["batchMaxSizeBytes"] = "512000",
            ["retryMaxRetries"] = "5",
            ["retryDelayMs"] = "1000",
            ["retryMaxDelayMs"] = "30000",
            ["retryMode"] = "Fixed"
        };

        options.BindFromUri(parameters);

        options.ConnectionString.Should().Be("Endpoint=sb://test");
        options.SubscriptionName.Should().Be("sub1");
        options.ReceiveMode.Should().Be("ReceiveAndDelete");
        options.MaxConcurrentCalls.Should().Be(5);
        options.PrefetchCount.Should().Be(10);
        options.MaxAutoLockRenewalDuration.Should().Be(600);
        options.SubQueue.Should().Be("deadletter");
        options.EnableSessions.Should().BeTrue();
        options.SessionId.Should().Be("session-1");
        options.MaxConcurrentSessions.Should().Be(3);
        options.SessionIdleTimeout.Should().Be(120);
        options.AutoDeadLetter.Should().BeTrue();
        options.DeadLetterReason.Should().Be("test-reason");
        options.Transacted.Should().BeTrue();
        options.PartitionKey.Should().Be("pk-1");
        options.ScheduleDelaySeconds.Should().Be(30);
        options.TimeToLive.Should().Be("00:05:00");
        options.EnableBatch.Should().BeTrue();
        options.BatchMaxMessages.Should().Be(50);
        options.BatchMaxSizeBytes.Should().Be(512000);
        options.RetryMaxRetries.Should().Be(5);
        options.RetryDelayMs.Should().Be(1000);
        options.RetryMaxDelayMs.Should().Be(30000);
        options.RetryMode.Should().Be("Fixed");
    }

    [Fact]
    public void BindFromUri_DefaultValues()
    {
        var options = new AzureServiceBusEndpointOptions();

        options.ReceiveMode.Should().Be("PeekLock");
        options.MaxConcurrentCalls.Should().Be(1);
        options.PrefetchCount.Should().Be(0);
        options.MaxAutoLockRenewalDuration.Should().Be(300);
        options.EnableSessions.Should().BeFalse();
        options.MaxConcurrentSessions.Should().Be(1);
        options.AutoDeadLetter.Should().BeFalse();
        options.Transacted.Should().BeFalse();
        options.EnableBatch.Should().BeFalse();
        options.BatchMaxMessages.Should().Be(100);
        options.BatchMaxSizeBytes.Should().Be(256 * 1024);
        options.RetryMaxRetries.Should().Be(3);
        options.RetryDelayMs.Should().Be(800);
        options.RetryMaxDelayMs.Should().Be(60_000);
        options.RetryMode.Should().Be("Exponential");
    }

    // ── Parsed helpers ──

    [Fact]
    public void ParsedReceiveMode_PeekLock()
    {
        var options = new AzureServiceBusEndpointOptions { ReceiveMode = "PeekLock" };
        options.ParsedReceiveMode.Should().Be(ServiceBusReceiveMode.PeekLock);
    }

    [Fact]
    public void ParsedReceiveMode_ReceiveAndDelete()
    {
        var options = new AzureServiceBusEndpointOptions { ReceiveMode = "ReceiveAndDelete" };
        options.ParsedReceiveMode.Should().Be(ServiceBusReceiveMode.ReceiveAndDelete);
    }

    [Fact]
    public void ParsedReceiveMode_CaseInsensitive()
    {
        var options = new AzureServiceBusEndpointOptions { ReceiveMode = "receiveanddelete" };
        options.ParsedReceiveMode.Should().Be(ServiceBusReceiveMode.ReceiveAndDelete);
    }

    [Fact]
    public void ParsedSubQueue_DeadLetter()
    {
        var options = new AzureServiceBusEndpointOptions { SubQueue = "deadletter" };
        options.ParsedSubQueue.Should().Be(SubQueue.DeadLetter);
    }

    [Fact]
    public void ParsedSubQueue_TransferDeadLetter()
    {
        var options = new AzureServiceBusEndpointOptions { SubQueue = "transferdeadletter" };
        options.ParsedSubQueue.Should().Be(SubQueue.TransferDeadLetter);
    }

    [Fact]
    public void ParsedSubQueue_Null_WhenNotSet()
    {
        var options = new AzureServiceBusEndpointOptions();
        options.ParsedSubQueue.Should().BeNull();
    }

    [Fact]
    public void ParsedRetryMode_Exponential()
    {
        var options = new AzureServiceBusEndpointOptions { RetryMode = "Exponential" };
        options.ParsedRetryMode.Should().Be(ServiceBusRetryMode.Exponential);
    }

    [Fact]
    public void ParsedRetryMode_Fixed()
    {
        var options = new AzureServiceBusEndpointOptions { RetryMode = "Fixed" };
        options.ParsedRetryMode.Should().Be(ServiceBusRetryMode.Fixed);
    }

    [Fact]
    public void ParsedRetryMode_CaseInsensitive()
    {
        var options = new AzureServiceBusEndpointOptions { RetryMode = "fixed" };
        options.ParsedRetryMode.Should().Be(ServiceBusRetryMode.Fixed);
    }
}
