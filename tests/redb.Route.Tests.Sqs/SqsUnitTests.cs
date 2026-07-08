using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sqs;
using SqsDsl = redb.Route.Sqs.Fluent.Sqs;
using SnsDsl = redb.Route.Sqs.Fluent.Sns;

namespace redb.Route.Tests.Sqs;

/// <summary>
/// Unit tests for the SQS/SNS connector — no broker required. Cover URI→options binding, validation,
/// FIFO detection, component scheme + endpoint creation, the fluent DSL, and header helpers.
/// </summary>
public class SqsUnitTests
{
    private static SqsEndpointOptions BindSqs(string query)
    {
        var uri = EndpointUriParser.Parse($"sqs://q?{query}");
        var options = new SqsEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        return options;
    }

    // ── Options binding ───────────────────────────────────────────────

    [Fact]
    public void Options_BindAllConsumerParams()
    {
        var o = BindSqs("accessKey=k&secretKey=s&region=eu-west-1&serviceUrl=http://localhost:4566"
            + "&waitTimeSeconds=15&maxNumberOfMessages=5&visibilityTimeout=45&concurrentConsumers=4"
            + "&extendMessageVisibility=true&deleteAfterRead=false&transacted=true&autoCreateQueue=true");

        o.AccessKey.Should().Be("k");
        o.SecretKey.Should().Be("s");
        o.Region.Should().Be("eu-west-1");
        o.ServiceUrl.Should().Be("http://localhost:4566");
        o.WaitTimeSeconds.Should().Be(15);
        o.MaxNumberOfMessages.Should().Be(5);
        o.VisibilityTimeout.Should().Be(45);
        o.ConcurrentConsumers.Should().Be(4);
        o.ExtendMessageVisibility.Should().BeTrue();
        o.DeleteAfterRead.Should().BeFalse();
        o.Transacted.Should().BeTrue();
        o.AutoCreateQueue.Should().BeTrue();
    }

    [Fact]
    public void Options_Defaults()
    {
        var o = BindSqs("accessKey=k&secretKey=s");
        o.WaitTimeSeconds.Should().Be(20);
        o.MaxNumberOfMessages.Should().Be(10);
        o.ConcurrentConsumers.Should().Be(1);
        o.DeleteAfterRead.Should().BeTrue();
        o.Region.Should().Be("us-east-1");
    }

    [Theory]
    [InlineData("accessKey=k&secretKey=s&waitTimeSeconds=21")]
    [InlineData("accessKey=k&secretKey=s&maxNumberOfMessages=11")]
    [InlineData("accessKey=k&secretKey=s&maxNumberOfMessages=0")]
    [InlineData("accessKey=k&secretKey=s&concurrentConsumers=0")]
    [InlineData("accessKey=k&secretKey=s&extendMessageVisibility=true")] // requires visibilityTimeout>0
    [InlineData("accessKey=k&secretKey=s&delaySeconds=901")]
    public void Validate_RejectsBadValues(string query)
    {
        var act = () => BindSqs(query).Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_RejectsMissingCredentials()
    {
        var act = () => BindSqs("region=us-east-1").Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*credentials*");
    }

    [Theory]
    [InlineData("accessKey=k&secretKey=s")]
    [InlineData("useDefaultCredentialsProvider=true")]
    [InlineData("profileName=dev")]
    public void Validate_AcceptsValidCredentialModes(string query)
    {
        var act = () => BindSqs(query).Validate();
        act.Should().NotThrow();
    }

    // ── FIFO detection ────────────────────────────────────────────────

    [Theory]
    [InlineData("orders.fifo", true)]
    [InlineData("orders", false)]
    public void Sqs_FifoDetectedFromQueueName(string name, bool expected)
    {
        var endpoint = (SqsEndpoint)new SqsComponent()
            .CreateEndpoint(EndpointUriParser.Parse($"sqs://{name}?accessKey=k&secretKey=s"));
        endpoint.IsFifo.Should().Be(expected);
        endpoint.QueueName.Should().Be(name);
    }

    // ── Component + endpoint ──────────────────────────────────────────

    [Fact]
    public void SqsComponent_SchemeAndEndpoint()
    {
        var component = new SqsComponent();
        component.Scheme.Should().Be("sqs");

        var endpoint = component.CreateEndpoint(EndpointUriParser.Parse("sqs://q?accessKey=k&secretKey=s"));
        endpoint.Should().BeOfType<SqsEndpoint>();
        endpoint.CreateProducer().Should().BeAssignableTo<IProducer>();
        endpoint.CreateConsumer(Substitute.For<IProcessor>()).Should().BeAssignableTo<IConsumer>();
    }

    [Fact]
    public void SnsComponent_SchemeAndPublishOnly()
    {
        var component = new SnsComponent();
        component.Scheme.Should().Be("sns");

        var endpoint = (SnsEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("sns://t?accessKey=k&secretKey=s"));
        endpoint.TopicName.Should().Be("t");
        endpoint.CreateProducer().Should().BeAssignableTo<IProducer>();

        var act = () => endpoint.CreateConsumer(Substitute.For<IProcessor>());
        act.Should().Throw<NotSupportedException>().WithMessage("*publish-only*");
    }

    [Fact]
    public void Sns_ValidateRequiresQueueArnForSubscription()
    {
        var uri = EndpointUriParser.Parse("sns://t?accessKey=k&secretKey=s&subscribeSnsToSqs=true");
        var options = new SnsEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*subscribeQueueArn*");
    }

    // ── Fluent DSL ────────────────────────────────────────────────────

    [Fact]
    public void SqsDsl_EmitsUri()
    {
        string uri = SqsDsl.Queue("orders").Region("eu-west-1").WaitTimeSeconds(20).ConcurrentConsumers(4);
        uri.Should().StartWith("sqs://orders?");
        uri.Should().Contain("region=eu-west-1");
        uri.Should().Contain("waitTimeSeconds=20");
        uri.Should().Contain("concurrentConsumers=4");
    }

    [Fact]
    public void SnsDsl_EmitsUri_AndSubscribeSetsBothParams()
    {
        string uri = SnsDsl.Topic("events").Region("us-east-1").SubscribeSnsToSqs("arn:aws:sqs:us-east-1:0:q");
        uri.Should().StartWith("sns://events?");
        uri.Should().Contain("subscribeSnsToSqs=true");
        uri.Should().Contain("subscribeQueueArn=");
    }

    [Fact]
    public void Dsl_RoundTripsThroughOptions()
    {
        string uri = SqsDsl.Queue("orders").Credentials("k", "s").MaxNumberOfMessages(7);
        var parsed = EndpointUriParser.Parse(uri);
        var options = new SqsEndpointOptions();
        options.BindFromUri(parsed.RawParameters);
        options.MaxNumberOfMessages.Should().Be(7);
    }

    // ── Headers ───────────────────────────────────────────────────────

    [Fact]
    public void Headers_PrefixAndGuard()
    {
        SqsHeaders.MessageId.Should().StartWith("redbSqs.");
        SqsHeaders.IsSqsHeader(SqsHeaders.ReceiptHandle).Should().BeTrue();
        SqsHeaders.IsSqsHeader("customHeader").Should().BeFalse();

        SnsHeaders.TopicArn.Should().StartWith("redbSns.");
        SnsHeaders.IsSnsHeader(SnsHeaders.MessageId).Should().BeTrue();
        SnsHeaders.IsSnsHeader("customHeader").Should().BeFalse();
    }
}
