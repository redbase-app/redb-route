using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FcmDslTests
{
    [Fact]
    public void Token_GeneratesBasicUri()
    {
        string uri = Fcm.Token("device-token-123");
        uri.Should().Contain("fcm://send?");
        uri.Should().Contain("messageType=Token");
        uri.Should().Contain("token=device-token-123");
    }

    [Fact]
    public void Topic_GeneratesBasicUri()
    {
        string uri = Fcm.Topic("news");
        uri.Should().Contain("messageType=Topic");
        uri.Should().Contain("topic=news");
    }

    [Fact]
    public void Condition_GeneratesBasicUri()
    {
        string uri = Fcm.Condition("'sports' in topics");
        uri.Should().Contain("messageType=Condition");
        uri.Should().Contain("condition=");
    }

    [Fact]
    public void Builder_WithAllOptions_GeneratesFullUri()
    {
        string uri = Fcm.Token("tok")
            .CredentialPath("/secrets/sa.json")
            .ProjectId("my-project")
            .Title("Hello")
            .Body("World")
            .ImageUrl("https://example.com/img.png")
            .DataOnly()
            .DryRun()
            .AndroidPriority("high")
            .AndroidTtlSeconds(3600)
            .AndroidChannelId("channel1")
            .ApnsPriority("10")
            .ApnsCollapseId("group1")
            .WebPushLink("https://example.com")
            .ConnectionFactory("myFactory");

        uri.Should().Contain("credentialPath=");
        uri.Should().Contain("projectId=my-project");
        uri.Should().Contain("title=Hello");
        uri.Should().Contain("body=World");
        uri.Should().Contain("imageUrl=");
        uri.Should().Contain("dataOnly=True");
        uri.Should().Contain("dryRun=True");
        uri.Should().Contain("androidPriority=high");
        uri.Should().Contain("androidTtlSeconds=3600");
        uri.Should().Contain("androidChannelId=channel1");
        uri.Should().Contain("apnsPriority=10");
        uri.Should().Contain("apnsCollapseId=group1");
        uri.Should().Contain("webPushLink=");
        uri.Should().Contain("connectionFactory=myFactory");
    }

    [Fact]
    public void Builder_ImplicitStringConversion()
    {
        FcmBuilder builder = Fcm.Token("tok");
        string uri = builder;
        uri.Should().StartWith("fcm://send?");
    }

    [Fact]
    public void Builder_ToString_EqualsImplicit()
    {
        var builder = Fcm.Token("tok").Title("T");
        builder.ToString().Should().Be((string)builder);
    }
}
