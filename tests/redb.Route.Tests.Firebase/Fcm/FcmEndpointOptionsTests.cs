using redb.Route.Core;
using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

[Collection("FirebaseEnvSensitive")]
public sealed class FcmEndpointOptionsTests
{
    // ── BindFromUri ──

    [Fact]
    public void BindFromUri_MessageType_Parsed()
    {
        var options = new FcmEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["messageType"] = "Topic",
            ["topic"] = "news"
        });
        options.MessageType.Should().Be(FcmMessageType.Topic);
    }

    [Fact]
    public void BindFromUri_Token_Parsed()
    {
        var options = new FcmEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["credentialPath"] = "/sa.json",
            ["token"] = "device-123"
        });
        options.Token.Should().NotBeNull();
    }

    [Fact]
    public void BindFromUri_CredentialPath_Parsed()
    {
        var options = new FcmEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["credentialPath"] = "/path/to/sa.json", ["token"] = "t" });
        options.CredentialPath.Should().Be("/path/to/sa.json");
    }

    [Fact]
    public void BindFromUri_ProjectId_Parsed()
    {
        var options = new FcmEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["projectId"] = "my-proj", ["credentialPath"] = "/sa.json", ["token"] = "t" });
        options.ProjectId.Should().Be("my-proj");
    }

    [Fact]
    public void BindFromUri_DataOnly_Parsed()
    {
        var options = new FcmEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["credentialPath"] = "/sa.json", ["dataOnly"] = "true", ["token"] = "t" });
        options.DataOnly.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_DryRun_Parsed()
    {
        var options = new FcmEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["credentialPath"] = "/sa.json", ["dryRun"] = "true", ["token"] = "t" });
        options.DryRun.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_AndroidTtlSeconds_Parsed()
    {
        var options = new FcmEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["credentialPath"] = "/sa.json", ["androidTtlSeconds"] = "3600", ["token"] = "t" });
        options.AndroidTtlSeconds.Should().Be(3600);
    }

    // ── Validate ──

    [Fact]
    public void Validate_NoCredential_NoEnvVar_Throws()
    {
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", null);
        var options = new FcmEndpointOptions
        {
            MessageType = FcmMessageType.Token,
            Token = DynamicValue<string>.FromStatic("tok")
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*CredentialPath*");
    }

    [Fact]
    public void Validate_WithCredentialPath_DoesNotThrowOnCredential()
    {
        var options = new FcmEndpointOptions
        {
            CredentialPath = "/sa.json",
            MessageType = FcmMessageType.Token,
            Token = DynamicValue<string>.FromStatic("tok")
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithConnectionFactory_DoesNotThrowOnCredential()
    {
        var options = new FcmEndpointOptions
        {
            ConnectionFactory = "factory1",
            MessageType = FcmMessageType.Token,
            Token = DynamicValue<string>.FromStatic("tok")
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_TokenType_NoToken_Throws()
    {
        var options = new FcmEndpointOptions
        {
            CredentialPath = "/sa.json",
            MessageType = FcmMessageType.Token
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Token*");
    }

    [Fact]
    public void Validate_TopicType_NoTopic_Throws()
    {
        var options = new FcmEndpointOptions
        {
            CredentialPath = "/sa.json",
            MessageType = FcmMessageType.Topic
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Topic*");
    }

    [Fact]
    public void Validate_ConditionType_NoCondition_Throws()
    {
        var options = new FcmEndpointOptions
        {
            CredentialPath = "/sa.json",
            MessageType = FcmMessageType.Condition
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Condition*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_NegativeAndroidTtl_Throws(int ttl)
    {
        var options = new FcmEndpointOptions
        {
            CredentialPath = "/sa.json",
            MessageType = FcmMessageType.Token,
            Token = DynamicValue<string>.FromStatic("tok"),
            AndroidTtlSeconds = ttl
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*AndroidTtlSeconds*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3600)]
    public void Validate_ValidAndroidTtl_DoesNotThrow(int ttl)
    {
        var options = new FcmEndpointOptions
        {
            CredentialPath = "/sa.json",
            MessageType = FcmMessageType.Token,
            Token = DynamicValue<string>.FromStatic("tok"),
            AndroidTtlSeconds = ttl
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    // ── Defaults ──

    [Fact]
    public void DefaultMessageType_IsToken()
    {
        new FcmEndpointOptions().MessageType.Should().Be(FcmMessageType.Token);
    }

    [Fact]
    public void DefaultDataOnly_IsFalse()
    {
        new FcmEndpointOptions().DataOnly.Should().BeFalse();
    }

    [Fact]
    public void DefaultDryRun_IsFalse()
    {
        new FcmEndpointOptions().DryRun.Should().BeFalse();
    }
}
