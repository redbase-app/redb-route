using redb.Route.Core;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

public sealed class SmtpComponentTests
{
    private readonly SmtpComponent _sut = new();

    [Fact]
    public void Scheme_ReturnsSmtp()
    {
        _sut.Scheme.Should().Be("smtp");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsSmtpEndpoint()
    {
        var uri = EndpointUriParser.Parse("smtp://mail.example.com?username=bot@ex.com&password=p");

        var endpoint = _sut.CreateEndpoint(uri);

        endpoint.Should().BeOfType<SmtpEndpoint>();
        var ep = (SmtpEndpoint)endpoint;
        ep.Host.Should().Be("mail.example.com");
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var act = () => _sut.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_ProducerCreated()
    {
        var uri = EndpointUriParser.Parse("smtp://localhost?username=u&password=p");
        var ep = (SmtpEndpoint)_sut.CreateEndpoint(uri);

        var producer = ep.CreateProducer();

        producer.Should().BeOfType<SmtpProducer>();
    }

    [Fact]
    public void CreateEndpoint_ConsumerThrows()
    {
        var uri = EndpointUriParser.Parse("smtp://localhost");
        var ep = (SmtpEndpoint)_sut.CreateEndpoint(uri);
        var processor = Substitute.For<redb.Route.Abstractions.IProcessor>();

        var act = () => ep.CreateConsumer(processor);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void CreateEndpoint_PortFromUri()
    {
        var uri = EndpointUriParser.Parse("smtp://localhost?port=2525");
        var ep = (SmtpEndpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(2525);
    }

    [Fact]
    public void CreateEndpoint_DefaultSmtpPort_StartTls()
    {
        var uri = EndpointUriParser.Parse("smtp://localhost");
        var ep = (SmtpEndpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(587); // default for StartTls/Auto
    }

    [Fact]
    public void CreateEndpoint_SslPort()
    {
        var uri = EndpointUriParser.Parse("smtp://localhost?security=Ssl");
        var ep = (SmtpEndpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(465);
    }

    [Fact]
    public void CreateEndpoint_PlainPort()
    {
        var uri = EndpointUriParser.Parse("smtp://localhost?security=None");
        var ep = (SmtpEndpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(25);
    }

    [Fact]
    public void CreateEndpoint_WithAllSmtpOptions()
    {
        var uri = EndpointUriParser.Parse(
            "smtp://mx.corp.com?username=bot&password=s&from=noreply@corp.com&to=admin@corp.com" +
            "&subject=Alert&contentType=text/html&security=StartTls");

        var ep = (SmtpEndpoint)_sut.CreateEndpoint(uri);

        ep.Host.Should().Be("mx.corp.com");
        ep.EndpointOptions.From.Should().Be("noreply@corp.com");
        ep.EndpointOptions.To.Should().Be("admin@corp.com");
        ep.EndpointOptions.Subject.Should().Be("Alert");
        ep.EndpointOptions.ContentType.Should().Be("text/html");
        ep.EndpointOptions.Security.Should().Be(MailSecurityMode.StartTls);
    }
}
