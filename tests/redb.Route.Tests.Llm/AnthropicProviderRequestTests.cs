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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastBody = JsonNode.Parse(raw) as JsonObject;

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
}
