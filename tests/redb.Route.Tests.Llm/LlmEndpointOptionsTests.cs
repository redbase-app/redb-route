namespace redb.Route.Tests.Llm;

public sealed class LlmEndpointOptionsTests
{
    private static LlmEndpointOptions Bind(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        var opts = new LlmEndpointOptions();
        opts.BindFromUri(uri.RawParameters);
        return opts;
    }

    [Fact]
    public void Defaults_AreSet()
    {
        var opts = new LlmEndpointOptions();
        opts.MaxIterations.Should().Be(8);
        opts.Conversation.Should().Be("none");
        opts.Stream.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_PullsTemperatureAndMaxTokens()
    {
        var opts = Bind("llm://c?temperature=0.3&maxTokens=512");
        opts.Temperature.Should().Be(0.3);
        opts.MaxTokens.Should().Be(512);
    }

    [Fact]
    public void BindFromUri_PullsConversationAndStream()
    {
        var opts = Bind("llm://c?conversation=header&stream=true");
        opts.Conversation.Should().Be("header");
        opts.Stream.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_PullsSystemPromptRef()
    {
        var opts = Bind("llm://c?systemPromptRef=translate-en");
        opts.SystemPromptRef.Should().Be("translate-en");
    }

    [Fact]
    public void BindFromUri_PullsMaxIterations()
    {
        var opts = Bind("llm://c?maxIterations=3");
        opts.MaxIterations.Should().Be(3);
    }

    [Fact]
    public void Validate_NegativeIterations_Throws()
    {
        var opts = new LlmEndpointOptions { MaxIterations = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_TemperatureOutOfRange_Throws()
    {
        var opts = new LlmEndpointOptions { Temperature = 3.0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_ValidConfig_DoesNotThrow()
    {
        var opts = new LlmEndpointOptions { Temperature = 0.5, MaxIterations = 4 };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void BindFromUri_PullsUser()
    {
        var opts = Bind("llm://c?user=system");
        opts.User.Should().Be("system");
    }

    [Fact]
    public void BindFromUri_PullsUserHeaderExpression()
    {
        var opts = Bind("llm://c?user=" + System.Web.HttpUtility.UrlEncode("${header.X-User-Id}"));
        opts.User.Should().Be("${header.X-User-Id}");
    }

    [Fact]
    public void BindFromUri_PullsAuditCsv()
    {
        var opts = Bind("llm://c?audit=tier%3Dgold%2Cbucket%3DA");
        opts.Audit.Should().Be("tier=gold,bucket=A");
    }

    [Fact]
    public void BindFromUri_PullsPromptTemplateNameAndVersion()
    {
        var opts = Bind("llm://c?promptTemplateName=triage&promptTemplateVersion=v3");
        opts.PromptTemplateName.Should().Be("triage");
        opts.PromptTemplateVersion.Should().Be("v3");
    }
}
