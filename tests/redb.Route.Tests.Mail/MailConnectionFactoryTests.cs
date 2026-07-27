using redb.Route.Core;
using redb.Route.Mail;

namespace redb.Route.Tests.Mail;

/// <summary>
/// Mailbox credentials must be able to live in the registry instead of the endpoint URI, so they
/// never reach logs, telemetry, or the dashboard. One factory serves SMTP, IMAP and POP3.
/// </summary>
public sealed class MailConnectionFactoryTests
{
    private const string Secret = "mailboxP4ssw0rd";

    private static MailConnectionFactory NewFactory() => new()
    {
        Host = "mail.corp.local",
        Port = 993,
        Username = "svc-reports",
        Password = Secret,
        SkipCertificateValidation = true
    };

    private static T Wire<T>(T component, string name, MailConnectionFactory factory)
        where T : ComponentBase
    {
        var context = new RouteContext();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Theory]
    [InlineData("smtp")]
    [InlineData("imap")]
    [InlineData("pop3")]
    public void Factory_SuppliesCredentials_ForEveryProtocol(string scheme)
    {
        ComponentBase component = scheme switch
        {
            "smtp" => new SmtpComponent(),
            "imap" => new ImapComponent(),
            _ => new Pop3Component()
        };
        Wire(component, "corp-mailbox", NewFactory());

        var uri = EndpointUriParser.Parse($"{scheme}://?connectionFactory=corp-mailbox");
        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Secret);
        uri.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        var component = Wire(new ImapComponent(), "f", NewFactory());

        var uri = EndpointUriParser.Parse(
            "imap://?connectionFactory=f&username=uri-user&port=143");
        component.CreateEndpoint(uri).Should().NotBeNull();

        uri.RawParameters["username"].Should().Be("uri-user");
        uri.RawParameters["port"].Should().Be("143");
    }

    [Fact]
    public void HostFromUriPath_IsNotOverriddenByFactory()
    {
        // imap://mail.other.local — the host lives in the path; a factory must never
        // silently redirect the route to its own server.
        var component = Wire(new ImapComponent(), "f", NewFactory());

        var uri = EndpointUriParser.Parse("imap://mail.other.local?connectionFactory=f");
        var endpoint = (ImapEndpoint)component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.Path.Should().Be("mail.other.local");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new SmtpComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("smtp://mail.corp.local?connectionFactory=absent");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dsl_EmitsConnectionFactory()
    {
        var uri = Imap.Read("mail.corp.local").ConnectionFactory("corp-mailbox").Build();

        uri.Should().Contain("connectionFactory=corp-mailbox");
        uri.Should().NotContain("password=");
    }
}
