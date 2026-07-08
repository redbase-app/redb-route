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

    /// <summary>
    /// Optional name of the <see cref="redb.Core.IRedbService"/> the LLM stores
    /// (conversation, approval, batch, idempotency, knowledge, ...) should target
    /// for this endpoint. The producer/consumer publishes the value into
    /// <c>exchange.Properties[LlmKeys.RedbName]</c>; storage implementations resolve
    /// the redb instance via <c>context.GetRedbService(name, exchange)</c>, which
    /// already takes care of per-exchange scoping and disposal.
    /// <para>
    /// When null or empty the default unnamed <c>IRedbService</c> registered in the
    /// route context is used — typically the host-wide instance from
    /// <c>services.AddRedb()</c>.
    /// </para>
    /// URI form: <c>llm://factory?redb=my-llm-db</c>.
    /// </summary>
    public string? Redb { get; set; }

    /// <summary>
    /// Principal identifier expression for audit. Either a literal value
    /// (<c>"system"</c>) or a header reference (<c>"${header.X-User-Id}"</c>);
    /// <see cref="LlmProducer"/> resolves it pre-call against the inbound
    /// exchange and stamps the value on every persisted row of the run as
    /// <c>MessageProps.UserId</c>. Falls back to the <c>llm.user.id</c>
    /// header when this is null.
    /// </summary>
    public string? User { get; set; }

    /// <summary>
    /// Comma-separated audit tags expressed as <c>key=value</c> pairs (each
    /// <c>value</c> may be a literal or a <c>${header.X}</c> expression).
    /// Example: <c>tier=${header.X-Tier},bucket=A</c>. Builders normally
    /// populate this through repeated <c>.Audit(k, v)</c> calls — values are
    /// URL-encoded before joining so commas / equals signs in literal values
    /// are safe. Header-driven tags (<c>llm.audit.&lt;name&gt;</c>) merge on
    /// top of these; headers win on collision.
    /// </summary>
    public string? Audit { get; set; }

    /// <summary>
    /// Prompt-template name persisted alongside every row of the run as
    /// <c>MessageProps.PromptTemplateName</c>. Pairs with
    /// <see cref="PromptTemplateVersion"/> so auditors can lock down which
    /// exact prompt drove an answer. Independent of <see cref="SystemPromptRef"/>:
    /// you can carry name/version even when the prompt body comes from elsewhere.
    /// </summary>
    public string? PromptTemplateName { get; set; }

    /// <summary>Prompt-template version paired with <see cref="PromptTemplateName"/>.</summary>
    public string? PromptTemplateVersion { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (MaxIterations < 1)
            throw new ArgumentException("MaxIterations must be >= 1.", nameof(MaxIterations));

        if (Temperature is < 0 or > 2)
            throw new ArgumentException("Temperature must be between 0 and 2.", nameof(Temperature));
    }
}
