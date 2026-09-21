using System.Net;
using System.Text;

namespace redb.Route.Tests.Llm;

/// <summary>
/// A streamed call assembles exactly the answer a non-streaming call returns for it: the same blocks in the same order
/// (thinking with its signature, text, tool calls), the same usage, stop reason, raw stop reason and response id. The
/// agent engine runs its loop, its store and the hand-back of thinking on that answer, so the two forms must not
/// differ. Each test serves one answer twice, as the JSON of a plain call and as the SSE of a streamed one.
/// </summary>
public sealed class ProviderStreamParityTests
{
    private sealed class FixedHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }

    private static HttpClient Serving(string body, string mediaType) => new(new FixedHandler(body, mediaType));

    private static LlmConnectionFactory Factory(string provider, string model) =>
        new() { Provider = provider, ModelId = model, ApiKey = "sk-test" };

    private static LlmRequest Request() => new() { Messages = [LlmMessage.User("hi")] };

    /// <summary>SSE lines joined the way a server writes them.</summary>
    private static string Sse(params string[] lines) => string.Join("\n", lines) + "\n";

    private static async Task<List<LlmStreamChunk>> DrainAsync(ILlmProvider provider)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in provider.StreamAsync(Request()))
            chunks.Add(chunk);
        return chunks;
    }

    private static void ShouldBeTheSameAnswer(LlmResponse? streamed, LlmResponse plain)
    {
        streamed.Should().NotBeNull("the last chunk carries the assembled answer");
        streamed!.Content.Should().Equal(plain.Content, "blocks, their order and their payloads match the plain call");
        streamed.StopReason.Should().Be(plain.StopReason);
        streamed.RawStopReason.Should().Be(plain.RawStopReason);
        streamed.Usage.Should().Be(plain.Usage);
        streamed.ProviderResponseId.Should().Be(plain.ProviderResponseId);
        streamed.ProviderSystemFingerprint.Should().Be(plain.ProviderSystemFingerprint);
    }

    // ── OpenAI-compatible (DeepSeek shape: reasoning_content beside the answer) ──

    private const string DeepSeekJson = """
        {"id":"chatcmpl-1","system_fingerprint":"fp_1","choices":[{"index":0,"message":{"role":"assistant","content":"Hello world","reasoning_content":"Let me think.","tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"q\":\"x\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}
        """;

    private static readonly string DeepSeekSse = Sse(
        """data: {"id":"chatcmpl-1","system_fingerprint":"fp_1","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Let me "}}]}""",
        "",
        ": keep-alive",
        "",
        """data: {"id":"chatcmpl-1","system_fingerprint":"fp_1","choices":[{"index":0,"delta":{"reasoning_content":"think."}}]}""",
        "",
        """data: {"id":"chatcmpl-1","system_fingerprint":"fp_1","choices":[{"index":0,"delta":{"content":"Hello "}}]}""",
        "",
        """data: {"id":"chatcmpl-1","system_fingerprint":"fp_1","choices":[{"index":0,"delta":{"content":"world"}}]}""",
        "",
        """data: {"id":"chatcmpl-1","system_fingerprint":"fp_1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"q\""}}]}}]}""",
        "",
        """data: {"id":"chatcmpl-1","system_fingerprint":"fp_1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"x\"}"}}]}}]}""",
        "",
        """data: {"id":"chatcmpl-1","system_fingerprint":"fp_1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        "",
        """data: {"id":"chatcmpl-1","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5}}""",
        "",
        "data: [DONE]");

    [Fact]
    public async Task OpenAi_Stream_AssemblesTheSameAnswerAsAPlainCall()
    {
        var plain = await new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Serving(DeepSeekJson, "application/json"))
            .CompleteAsync(Request());
        var chunks = await DrainAsync(
            new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Serving(DeepSeekSse, "text/event-stream")));

        plain.Content.OfType<LlmThinkingBlock>().Should().ContainSingle("the fixture carries reasoning_content");
        ShouldBeTheSameAnswer(chunks[^1].Response, plain);
    }

    [Fact]
    public async Task OpenAi_Stream_PiecesArriveAsTheyCome_ThinkingApartFromText()
    {
        var chunks = await DrainAsync(
            new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Serving(DeepSeekSse, "text/event-stream")));

        var pieces = chunks.Take(chunks.Count - 1).SelectMany(c => c.Content).ToList();
        pieces.OfType<LlmTextBlock>().Select(t => t.Text).Should().Equal("Hello ", "world");
        pieces.OfType<LlmThinkingBlock>().Select(t => t.Text).Should().Equal(
            ["Let me ", "think."], "the thinking is a piece of its own kind, never visible text");
    }

    [Fact]
    public async Task OpenAi_Stream_MalformedFrame_Fails()
    {
        var sse = Sse("""data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"Hel""", "", "data: [DONE]");

        var thrown = await Record.ExceptionAsync(() => DrainAsync(
            new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Serving(sse, "text/event-stream"))));

        thrown.Should().NotBeNull("a frame that cannot be read is lost text; skipping it would hand on a shorter answer as whole");
        thrown!.Message.Should().Contain("\"content\":\"Hel", "the message shows the frame");
    }

    [Fact]
    public async Task OpenAi_Stream_ErrorFrame_Fails()
    {
        var sse = Sse("""data: {"error":{"message":"the model is overloaded","type":"server_error"}}""", "", "data: [DONE]");

        var thrown = await Record.ExceptionAsync(() => DrainAsync(
            new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Serving(sse, "text/event-stream"))));

        thrown.Should().NotBeNull("an error reported inside the stream is a failed call, not an empty answer");
        thrown!.Message.Should().Contain("the model is overloaded");
    }

    [Fact]
    public async Task OpenAi_Stream_EndingWithoutFinishReason_Fails()
    {
        var sse = Sse(
            """data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"Hello"}}]}""", "",
            "data: [DONE]");

        var thrown = await Record.ExceptionAsync(() => DrainAsync(
            new OpenAiProvider(Factory("deepseek", "deepseek-flash"), Serving(sse, "text/event-stream"))));

        thrown.Should().BeOfType<InvalidOperationException>(
            "a stream that ended before the model said why it stopped is a cut answer, not a finished one");
    }

    // ── Anthropic ──

    private const string AnthropicJson = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-4-8","content":[{"type":"thinking","thinking":"Let me think.","signature":"sig-abc"},{"type":"redacted_thinking","data":"opaque=="},{"type":"text","text":"Hello world"},{"type":"tool_use","id":"toolu_1","name":"lookup","input":{"q":"x"}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":3,"cache_read_input_tokens":7}}
        """;

    private static readonly string AnthropicSse = Sse(
        "event: message_start",
        """data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-4-8","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1,"cache_creation_input_tokens":3,"cache_read_input_tokens":7}}}""",
        "",
        "event: content_block_start",
        """data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        "",
        "event: content_block_delta",
        """data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me "}}""",
        "",
        "event: content_block_delta",
        """data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"think."}}""",
        "",
        "event: content_block_delta",
        """data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc"}}""",
        "",
        "event: content_block_stop",
        """data: {"type":"content_block_stop","index":0}""",
        "",
        "event: content_block_start",
        """data: {"type":"content_block_start","index":1,"content_block":{"type":"redacted_thinking","data":"opaque=="}}""",
        "",
        "event: content_block_stop",
        """data: {"type":"content_block_stop","index":1}""",
        "",
        "event: content_block_start",
        """data: {"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}""",
        "",
        "event: content_block_delta",
        """data: {"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Hello "}}""",
        "",
        "event: ping",
        """data: {"type":"ping"}""",
        "",
        "event: content_block_delta",
        """data: {"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"world"}}""",
        "",
        "event: content_block_stop",
        """data: {"type":"content_block_stop","index":2}""",
        "",
        "event: content_block_start",
        """data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_1","name":"lookup","input":{}}}""",
        "",
        "event: content_block_delta",
        """data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"{\"q\":"}}""",
        "",
        "event: content_block_delta",
        """data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"\"x\"}"}}""",
        "",
        "event: content_block_stop",
        """data: {"type":"content_block_stop","index":3}""",
        "",
        "event: message_delta",
        """data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":5}}""",
        "",
        "event: message_stop",
        """data: {"type":"message_stop"}""");

    [Fact]
    public async Task Anthropic_Stream_AssemblesTheSameAnswerAsAPlainCall()
    {
        var plain = await new AnthropicProvider(Factory("anthropic", "claude-opus-4-8"), Serving(AnthropicJson, "application/json"))
            .CompleteAsync(Request());
        var chunks = await DrainAsync(
            new AnthropicProvider(Factory("anthropic", "claude-opus-4-8"), Serving(AnthropicSse, "text/event-stream")));

        plain.Content.OfType<LlmThinkingBlock>().Should().HaveCount(2, "the fixture carries a signed and a redacted thinking block");
        ShouldBeTheSameAnswer(chunks[^1].Response, plain);
    }

    [Fact]
    public async Task Anthropic_Stream_PiecesArriveAsTheyCome_ThinkingApartFromText()
    {
        var chunks = await DrainAsync(
            new AnthropicProvider(Factory("anthropic", "claude-opus-4-8"), Serving(AnthropicSse, "text/event-stream")));

        var pieces = chunks.Take(chunks.Count - 1).SelectMany(c => c.Content).ToList();
        pieces.OfType<LlmTextBlock>().Select(t => t.Text).Should().Equal("Hello ", "world");
        pieces.OfType<LlmThinkingBlock>().Select(t => t.Text).Should().Equal(
            ["Let me ", "think."], "the thinking is a piece of its own kind, never visible text");
    }

    [Fact]
    public async Task Anthropic_Stream_MalformedFrame_Fails()
    {
        var sse = Sse(
            "event: message_start",
            """data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":1,"output_tokens":1""");

        var thrown = await Record.ExceptionAsync(() => DrainAsync(
            new AnthropicProvider(Factory("anthropic", "claude-opus-4-8"), Serving(sse, "text/event-stream"))));

        thrown.Should().NotBeNull("a frame that cannot be read must not be skipped");
        thrown!.Message.Should().Contain("\"message_start\"", "the message shows the frame");
    }

    [Fact]
    public async Task Anthropic_Stream_EndingWithoutStopReason_Fails()
    {
        var sse = Sse(
            "event: message_start",
            """data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"usage":{"input_tokens":1,"output_tokens":1}}}""",
            "",
            "event: content_block_start",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""");

        var thrown = await Record.ExceptionAsync(() => DrainAsync(
            new AnthropicProvider(Factory("anthropic", "claude-opus-4-8"), Serving(sse, "text/event-stream"))));

        thrown.Should().BeOfType<InvalidOperationException>(
            "a stream that ended before the model said why it stopped is a cut answer, not a finished one");
    }

    // ── Providers without a stream of their own ──

    /// <summary>Implements only the plain call; streaming comes from the interface's default.</summary>
    private sealed class PlainOnlyProvider(LlmResponse response) : ILlmProvider
    {
        public string ProviderId => "plain";
        public string ModelId => "plain-model";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) => Task.FromResult(response);
    }

    [Fact]
    public async Task DefaultStream_CarriesTheAnswer()
    {
        var answer = new LlmResponse
        {
            Content = [new LlmTextBlock("done")],
            StopReason = LlmStopReason.EndTurn,
            Usage = new LlmUsage(3, 2),
            RawStopReason = "stop",
            ProviderResponseId = "r-1"
        };

        var chunks = await DrainAsync(new PlainOnlyProvider(answer));

        chunks[^1].Response.Should().BeSameAs(answer, "a provider that cannot stream still hands the engine its answer");
    }
}
