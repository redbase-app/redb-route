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
        opts.Stream.Should().Be(LlmStreamMode.Off);
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
        var opts = Bind("llm://c?conversation=header&stream=calls");
        opts.Conversation.Should().Be("header");
        opts.Stream.Should().Be(LlmStreamMode.Calls);
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

    // The core binder leaves an unknown name, or a value that does not convert, among the unmapped
    // parameters, where nothing reads it: a typo used to drop the option without a word.

    [Fact]
    public void Validate_UnknownOption_IsRefused_WithTheNearestName()
    {
        var opts = Bind("llm://c?tempreature=0.2");

        var act = () => opts.Validate();

        act.Should().Throw<ArgumentException>("a misspelt option would silently run with the default")
            .Which.Message.Should().Contain("tempreature").And.Contain("temperature", "the nearest option is named");
    }

    [Fact]
    public void Validate_ValueThatDoesNotConvert_IsRefused()
    {
        var opts = Bind("llm://c?maxIterations=abc");

        var act = () => opts.Validate();

        act.Should().Throw<ArgumentException>("the option would silently keep its default")
            .Which.Message.Should().Contain("maxIterations").And.Contain("abc");
    }

    [Fact]
    public void EveryDslOption_IsAnOptionTheEndpointKnows()
    {
        var uri = redb.Route.Llm.Fluent.Llm.Factory("c")
            .Temperature(0.2).MaxTokens(512).TopP(0.9)
            .SystemPromptRef("#sys").ConversationFromHeader().Stream(LlmStreamMode.Calls)
            .Schedule("30s").InitialBody("#first").MaxIterations(4)
            .Budget(inputTokens: 100, outputTokens: 50, costUsd: 0.5m).CacheSystemPrompt()
            .Tools("*").User("${header.userId}").Audit("tenant", "acme")
            .PropagateToolHeaders("x-tenant-id").PromptTemplate("triage", "v3")
            .AsUri() + "&redb=main&connectionFactory=c";

        var opts = Bind(uri);

        opts.UnmappedParameters.Should().BeEmpty("every option the DSL writes is one the endpoint reads");
        opts.Invoking(o => o.Validate()).Should().NotThrow();
    }
}
