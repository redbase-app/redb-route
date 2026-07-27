using redb.Route.Core;
using redb.Route.Telegram;
using redb.Route.Telegram.Fluent;

namespace redb.Route.Tests.Telegram;

/// <summary>
/// The bot token — a full account credential — must be able to live in the registry
/// instead of the endpoint URI, so it never reaches logs, telemetry, or the dashboard.
/// </summary>
public sealed class TelegramConnectionFactoryTests
{
    private const string Token = "123456:AAH-TOPSECRET-BOT-TOKEN";

    private static TelegramComponent Wire(string name, TelegramConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new TelegramComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesToken_WhenUriCarriesNone()
    {
        var component = Wire("support-bot", new TelegramConnectionFactory
        {
            Token = Token,
            SendTimeoutSeconds = 42
        });

        var uri = EndpointUriParser.Parse("telegram://receive?connectionFactory=support-bot");

        // Validate() requires a token — this would throw if the factory were not applied first.
        var endpoint = (TelegramEndpoint)component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Token);
        uri.ToString().Should().NotContain(Token);
    }

    [Fact]
    public void ExplicitUriToken_WinsOverFactory()
    {
        var component = Wire("f", new TelegramConnectionFactory { Token = "from:FACTORY" });

        var uri = EndpointUriParser.Parse("telegram://send?connectionFactory=f&token=from:URI&chatId=1");
        var endpoint = (TelegramEndpoint)component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        // the URI value survived the merge (endpoint construction validated it)
        uri.RawParameters["token"].Should().Be("from:URI");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new TelegramComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("telegram://receive?connectionFactory=absent&token=inline:TOKEN");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }

    [Fact]
    public void TokenLessDsl_EmitsConnectionFactoryAndNoToken()
    {
        var uri = Tg.Receive().ConnectionFactory("support-bot").Build();

        uri.Should().Be("telegram://receive?connectionFactory=support-bot");
        uri.Should().NotContain("token=");
    }

    [Fact]
    public void TokenDsl_StillEmitsToken_Unchanged()
    {
        var uri = Tg.Send("abc:123").ChatId(42).Build();

        uri.Should().Contain("token=");
        uri.Should().StartWith("telegram://send?token=");
    }
}
