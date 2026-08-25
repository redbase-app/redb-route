namespace redb.Route.Llm.Providers;

/// <summary>
/// Contract generation of an Anthropic model, as it affects how a Messages-API
/// request must be shaped. The Anthropic wire contract changed across generations:
/// current-generation models <b>reject</b> the sampling knobs
/// (<c>temperature</c>/<c>top_p</c>/<c>top_k</c>) with HTTP 400, whereas older
/// generations accept them. The tier drives request building so the provider never
/// sends a field the target model will reject.
/// </summary>
public enum AnthropicModelTier
{
    /// <summary>
    /// Older generation (Sonnet 4.5 / Haiku 4.5 and earlier, Claude 3.x).
    /// Accepts <c>temperature</c>/<c>top_p</c>/<c>top_k</c>.
    /// </summary>
    Legacy,

    /// <summary>
    /// Transitional generation (Opus 4.6 / Sonnet 4.6). Still accepts sampling knobs.
    /// </summary>
    Transitional,

    /// <summary>
    /// Current generation (Opus 4.7 / 4.8 / 5, Sonnet 5, Fable 5, and — by the
    /// fail-forward default — any unrecognised newer id). <b>Rejects</b> sampling
    /// knobs with HTTP 400.
    /// </summary>
    Modern
}

/// <summary>
/// How the target model accepts the sampling knobs (<c>temperature</c>/<c>top_p</c>).
/// The contract is not binary — it has three states across generations:
/// </summary>
public enum AnthropicSamplingPolicy
{
    /// <summary>Claude 3.x — <c>temperature</c> and <c>top_p</c> may both be sent.</summary>
    Both,

    /// <summary>
    /// Claude 4.0–4.6 (incl. Sonnet/Haiku 4.5 and Opus/Sonnet 4.6) — <b>at most one</b>
    /// of <c>temperature</c>/<c>top_p</c>; sending both returns HTTP 400.
    /// </summary>
    AtMostOne,

    /// <summary>
    /// Claude 4.7+ / 5 — non-default sampling is rejected with HTTP 400 (omitting the
    /// field, or passing its default, is accepted). The connector omits them entirely.
    /// </summary>
    None
}

