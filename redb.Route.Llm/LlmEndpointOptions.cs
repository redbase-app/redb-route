using redb.Route.Core;

namespace redb.Route.Llm;

/// <summary>
/// Typed options for <see cref="LlmEndpoint"/>. Bound from URI query parameters via reflection.
/// <para>
/// The set of accepted parameters is intentionally narrow — model, provider, version
/// and credentials must come from <see cref="LlmConnectionFactory"/>, not the URI.
/// </para>
/// </summary>
public sealed class LlmEndpointOptions : EndpointOptions
{
    /// <summary>Name of the <see cref="LlmConnectionFactory"/> registered in the route registry.</summary>
    /// <remarks>
    /// May also come from the URI host (<c>llm://myFactory</c>); the host wins when both are set.
    /// </remarks>
    public string? ConnectionFactory { get; set; }

    /// <summary>Optional per-call temperature override.</summary>
    public double? Temperature { get; set; }

    /// <summary>Optional per-call max-tokens override.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Optional per-call top-p override.</summary>
    public double? TopP { get; set; }

    /// <summary>
    /// Reference to a system-prompt template stored in the prompt registry
    /// (or a literal prompt — applied when no registry entry matches).
    /// </summary>
    public string? SystemPromptRef { get; set; }

    /// <summary>
    /// Conversation identifier strategy. Recognised values:
    /// <c>"none"</c> — no conversation tracking (default);
    /// <c>"header"</c> — read from <c>llm.conversation.id</c> header;
    /// <c>"property"</c> — read from <c>exchange.RouteId</c> as conversation key.
    /// </summary>
    public string Conversation { get; set; } = "none";

    /// <summary>
    /// Streaming behaviour: when <c>true</c>, the producer opens a streaming response
    /// and writes tokens to <c>exchange.Out.Body</c> as an <see cref="IAsyncEnumerable{T}"/> of strings.
    /// </summary>
    public bool Stream { get; set; }

    /// <summary>
    /// Schedule expression for consumer mode (cron or fixed interval). When set on
    /// a <c>From("llm://...")</c> route, the consumer wakes up on this schedule and
    /// invokes the agent with an empty user message (or with body resolved from
    /// <see cref="InitialBodyRef"/>).
    /// </summary>
    public string? Schedule { get; set; }

    /// <summary>Optional reference to a body template used by scheduled consumers.</summary>
    public string? InitialBodyRef { get; set; }

    /// <summary>Maximum tool-loop iterations the agent engine may consume for one call.</summary>
    public int MaxIterations { get; set; } = 8;

    /// <summary>
    /// Tool exposure filter. Recognised values:
    /// <c>null</c>/empty — no tools exposed (default; explicit opt-in required);
    /// <c>"*"</c> — every descriptor in the registry;
    /// CSV of names (e.g. <c>"get_order,refund"</c>) — only those names from the registry.
    /// </summary>
    public string? Tools { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (MaxIterations < 1)
            throw new ArgumentException("MaxIterations must be >= 1.", nameof(MaxIterations));

        if (Temperature is < 0 or > 2)
            throw new ArgumentException("Temperature must be between 0 and 2.", nameof(Temperature));
    }
}
