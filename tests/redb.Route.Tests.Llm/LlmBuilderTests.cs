using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm;

public sealed class LlmBuilderTests
{
    [Fact]
    public void Factory_StartsWithLlmScheme()
    {
        LlmDsl.Factory("claude").AsUri().Should().StartWith("llm://claude");
    }

    [Fact]
    public void NullFactory_Throws()
    {
        var act = () => LlmDsl.Factory(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyFactory_Throws()
    {
        var act = () => LlmDsl.Factory(" ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Temperature_SetsParam()
    {
        LlmDsl.Factory("c").Temperature(0.25).AsUri().Should().Contain("temperature=0.25");
    }

    [Fact]
    public void MaxTokens_SetsParam()
    {
        LlmDsl.Factory("c").MaxTokens(1024).AsUri().Should().Contain("maxTokens=1024");
    }

    [Fact]
    public void TopP_SetsParam()
    {
        LlmDsl.Factory("c").TopP(0.9).AsUri().Should().Contain("topP=0.9");
    }

    [Fact]
    public void SystemPromptRef_SetsParam()
    {
        LlmDsl.Factory("c").SystemPromptRef("translate-en").AsUri().Should().Contain("systemPromptRef=translate-en");
    }

    [Fact]
    public void Conversation_FromHeader_SetsParam()
    {
        LlmDsl.Factory("c").ConversationFromHeader().AsUri().Should().Contain("conversation=header");
    }

    [Fact]
    public void Conversation_FromRoute_SetsParam()
    {
        LlmDsl.Factory("c").ConversationFromRoute().AsUri().Should().Contain("conversation=property");
    }

    [Fact]
    public void Stream_SetsTrue()
    {
        LlmDsl.Factory("c").Stream().AsUri().Should().Contain("stream=true");
    }

    [Fact]
    public void Schedule_SetsParam()
    {
        LlmDsl.Factory("c").Schedule("0 0/5 * * * ?").AsUri()
            .Should().Contain("schedule=");
    }

    [Fact]
    public void MaxIterations_SetsParam()
    {
        LlmDsl.Factory("c").MaxIterations(4).AsUri().Should().Contain("maxIterations=4");
    }

    [Fact]
    public void Combined_ProducesMultipleParams()
    {
        var uri = LlmDsl.Factory("claude")
            .Temperature(0.0)
            .MaxTokens(512)
            .ConversationFromHeader()
            .Stream()
            .AsUri();

        uri.Should().Contain("temperature=0");
        uri.Should().Contain("maxTokens=512");
        uri.Should().Contain("conversation=header");
        uri.Should().Contain("stream=true");
    }

    [Fact]
    public void ImplicitConversion_ProducesValidEndpointUri()
    {
        EndpointUri uri = LlmDsl.Factory("claude").Temperature(0.1).MaxTokens(64);
        uri.Scheme.Should().Be("llm");
        uri.Path.Should().Be("claude");
    }
}