/// <summary>
/// Capability descriptor for an Anthropic model: which request fields the target
/// model accepts. Resolved from the model id by <see cref="Resolve"/> using a static
/// generation table plus a <em>fail-forward</em> default — an unrecognised id is
/// treated as <see cref="AnthropicModelTier.Modern"/> so a future model release does
/// not get sent a now-removed field and 400.
/// <para>
/// This is Anthropic-scoped on purpose (the OpenAI-compatible providers accept
/// sampling and need no such gating). Generalise to an <c>IModelProfile</c> only if a
/// second provider ever needs per-model request shaping.
/// </para>
/// </summary>
public readonly record struct AnthropicModelProfile(AnthropicModelTier Tier, AnthropicSamplingPolicy Sampling)
{
    /// <summary>
    /// Whether any sampling knob may be sent at all. <see langword="false"/> only for
    /// <see cref="AnthropicSamplingPolicy.None"/> (current-generation models — sending
    /// <c>temperature</c>/<c>top_p</c> yields HTTP 400). Note that
    /// <see cref="AnthropicSamplingPolicy.AtMostOne"/> still allows <b>one</b> knob;
    /// consult <see cref="Sampling"/> directly to shape the request.
    /// </summary>
    public bool SamplingSupported => Sampling != AnthropicSamplingPolicy.None;

    /// <summary>
    /// Resolves the profile for <paramref name="modelId"/>. When
    /// <paramref name="tierOverride"/> is a recognised tier name
    /// (<c>legacy</c>/<c>transitional</c>/<c>modern</c>, case-insensitive) it wins —
    /// the escape-hatch for self-hosted ids, proxies, or snapshots the table does not
    /// know. Otherwise the id is classified by generation, defaulting to
    /// <see cref="AnthropicModelTier.Modern"/> when no version can be read.
    /// </summary>
    public static AnthropicModelProfile Resolve(string? modelId, string? tierOverride = null)
    {
        if (TryParseTier(tierOverride, out var forced))
            // An explicit tier override maps to the safe sampling policy for that tier:
            // any sampling-accepting model tolerates a single knob, so AtMostOne is the
            // safe superset for legacy/transitional; modern omits sampling.
            return new AnthropicModelProfile(
                forced,
                forced == AnthropicModelTier.Modern
                    ? AnthropicSamplingPolicy.None
                    : AnthropicSamplingPolicy.AtMostOne);

        TryReadVersion(modelId, out var major, out var minor);
        return new AnthropicModelProfile(
            ClassifyByGeneration(major, minor),
            ClassifySampling(major, minor));
    }

    /// <summary>Parses an explicit tier-name override; returns false when unset/unknown.</summary>
    public static bool TryParseTier(string? name, out AnthropicModelTier tier)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "legacy": tier = AnthropicModelTier.Legacy; return true;
            case "transitional": tier = AnthropicModelTier.Transitional; return true;
            case "modern": tier = AnthropicModelTier.Modern; return true;
            default: tier = AnthropicModelTier.Modern; return false;
        }
    }

    /// <summary>
    /// Classifies a (major, minor) version into a contract tier. Rules:
    /// <list type="bullet">
    ///   <item>major ≥ 5 → Modern (Opus/Sonnet/Fable 5);</item>
    ///   <item>major 4, minor ≥ 7 → Modern (Opus 4.7 / 4.8);</item>
    ///   <item>major 4, minor == 6 → Transitional (Opus/Sonnet 4.6);</item>
    ///   <item>major 4, minor ≤ 5 → Legacy (Sonnet 4.5 / Haiku 4.5);</item>
    ///   <item>major ≤ 3 → Legacy (Claude 3.x);</item>
    ///   <item>no version (major &lt; 0) → Modern (fail-forward for future ids).</item>
    /// </list>
    /// </summary>
    private static AnthropicModelTier ClassifyByGeneration(int major, int minor)
    {
        if (major < 0) return AnthropicModelTier.Modern; // unknown/future id — assume current contract
        if (major >= 5) return AnthropicModelTier.Modern;

        if (major == 4)
        {
            if (minor >= 7) return AnthropicModelTier.Modern;       // 4.7, 4.8
            if (minor == 6) return AnthropicModelTier.Transitional; // 4.6
            return AnthropicModelTier.Legacy;                       // 4.5 and lower, or none
        }

        return AnthropicModelTier.Legacy; // 3.x and older
    }

    /// <summary>
    /// Classifies a (major, minor) version into a sampling policy — the state that
    /// actually shapes the wire request:
    /// <list type="bullet">
    ///   <item>major ≤ 3 → <see cref="AnthropicSamplingPolicy.Both"/> (Claude 3.x accepts both knobs);</item>
    ///   <item>major 4, minor ≤ 6 → <see cref="AnthropicSamplingPolicy.AtMostOne"/> (Claude 4.0–4.6: both → 400);</item>
    ///   <item>major 4, minor ≥ 7 or major ≥ 5 → <see cref="AnthropicSamplingPolicy.None"/> (4.7+/5 reject non-default sampling);</item>
    ///   <item>no version (major &lt; 0) → <see cref="AnthropicSamplingPolicy.None"/> (fail-forward).</item>
    /// </list>
    /// </summary>
    private static AnthropicSamplingPolicy ClassifySampling(int major, int minor)
    {
        if (major < 0) return AnthropicSamplingPolicy.None;   // unknown/future id
        if (major <= 3) return AnthropicSamplingPolicy.Both;  // Claude 3.x
        if (major == 4 && minor <= 6) return AnthropicSamplingPolicy.AtMostOne; // 4.0–4.6
        return AnthropicSamplingPolicy.None;                  // 4.7+ / 5
    }

    /// <summary>
    /// Extracts (major, minor) from a model id. Normalises '.'→'-', lowercases, and
    /// treats the first two short numeric tokens (1–2 digits) as major/minor —
    /// skipping long tokens such as an 8-digit date snapshot. <paramref name="minor"/>
    /// is -1 when absent. Returns false when no version token is present.
    /// </summary>
    private static bool TryReadVersion(string? modelId, out int major, out int minor)
    {
        major = -1;
        minor = -1;
        if (string.IsNullOrWhiteSpace(modelId)) return false;

        var normalized = modelId.Replace('.', '-');
        foreach (var token in normalized.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            // Version components are 1–2 digits; longer all-digit tokens are dates (e.g. 20251001).
            if (token.Length is < 1 or > 2) continue;
            if (!int.TryParse(token, out var n)) continue;

            if (major < 0) { major = n; continue; }
            minor = n;
            break;
        }

        return major >= 0;
    }
}
