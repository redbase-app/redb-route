using redb.Route.Core;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

public sealed class Pop3ComponentTests
{
    private readonly Pop3Component _sut = new();

    [Fact]
    public void Scheme_ReturnsPop3()
    {
        _sut.Scheme.Should().Be("pop3");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsPop3Endpoint()
    {
        var uri = EndpointUriParser.Parse("pop3://pop.example.com?username=u&password=p");

        var endpoint = _sut.CreateEndpoint(uri);

        endpoint.Should().BeOfType<Pop3Endpoint>();
        var ep = (Pop3Endpoint)endpoint;
        ep.Host.Should().Be("pop.example.com");
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var act = () => _sut.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_ProducerThrows()
    {
        var uri = EndpointUriParser.Parse("pop3://localhost");
        var ep = (Pop3Endpoint)_sut.CreateEndpoint(uri);

        var act = () => ep.CreateProducer();

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void CreateEndpoint_ConsumerCreated()
    {
        var uri = EndpointUriParser.Parse("pop3://localhost?username=u&password=p");
        var ep = (Pop3Endpoint)_sut.CreateEndpoint(uri);
        var processor = Substitute.For<redb.Route.Abstractions.IProcessor>();

        var consumer = ep.CreateConsumer(processor);

        consumer.Should().BeOfType<Pop3Consumer>();
    }

    [Fact]
    public void CreateEndpoint_DefaultPop3SslPort()
    {
        var uri = EndpointUriParser.Parse("pop3://localhost");
        var ep = (Pop3Endpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(995);
    }

    [Fact]
    public void CreateEndpoint_PlainPop3Port()
    {
        var uri = EndpointUriParser.Parse("pop3://localhost?security=None");
        var ep = (Pop3Endpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(110);
    }

    [Fact]
    public void CreateEndpoint_ExplicitPort()
    {
        var uri = EndpointUriParser.Parse("pop3://localhost?port=3110");
        var ep = (Pop3Endpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(3110);
    }

    [Fact]
    public void CreateEndpoint_WithConsumerOptions()
    {
        var uri = EndpointUriParser.Parse(
            "pop3://pop.corp.com?username=u&password=p&maxMessages=20&delay=30000" +
            "&postProcess=Delete&idempotent=true");

        var ep = (Pop3Endpoint)_sut.CreateEndpoint(uri);

        ep.EndpointOptions.MaxMessages.Should().Be(20);
        ep.EndpointOptions.Delay.Should().Be(30000);
        ep.EndpointOptions.PostProcess.Should().Be(PostProcessAction.Delete);
        ep.EndpointOptions.Idempotent.Should().BeTrue();
    }
}
