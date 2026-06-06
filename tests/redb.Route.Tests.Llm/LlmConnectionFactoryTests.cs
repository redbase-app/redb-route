namespace redb.Route.Tests.Llm;

public sealed class LlmConnectionFactoryTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var f = new LlmConnectionFactory();
        f.Provider.Should().Be("stub");
        f.ModelId.Should().Be("stub-model");
        f.RequestTimeoutMs.Should().Be(120_000);
        f.Retries.Should().Be(2);
    }

    [Fact]
    public void Build_StubProvider_Works()
    {
        var f = new LlmConnectionFactory { Provider = "stub", ModelId = "x" };
        var p = f.Build();
        p.Should().BeOfType<StubProvider>();
        p.ProviderId.Should().Be("stub");
        p.ModelId.Should().Be("x");
    }

    [Fact]
    public void Build_AnthropicProvider_ReturnsAnthropicType()
    {
        // Anthropic is placeholder; ctor must succeed even though CompleteAsync isn't implemented.
        var f = new LlmConnectionFactory { Provider = "anthropic", ModelId = "claude-haiku" };
        var p = f.Build();
        p.Should().BeOfType<AnthropicProvider>();
    }

    [Fact]
    public void Build_UnknownProvider_Throws()
    {
        var f = new LlmConnectionFactory { Provider = "does-not-exist" };
        var act = () => f.Build();
        act.Should().Throw<NotSupportedException>().WithMessage("*Unknown LLM provider*");
    }

    [Fact]
    public void Build_PrebuiltProvider_TakesPrecedence()
    {
        var prebuilt = new StubProvider(new LlmConnectionFactory { ModelId = "x" }) { FixedReply = "pre" };
        var f = new LlmConnectionFactory
        {
            Provider = "does-not-exist", // ignored
            PrebuiltProvider = prebuilt
        };
        f.Build().Should().BeSameAs(prebuilt);
    }

    [Fact]
    public void ProviderName_IsCaseInsensitive()
    {
        var f = new LlmConnectionFactory { Provider = "STUB" };
        f.Build().Should().BeOfType<StubProvider>();
    }
}
