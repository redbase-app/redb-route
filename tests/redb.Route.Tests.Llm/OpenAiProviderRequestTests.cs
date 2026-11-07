using System.Net;
using System.Text.Json.Nodes;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Verifies the OpenAI-compatible request/response shape for reasoning models: DeepSeek returns its
/// chain of thought in <c>reasoning_content</c>, and the contract asks for that field back on the
/// assistant message that carries the tool call — which is the only place the field belongs.
/// </summary>
public sealed class OpenAiProviderRequestTests
{
    /// <summary>Captures the body of every request and answers with the scripted body per call.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly string[] _bodies;
        private int _calls;

        public ScriptedHandler(params string[] bodies) => _bodies = bodies;

        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            var body = _bodies[Math.Min(_calls++, _bodies.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    /// <summary>A thinking model that asks for a tool: content is null, the reasoning is beside it.</summary>
    private const string ToolCallReply =
        """
        {"id":"chatcmpl_1","choices":[{"index":0,"message":{
            "role":"assistant","content":null,
            "reasoning_content":"the user asks about order 42; call the tool",
            "tool_calls":[{"id":"call_1","type":"function",
                "function":{"name":"order_lookup","arguments":"{\"orderId\":\"42\"}"}}]},
         "finish_reason":"tool_calls"}],
         "usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
        """;

    private static LlmToolCapability LookupTool() => new()
    {
        Name = "order_lookup",
        Description = "Look up an order by id.",
        InputSchema = """{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}"""
    };

    private static OpenAiProvider Provider(HttpMessageHandler handler) =>
        new(new LlmConnectionFactory
        {
            Provider = "deepseek", ModelId = "deepseek-flash", ApiKey = "test-key"
        }, new HttpClient(handler));

    [Fact]
    public async Task DeepSeek_ToolLoop_SecondRequest_CarriesReasoningContent()
    {
        var handler = new ScriptedHandler(ToolCallReply);
        var provider = Provider(handler);

        var first = await provider.CompleteAsync(new LlmRequest
        {
            Messages = [LlmMessage.User("what is the status of order 42?")],
            Tools = [LookupTool()]
        });

        var thinking = first.Content.OfType<LlmThinkingBlock>().Should().ContainSingle(
            "reasoning_content is a block of its own, not the answer").Which;
        thinking.Text.Should().Be("the user asks about order 42; call the tool");
        thinking.Signature.Should().BeNull("DeepSeek signs nothing: the text is the whole payload");

        // Second iteration of the loop: the assistant turn goes back with the reasoning it produced.
        await provider.CompleteAsync(new LlmRequest
        {
            Messages =
            [
                LlmMessage.User("what is the status of order 42?"),
                new LlmMessage { Role = "assistant", Content = first.Content },
                new LlmMessage { Role = "user", Content = [new LlmToolResultBlock("call_1", """{"status":"shipped"}""")] }
            ],
            Tools = [LookupTool()]
        });

        var assistant = handler.Bodies[1]["messages"]!.AsArray()[1]!.AsObject();
        assistant["reasoning_content"]!.GetValue<string>()
            .Should().Be("the user asks about order 42; call the tool");
        assistant["tool_calls"]!.AsArray().Should().HaveCount(1, "the tool call travels with its reasoning");
    }

    [Fact]
    public async Task DeepSeek_WithoutTools_ReasoningIsNotSent()
    {
        var handler = new ScriptedHandler("""{"choices":[{"message":{"role":"assistant","content":"done"}}]}""");

        await Provider(handler).CompleteAsync(new LlmRequest
        {
            Messages =
            [
                LlmMessage.User("hi"),
                new LlmMessage
                {
                    Role = "assistant",
                    Content = [new LlmThinkingBlock("the user greets me"), new LlmTextBlock("done")]
                }
            ]
        });

        // Decision 19: with no tools in play the field is not part of the contract, so it stays home.
        handler.Bodies[0]["messages"]!.AsArray()[1]!.AsObject().ContainsKey("reasoning_content")
            .Should().BeFalse("reasoning_content goes back only where tools are declared");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MessageWithNothingToSay_IsNotSent(bool withTools)
    {
        // An assistant turn with no text and no tool call (the model spent the turn thinking, or answered
        // with an empty string) used to go out as {"role":"assistant","content":null}. Content may be
        // null only beside tool_calls, so that message is a refusal waiting in every later request of the
        // conversation. The turn stays in the record; it is not sent.
        var handler = new ScriptedHandler("""{"choices":[{"message":{"role":"assistant","content":"done"}}]}""");

        await Provider(handler).CompleteAsync(new LlmRequest
        {
            Messages =
            [
                LlmMessage.User("status of order 42?"),
                new LlmMessage { Role = "assistant", Content = [new LlmThinkingBlock("ran out of tokens")] },
                LlmMessage.User("and now?"),
                new LlmMessage { Role = "assistant", Content = [new LlmTextBlock(string.Empty)] },
                LlmMessage.User("still there?")
            ],
            Tools = withTools ? [LookupTool()] : []
        });

        var messages = handler.Bodies[0]["messages"]!.AsArray();
        messages.Count.Should().Be(3, "the two turns that say nothing are not sent");
        messages.Should().OnlyContain(m => m!["role"]!.GetValue<string>() == "user");
        messages.Should().OnlyContain(m => m!["content"] != null, "no message goes out with null content");
    }

    [Fact]
    public async Task DeepSeek_VisibleAnswer_IsTheContent_NotTheReasoning()
    {
        var handler = new ScriptedHandler(
            """
            {"id":"chatcmpl_2","choices":[{"index":0,"message":{
                "role":"assistant","content":"shipped",
                "reasoning_content":"the tool said shipped, so the answer is shipped"},
             "finish_reason":"stop"}],
             "usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
            """);

        var response = await Provider(handler).CompleteAsync(new LlmRequest
        {
            Messages = [LlmMessage.User("status of order 42?")]
        });

        response.Content.OfType<LlmTextBlock>().Should().ContainSingle().Which.Text.Should().Be("shipped");
        response.Content.OfType<LlmThinkingBlock>().Should().ContainSingle();
    }
}
