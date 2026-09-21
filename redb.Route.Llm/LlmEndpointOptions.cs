using System.Reflection;
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
    /// Streaming mode (<see cref="LlmStreamMode"/>): <c>stream=calls</c> or <c>stream=body</c>; absent means no
    /// streaming. Any other value, <c>stream=true</c> included, is refused when the endpoint is created.
    /// </summary>
    public LlmStreamMode Stream { get; set; }

    /// <summary>
    /// <c>cacheSystemPrompt=true</c> — ask the provider to cache the system prompt so repeated
    /// turns re-read it instead of re-paying for it.
    ///
    /// <para>Set it only when the system prompt is the same bytes every time. Caching matches on
    /// the rendered prefix, so a prompt carrying a timestamp or a per-user name is written to the
    /// cache and never read back: cost, no benefit. Verify with
    /// <c>LlmUsage.CacheReadInputTokens</c> — zero across repeated calls means the prefix moves.</para>
    /// </summary>
    public bool CacheSystemPrompt { get; set; }

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
    /// Per-run input-token budget (summed across iterations). Null = no ceiling; <c>0</c> also means no
    /// ceiling (the enforcer only enforces positive limits). Distinct from <see cref="MaxTokens"/>, which
    /// caps what the provider may generate in a single call.
    /// URI form: <c>llm://factory?budgetInputTokens=20000</c>.
    /// </summary>
    public int? BudgetInputTokens { get; set; }

    /// <summary>
    /// Per-run output-token budget (summed across iterations). Null or <c>0</c> = no ceiling.
    /// URI form: <c>llm://factory?budgetOutputTokens=4000</c>.
    /// </summary>
    public int? BudgetOutputTokens { get; set; }

    /// <summary>
    /// Per-run cost ceiling in USD. Null or <c>0</c> = no ceiling. A positive value requires an
    /// <see cref="Engine.Governance.ICostCalculator"/> that can price the model — the run fails fast
    /// before the first provider call when the registered calculator cannot.
    /// URI form: <c>llm://factory?budgetCostUsd=0.5</c>.
    /// </summary>
    public decimal? BudgetCostUsd { get; set; }

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

    /// <summary>
    /// Comma-separated extra header names propagated from the agent exchange to
    /// every tool call of the run. A trailing <c>*</c> makes an entry a prefix
    /// match. Example: <c>x-tenant-id,accept-language,x-app-*</c>.
    /// <para>
    /// Propagation is <b>default-deny</b>: without this option a tool route sees
    /// only the conversation / correlation ids, the resolved principal
    /// (<c>llm.user.id</c>) and the resolved audit tags (<c>llm.audit.*</c>) —
    /// never the inbound transport's header set. Add names here only for headers
    /// the route itself controls.
    /// </para>
    /// URI form: <c>llm://factory?propagateToolHeaders=x-tenant-id,x-app-*</c>.
    /// </summary>
    public string? PropagateToolHeaders { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (MaxIterations < 1)
            throw new ArgumentException("MaxIterations must be >= 1.", nameof(MaxIterations));

        if (Temperature is < 0 or > 2)
            throw new ArgumentException("Temperature must be between 0 and 2.", nameof(Temperature));

        // The core binder leaves a parameter it cannot place — a name no option has, or a value that does not convert
        // to the option's type — among the unmapped parameters, where nothing reads it: a typo would drop the option
        // without a word and the endpoint would run with its default ('stream=true', the old switch, would silently
        // not stream). Every such parameter is refused, each one named.
        if (UnmappedParameters.Count > 0)
            throw new ArgumentException(
                string.Join(" ", UnmappedParameters.Select(p => DescribeUnmapped(p.Key, p.Value))),
                UnmappedParameters.Keys.First());

        // Enum.Parse also takes a number, which may name no mode at all.
        if (!Enum.IsDefined(Stream))
            throw new ArgumentException(DescribeUnmapped("stream", ((int)Stream).ToString()), nameof(Stream));

        if (BudgetInputTokens is < 0)
            throw new ArgumentException("BudgetInputTokens must be >= 0.", nameof(BudgetInputTokens));

        if (BudgetOutputTokens is < 0)
            throw new ArgumentException("BudgetOutputTokens must be >= 0.", nameof(BudgetOutputTokens));

        if (BudgetCostUsd is < 0m)
            throw new ArgumentException("BudgetCostUsd must be >= 0.", nameof(BudgetCostUsd));
    }

    /// <summary>The options this endpoint reads, by the name the URI gives them.</summary>
    private static readonly PropertyInfo[] Options = typeof(LlmEndpointOptions)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite)
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

    private static string UriName(PropertyInfo option) => char.ToLowerInvariant(option.Name[0]) + option.Name[1..];

    /// <summary>Why a parameter the binder could not place is refused, and what would be accepted.</summary>
    private static string DescribeUnmapped(string name, string value)
    {
        if (name.Equals("stream", StringComparison.OrdinalIgnoreCase))
            return $"'stream={value}' is not a streaming mode. Use 'stream=calls': the agent engine streams every model "
                + "call inside the route, tools, the conversation and the route's transaction work as without streaming, "
                + "the pieces go to IAgentObserver.OnDeltaAsync and Out.Body is the final text. Or 'stream=body': the "
                + "agent run happens when the body is read, and its text streams into Out.Body while the model writes it.";

        var option = Array.Find(Options, o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (option is not null)
        {
            var type = Nullable.GetUnderlyingType(option.PropertyType) ?? option.PropertyType;
            var expected = type.IsEnum
                ? "one of " + string.Join(", ", Enum.GetNames(type).Select(n => n.ToLowerInvariant()))
                : type.Name;
            return $"'{UriName(option)}={value}' is not a valid value: {UriName(option)} takes {expected}.";
        }

        var nearest = Options
            .Select(o => (Name: UriName(o), Distance: EditDistance(name.ToLowerInvariant(), o.Name.ToLowerInvariant())))
            .MinBy(o => o.Distance);
        return nearest.Distance <= 2
            ? $"'{name}' is not an llm: option; did you mean '{nearest.Name}'?"
            : $"'{name}' is not an llm: option. Options: {string.Join(", ", Options.Select(UriName))}.";
    }

    /// <summary>Levenshtein distance: how many single-character edits turn one name into the other.</summary>
    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
