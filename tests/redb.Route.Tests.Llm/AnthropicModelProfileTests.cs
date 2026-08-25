namespace redb.Route.Tests.Llm;

/// <summary>
/// Classification of Anthropic model ids into contract tiers. The tier decides whether
/// the sampling knobs (temperature/top_p) may be sent — current-generation models reject
/// them with HTTP 400, so an unrecognised id must fail forward to <c>Modern</c>.
/// </summary>
public sealed class AnthropicModelProfileTests
{
    [Theory]
    // Current generation — sampling removed (HTTP 400 if sent).
    [InlineData("claude-opus-5", AnthropicModelTier.Modern)]
    [InlineData("claude-sonnet-5", AnthropicModelTier.Modern)]
    [InlineData("claude-fable-5", AnthropicModelTier.Modern)]
    [InlineData("claude-opus-4-8", AnthropicModelTier.Modern)]
    [InlineData("claude-opus-4-7", AnthropicModelTier.Modern)]
    [InlineData("claude-opus-4-8-20251001", AnthropicModelTier.Modern)] // date snapshot suffix
    // Transitional — sampling still accepted.
    [InlineData("claude-opus-4-6", AnthropicModelTier.Transitional)]
    [InlineData("claude-sonnet-4-6", AnthropicModelTier.Transitional)]
    // Legacy — sampling accepted.
    [InlineData("claude-sonnet-4-5", AnthropicModelTier.Legacy)]
    [InlineData("claude-haiku-4-5", AnthropicModelTier.Legacy)]
    [InlineData("claude-haiku-4.5", AnthropicModelTier.Legacy)]          // dot separator
    [InlineData("claude-3-7-sonnet-20250219", AnthropicModelTier.Legacy)]
    [InlineData("claude-3-5-sonnet-20241022", AnthropicModelTier.Legacy)]
    // Unknown / future id — fail forward to Modern (never send a now-removed field).
    [InlineData("claude-something-new", AnthropicModelTier.Modern)]
    [InlineData("", AnthropicModelTier.Modern)]
    public void Resolve_ClassifiesByGeneration(string modelId, AnthropicModelTier expected)
    {
        AnthropicModelProfile.Resolve(modelId).Tier.Should().Be(expected);
    }

    [Theory]
    // The sampling contract is three-state, not binary — 4.0–4.6 accept ONE knob.
    [InlineData("claude-opus-5", AnthropicSamplingPolicy.None)]
    [InlineData("claude-sonnet-5", AnthropicSamplingPolicy.None)]
    [InlineData("claude-opus-4-8", AnthropicSamplingPolicy.None)]
    [InlineData("claude-opus-4-7", AnthropicSamplingPolicy.None)]
    [InlineData("claude-opus-4-6", AnthropicSamplingPolicy.AtMostOne)]
    [InlineData("claude-sonnet-4-6", AnthropicSamplingPolicy.AtMostOne)]
    [InlineData("claude-sonnet-4-5", AnthropicSamplingPolicy.AtMostOne)]
    [InlineData("claude-haiku-4-5", AnthropicSamplingPolicy.AtMostOne)]
    [InlineData("claude-3-5-sonnet-20241022", AnthropicSamplingPolicy.Both)]
    [InlineData("claude-3-opus-20240229", AnthropicSamplingPolicy.Both)]
    [InlineData("claude-something-new", AnthropicSamplingPolicy.None)]
    public void Resolve_ClassifiesSamplingPolicy(string modelId, AnthropicSamplingPolicy expected)
    {
        AnthropicModelProfile.Resolve(modelId).Sampling.Should().Be(expected);
    }

    [Theory]
    [InlineData(AnthropicSamplingPolicy.None, false)]
    [InlineData(AnthropicSamplingPolicy.AtMostOne, true)]
    [InlineData(AnthropicSamplingPolicy.Both, true)]
    public void SamplingSupported_MatchesPolicy(AnthropicSamplingPolicy policy, bool expected)
    {
        new AnthropicModelProfile(AnthropicModelTier.Legacy, policy).SamplingSupported.Should().Be(expected);
    }

    [Theory]
    [InlineData("legacy", AnthropicModelTier.Legacy)]
    [InlineData("Transitional", AnthropicModelTier.Transitional)]
    [InlineData("MODERN", AnthropicModelTier.Modern)]
    public void Resolve_TierOverride_WinsOverModelId(string tierOverride, AnthropicModelTier expected)
    {
        // A modern id that would classify as Modern, forced to another tier by override.
        AnthropicModelProfile.Resolve("claude-opus-5", tierOverride).Tier.Should().Be(expected);
    }

    [Fact]
    public void Resolve_UnknownOverride_FallsBackToModelClassification()
    {
        AnthropicModelProfile.Resolve("claude-haiku-4-5", "nonsense").Tier
            .Should().Be(AnthropicModelTier.Legacy);
    }
}
