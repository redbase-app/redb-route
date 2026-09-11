using System.Net;
using System.Text.Json.Nodes;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Verifies the shape of the outgoing Anthropic request body — in particular that the
/// sampling knobs (temperature/top_p) are emitted only for models whose contract accepts
/// them. Current-generation models reject those fields with HTTP 400, so a configured
/// <c>Temperature</c> pointed at a modern model must NOT appear on the wire.
/// </summary>
public sealed class AnthropicProviderRequestTests
{
    /// <summary>Captures the JSON body of the last request and returns a canned Anthropic reply.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public JsonObject? LastBody { get; private set; }
        public Version? LastVersion { get; private set; }
        public HttpVersionPolicy? LastVersionPolicy { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastBody = JsonNode.Parse(raw) as JsonObject;
            LastVersion = request.Version;
            LastVersionPolicy = request.VersionPolicy;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"id":"msg_1","content":[{"type":"text","text":"ok"}],
                     "stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
                    """)
            };
        }
    }

    private static async Task<JsonObject> CaptureBodyAsync(
        string modelId, double? temperature, double? topP,
        string? tierOverride = null, string? requestModelId = null)
    {
        var handler = new CapturingHandler();
        var factory = new LlmConnectionFactory
        {
            Provider = "anthropic",
            ModelId = modelId,
            ApiKey = "test-key",
            ModelContractTier = tierOverride
        };
        var provider = new AnthropicProvider(factory, new HttpClient(handler));

        await provider.CompleteAsync(new LlmRequest
        {
            ModelId = requestModelId,
            Messages = [LlmMessage.User("hi")],
            Temperature = temperature,
            TopP = topP
        });

        return handler.LastBody!;
    }

    [Fact]
    public async Task SystemPrompt_IsAPlainString_UntilCachingIsAskedFor()
    {
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(
            new LlmConnectionFactory { Provider = "anthropic", ModelId = "claude-sonnet-4-6", ApiKey = "k" },
            new HttpClient(handler));

        await provider.CompleteAsync(new LlmRequest
        {
            SystemPrompt = "constitution",
            Messages = [LlmMessage.User("hi")],
        });

        // Default shape unchanged: a bare string. Switching everyone to the block form would be a
        // silent wire change for callers who never asked for caching.
        handler.LastBody!["system"]!.GetValue<string>().Should().Be("constitution");
        (handler.LastBody!["system"] is System.Text.Json.Nodes.JsonArray).Should().BeFalse();
    }

    [Fact]
    public async Task CacheSystemPrompt_EmitsTheBlockFormWithCacheControl()
    {
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(
            new LlmConnectionFactory { Provider = "anthropic", ModelId = "claude-sonnet-4-6", ApiKey = "k" },
            new HttpClient(handler));

        await provider.CompleteAsync(new LlmRequest
        {
            SystemPrompt = "constitution",
            CacheSystemPrompt = true,
            Messages = [LlmMessage.User("hi")],
        });

        // cache_control is a property OF a content block — a bare string has nowhere to hang it,
        // so asking for caching necessarily switches the shape.
        var system = handler.LastBody!["system"]!.AsArray();
        system.Count.Should().Be(1);
        system[0]!["type"]!.GetValue<string>().Should().Be("text");
        system[0]!["text"]!.GetValue<string>().Should().Be("constitution");
        system[0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
    }

    [Fact]
    public async Task CacheBreakpoint_OnAMessage_MarksItsLastBlock_AndNothingElse()
    {
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(
            new LlmConnectionFactory { Provider = "anthropic", ModelId = "claude-sonnet-4-6", ApiKey = "k" },
            new HttpClient(handler));

        await provider.CompleteAsync(new LlmRequest
        {
            SystemPrompt = "constitution",
            CacheSystemPrompt = true,
            Messages =
            [
                LlmMessage.User("read it?"),
                new LlmMessage
                {
                    Role = "assistant",
                    Content = [new LlmTextBlock("part one"), new LlmTextBlock("part two")],
                    CacheBreakpoint = true
                },
                LlmMessage.User("hi"),
            ],
        });

        var messages = handler.LastBody!["messages"]!.AsArray();
        messages.Count.Should().Be(3);

        // The breakpoint sits on the LAST block of the marked message — the cached prefix runs up
        // to it inclusive — and on no other block of any other message.
        var marked = messages[1]!["content"]!.AsArray();
        marked.Count.Should().Be(2);
        marked[0]!.AsObject().ContainsKey("cache_control").Should().BeFalse();
        marked[1]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");

        messages[0]!["content"]!.AsArray()[0]!.AsObject().ContainsKey("cache_control").Should().BeFalse();
        messages[2]!["content"]!.AsArray()[0]!.AsObject().ContainsKey("cache_control").Should().BeFalse();

        // Two breakpoints in the request: system and the marked message. Anthropic allows four.
        handler.LastBody!["system"]!.AsArray()[0]!["cache_control"].Should().NotBeNull();
    }

    [Fact]
    public async Task ModernModel_DropsSamplingKnobs()
    {
        // The bug: connector sent temperature/top_p to a modern model → HTTP 400.
        var body = await CaptureBodyAsync("claude-opus-4-8", temperature: 0.7, topP: 0.9);

        body.ContainsKey("temperature").Should().BeFalse("modern models reject temperature (400)");
        body.ContainsKey("top_p").Should().BeFalse("modern models reject top_p (400)");
    }

    [Fact]
    public async Task Claude3_KeepsBothSamplingKnobs()
    {
        // Claude 3.x is the only generation that accepts temperature AND top_p together.
        var body = await CaptureBodyAsync("claude-3-5-sonnet-20241022", temperature: 0.5, topP: 0.8);

        body["temperature"]!.GetValue<double>().Should().Be(0.5);
        body["top_p"]!.GetValue<double>().Should().Be(0.8);
    }

    [Fact]
    public async Task Claude4x_WithBothKnobs_KeepsOnlyTemperature()
    {
        // Claude 4.0–4.6 reject temperature+top_p together (400); keep temperature, drop top_p.
        var body = await CaptureBodyAsync("claude-sonnet-4-6", temperature: 0.5, topP: 0.8);

        body["temperature"]!.GetValue<double>().Should().Be(0.5);
        body.ContainsKey("top_p").Should().BeFalse("Claude 4.x accepts at most one sampling knob");
    }

    [Fact]
    public async Task Claude4x_WithOnlyTopP_KeepsTopP()
    {
        var body = await CaptureBodyAsync("claude-haiku-4-5", temperature: null, topP: 0.8);

        body["top_p"]!.GetValue<double>().Should().Be(0.8);
        body.ContainsKey("temperature").Should().BeFalse();
    }

    [Fact]
    public async Task TierOverride_ForcesLegacyOnModernId()
    {
        var body = await CaptureBodyAsync("claude-opus-5", temperature: 0.3, topP: null, tierOverride: "legacy");

        body["temperature"]!.GetValue<double>().Should().Be(0.3);
    }

    [Fact]
    public async Task PerRequestModelOverride_IsClassifiedFresh()
    {
        // Factory is a legacy model (cached profile → sampling allowed), but the request
        // overrides to a modern id — the per-request resolve must win and drop sampling.
        var body = await CaptureBodyAsync(
            "claude-haiku-4-5", temperature: 0.5, topP: null, requestModelId: "claude-opus-4-8");

        body["model"]!.GetValue<string>().Should().Be("claude-opus-4-8");
        body.ContainsKey("temperature").Should().BeFalse("the overridden modern model rejects sampling");
    }

    [Fact]
    public async Task ModernModel_WithoutSampling_StillWorks()
    {
        var body = await CaptureBodyAsync("claude-opus-4-8", temperature: null, topP: null);

        body.ContainsKey("temperature").Should().BeFalse();
        body["model"]!.GetValue<string>().Should().Be("claude-opus-4-8");
    }

    [Fact]
    public void DefaultTransport_UsesHttp2_WithKeepAlivePings()
    {
        // A non-streaming completion is tens of seconds of silence on the wire, and tunnels/NATs cut
        // a silent TLS connection at ~50 s. PING frames while the request is in flight are what keeps
        // a long answer alive — and they need HTTP/2 to exist at all. The delay must stay well under
        // the ~50 s cut so at least one ping always lands before it.
        var handler = AnthropicProvider.BuildDefaultHandler();

        handler.KeepAlivePingPolicy.Should().Be(HttpKeepAlivePingPolicy.WithActiveRequests);
        handler.KeepAlivePingDelay.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromSeconds(30));
        handler.KeepAlivePingTimeout.Should().BeGreaterThan(TimeSpan.Zero);

        using var client = AnthropicProvider.BuildDefaultClient(new LlmConnectionFactory
        {
            Provider = "anthropic",
            ModelId = "claude-opus-4-8",
            ApiKey = "sk-test",
            RequestTimeoutMs = 90_000,
        });

        client.DefaultRequestVersion.Should().Be(System.Net.HttpVersion.Version20);
        client.DefaultVersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower, "HTTP/1.1 must stay the fallback");
        client.Timeout.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task Request_CarriesTheClientsHttpVersion()
    {
        // HttpClient.DefaultRequestVersion never touches a hand-built HttpRequestMessage — it
        // starts at HTTP/1.1 regardless. The provider builds its own messages, so it must copy
        // the client's defaults onto them, or the HTTP/2 keep-alive pings silently never happen.
        var handler = new CapturingHandler();
        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        var provider = new AnthropicProvider(new LlmConnectionFactory
        {
            Provider = "anthropic", ModelId = "claude-opus-4-8", ApiKey = "test-key",
        }, client);

        await provider.CompleteAsync(new LlmRequest { Messages = [LlmMessage.User("hi")] });

        handler.LastVersion.Should().Be(HttpVersion.Version20);
        handler.LastVersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
    }

    [Fact]
    public async Task EmptyMessages_AreSkipped_NotSentAsEmptyTextBlocks()
    {
        // One assistant reply with no text (the model spent max_tokens on thinking) used to be
        // rendered as {"type":"text","text":""}, which the API rejects with 400 — and since the
        // reply was persisted, EVERY later call of that conversation failed. Empty and
        // whitespace-only messages must simply not be sent; consecutive same-role turns are fine.
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(new LlmConnectionFactory
        {
            Provider = "anthropic", ModelId = "claude-opus-4-8", ApiKey = "test-key",
        }, new HttpClient(handler));

        await provider.CompleteAsync(new LlmRequest
        {
            Messages =
            [
                LlmMessage.User("первый вопрос"),
                new LlmMessage { Role = "assistant", Content = [new LlmTextBlock(string.Empty)] },
                LlmMessage.User("второй вопрос"),
                new LlmMessage { Role = "assistant", Content = [new LlmTextBlock("   ")] },
                new LlmMessage { Role = "assistant", Content = [] },
                LlmMessage.User("третий вопрос"),
            ],
        });

        var messages = handler.LastBody!["messages"]!.AsArray();
        messages.Count.Should().Be(3, "the three empty assistant replies are not sent");
        messages.Should().OnlyContain(m => m!["role"]!.GetValue<string>() == "user");

        foreach (var message in messages)
            foreach (var block in message!["content"]!.AsArray())
                if (block!["type"]!.GetValue<string>() == "text")
                    block["text"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Effort_Unset_SendsNoOutputConfig(string? effort)
    {
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(new LlmConnectionFactory
        {
            Provider = "anthropic", ModelId = "claude-sonnet-5", ApiKey = "test-key", Effort = effort,
        }, new HttpClient(handler));

        await provider.CompleteAsync(new LlmRequest { Messages = [LlmMessage.User("hi")] });

        handler.LastBody!.ContainsKey("output_config").Should().BeFalse("the provider's default effort must stay the provider's");
    }

    [Fact]
    public async Task Effort_Set_GoesInsideOutputConfig()
    {
        // Thinking tokens count against max_tokens; at the default (high) effort Sonnet 5 spent
        // all 4096 on thinking and answered with nothing. The knob lives in output_config, not
        // top-level, and is sent only for the factory that asked for it.
        var handler = new CapturingHandler();
        var provider = new AnthropicProvider(new LlmConnectionFactory
        {
            Provider = "anthropic", ModelId = "claude-sonnet-5", ApiKey = "test-key", Effort = " medium ",
        }, new HttpClient(handler));

        await provider.CompleteAsync(new LlmRequest { Messages = [LlmMessage.User("hi")] });

        handler.LastBody!["output_config"]!["effort"]!.GetValue<string>().Should().Be("medium");
        handler.LastBody!.ContainsKey("effort").Should().BeFalse("effort is not a top-level field");
    }
}
