using redb.Route.Core;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

public sealed class ImapComponentTests
{
    private readonly ImapComponent _sut = new();

    [Fact]
    public void Scheme_ReturnsImap()
    {
        _sut.Scheme.Should().Be("imap");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsImapEndpoint()
    {
        var uri = EndpointUriParser.Parse("imap://imap.example.com?username=u&password=p");

        var endpoint = _sut.CreateEndpoint(uri);

        endpoint.Should().BeOfType<ImapEndpoint>();
        var ep = (ImapEndpoint)endpoint;
        ep.Host.Should().Be("imap.example.com");
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
        var uri = EndpointUriParser.Parse("imap://localhost");
        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);

        var act = () => ep.CreateProducer();

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void CreateEndpoint_ConsumerCreated()
    {
        var uri = EndpointUriParser.Parse("imap://localhost?username=u&password=p");
        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);
        var processor = Substitute.For<redb.Route.Abstractions.IProcessor>();

        var consumer = ep.CreateConsumer(processor);

        consumer.Should().BeOfType<ImapConsumer>();
    }

    [Fact]
    public void CreateEndpoint_DefaultImapSslPort()
    {
        var uri = EndpointUriParser.Parse("imap://localhost");
        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(993);
    }

    [Fact]
    public void CreateEndpoint_PlainImapPort()
    {
        var uri = EndpointUriParser.Parse("imap://localhost?security=None");
        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(143);
    }

    [Fact]
    public void CreateEndpoint_ExplicitPort()
    {
        var uri = EndpointUriParser.Parse("imap://localhost?port=3143");
        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);

        ep.Port.Should().Be(3143);
    }

    [Fact]
    public void CreateEndpoint_WithConsumerOptions()
    {
        var uri = EndpointUriParser.Parse(
            "imap://imap.corp.com?username=inbox@corp.com&password=s" +
            "&folder=Alerts&fetchFilter=All&maxMessages=10&postProcess=MarkRead" +
            "&idle=true&idleTimeout=1740000&peek=true");

        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);

        ep.EndpointOptions.Folder.Should().Be("Alerts");
        ep.EndpointOptions.FetchFilter.Should().Be(MailFetchFilter.All);
        ep.EndpointOptions.MaxMessages.Should().Be(10);
        ep.EndpointOptions.PostProcess.Should().Be(PostProcessAction.MarkRead);
        ep.EndpointOptions.Idle.Should().BeTrue();
        ep.EndpointOptions.IdleTimeout.Should().Be(1_740_000);
        ep.EndpointOptions.Peek.Should().BeTrue();
    }

    [Fact]
    public void CreateEndpoint_MovePostProcess_RequiresMoveTo()
    {
        var uri = EndpointUriParser.Parse("imap://localhost?postProcess=Move");

        var act = () => _sut.CreateEndpoint(uri);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("MoveTo");
    }

    [Fact]
    public void CreateEndpoint_MovePostProcess_WithMoveTo_Succeeds()
    {
        var uri = EndpointUriParser.Parse("imap://localhost?postProcess=Move&moveTo=Archive");

        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);

        ep.EndpointOptions.PostProcess.Should().Be(PostProcessAction.Move);
        ep.EndpointOptions.MoveTo.Should().Be("Archive");
    }

    [Fact]
    public void CreateEndpoint_IdempotentOption()
    {
        var uri = EndpointUriParser.Parse("imap://localhost?idempotent=true");
        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);

        ep.EndpointOptions.Idempotent.Should().BeTrue();
    }

    [Fact]
    public void CreateEndpoint_AdditionalFolders()
    {
        var uri = EndpointUriParser.Parse(
            "imap://localhost?additionalFolders=Sent,Drafts");
        var ep = (ImapEndpoint)_sut.CreateEndpoint(uri);

        ep.EndpointOptions.AdditionalFolders.Should().Be("Sent,Drafts");
    }
}
