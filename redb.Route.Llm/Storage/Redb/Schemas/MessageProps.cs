using redb.Core.Attributes;

namespace redb.Route.Llm.Storage.Redb.Schemas;

/// <summary>
/// A single message node within a conversation tree. Persisted as a
/// <c>TreeRedbObject&lt;MessageProps&gt;</c> with the conversation root (or
/// another <see cref="MessageProps"/>) as parent — branching is native: any
/// regenerate / parallel-tool / what-if path attaches as another child of the
/// same parent.
/// <para>
/// Identifier lives in <c>value_string</c> (per-message GUID) and the
/// conversation FK lives in <c>value_long</c> (pointing at the root's
/// <c>_objects.id</c>) — both on the indexed <c>_objects</c> row, so transcript
/// queries never scan <c>_values</c>. Parent is tracked natively via the tree's
/// <c>parent_id</c>; no duplicate fields here.
/// </para>
/// Content blocks (text / tool-use / tool-result) are stored as a typed
/// nested array (<see cref="Content"/>) — REDB persists them natively, no
/// JSON-string marshalling.
/// </summary>
[RedbScheme("LLM Conversation Message")]
public class MessageProps
{
    /// <summary>Role: "user" / "assistant" / "system" / "tool".</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Agent iteration that produced the message (0 = user prompt).</summary>
    public int Iteration { get; set; }

    /// <summary>When the message was appended.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Provider id ("openai" / "anthropic" / ...); null for non-provider messages.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Model id as resolved at call time.</summary>
    public string? ModelId { get; set; }

    /// <summary>Stop reason string for assistant messages; null otherwise.</summary>
    public string? StopReason { get; set; }

    /// <summary>Tool-use id when this message is a tool result; null otherwise.</summary>
    public string? ToolUseId { get; set; }

    /// <summary>Input tokens attributed to this turn (0 for non-assistant messages).</summary>
    public int InputTokens { get; set; }

    /// <summary>Output tokens attributed to this turn.</summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// Effective sampling temperature passed to the provider for the call that
    /// produced this assistant message; null on user / tool-result rows.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>Effective max-output-tokens cap; null on non-assistant rows.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Effective top-p value; null on non-assistant rows.</summary>
    public double? TopP { get; set; }

    /// <summary>
    /// Prompt-template name resolved at call time. Pairs with
    /// <see cref="PromptTemplateVersion"/> and a row in <c>PromptTemplateProps</c>
    /// to lock down "which exact prompt drove this answer".
    /// </summary>
    public string? PromptTemplateName { get; set; }

    /// <summary>Prompt-template version (paired with <see cref="PromptTemplateName"/>).</summary>
    public string? PromptTemplateVersion { get; set; }

    /// <summary>
    /// Stable hash of the tool-capabilities set exposed to the model on this
    /// call (canonical JSON of {name, description, schema} sorted by name,
    /// SHA-256, hex). Lets auditors detect "the same prompt saw a different
    /// tool surface yesterday".
    /// </summary>
    public string? ToolSetHash { get; set; }

    /// <summary>
    /// Opaque provider-side fingerprint identifying the exact backend
    /// configuration that served the call (OpenAI returns this as
    /// <c>system_fingerprint</c>; other OpenAI-compatible providers may or may
    /// not). When the value changes between two otherwise identical requests,
    /// the provider re-released the model under the same id.
    /// </summary>
    public string? ProviderSystemFingerprint { get; set; }

    /// <summary>
    /// Stable identifier of the principal that initiated the call (end-user,
    /// service account, on-behalf-of subject — the host decides). Captured
    /// pre-call from the fluent builder (<c>.User(...)</c>) or the
    /// <c>llm.user.id</c> header; the same value is stamped on every row of
    /// the same agent turn (system / user / tool / assistant).
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Connection-factory alias used for this call (e.g. <c>"haiku"</c>,
    /// <c>"gpt-mini"</c>) — the operator-chosen profile name from
    /// <c>LlmConnectionFactory.Name</c>. Audits the *intent* (which configured
    /// connection was selected) on top of <see cref="ProviderId"/> /
    /// <see cref="ModelId"/> (which model actually answered). Differs from the
    /// (provider, model) pair when the host registers multiple connections to
    /// the same provider with different keys / quotas / regions.
    /// </summary>
    public string? FactoryName { get; set; }

    /// <summary>
    /// Effective base URL the request was sent to, as configured on
    /// <c>LlmConnectionFactory.BaseUrl</c>. Null when the factory used the
    /// provider's built-in default endpoint. Captures self-hosted gateways
    /// (vLLM, llama.cpp, in-house proxies) that auditors otherwise have to
    /// chase through environment configuration.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Provider-issued response identifier surfaced by the backend (top-level
    /// <c>id</c> on OpenAI / Anthropic / xAI / Together responses). Null on
    /// non-assistant rows or when the provider does not surface one. Lets an
    /// auditor cross-reference a persisted row with the provider's own usage
    /// / billing logs.
    /// </summary>
    public string? ProviderResponseId { get; set; }

    /// <summary>
    /// Wall-clock time (milliseconds) the provider spent producing this
    /// assistant turn — measured around <c>ILlmProvider.CompleteAsync</c>.
    /// Null on user / tool-result / system rows.
    /// </summary>
    public long? LatencyMs { get; set; }

    /// <summary>
    /// Stable, non-secret fingerprint of the API key used for this call —
    /// SHA-256 of the key, first 16 hex chars. Lets a host with multiple keys
    /// per provider (per-tenant, per-environment, rotation pairs) tell which
    /// key served a given row without storing the key itself. Null when the
    /// factory exposes no <c>ApiKey</c> (e.g. on-host providers, secret-ref
    /// resolution skipped).
    /// </summary>
    public string? ApiKeyFingerprint { get; set; }

    /// <summary>
    /// How many times the route framework retried the inbound exchange before
    /// the call succeeded — the operator-visible "this row is from a retry"
    /// signal. Read from <c>exchange.Properties["RetryAttempt"]</c>
    /// (set by <c>RetryProcessor</c>) with fallbacks to
    /// <c>exchange.In.Headers["CamelRedeliveryCounter"]</c>
    /// (<c>OnExceptionProcessor</c>) and
    /// <c>exchange.In.Headers["CamelDeadLetterRedeliveryCount"]</c>
    /// (<c>DeadLetterProcessor</c>). Null when no retry counter is on the
    /// exchange (first / only delivery). Same value stamped on every row of
    /// the same agent turn.
    /// </summary>
    public int? RetryCount { get; set; }

    /// <summary>
    /// Content blocks for this message, stored as a typed nested array. Each
    /// block carries a discriminator (<see cref="MessageContentBlock.Kind"/>)
    /// plus the fields used by the matching <c>LlmContentBlock</c> variant.
    /// Provider-emitted JSON (<see cref="MessageContentBlock.InputJson"/> /
    /// <see cref="MessageContentBlock.OutputJson"/>) stays a raw string —
    /// those payloads are intentionally schema-less.
    /// </summary>
    public MessageContentBlock[] Content { get; set; } = [];

    /// <summary>
    /// Free-form audit tags for this row. Stored as a native REDB
    /// <see cref="Dictionary{TKey,TValue}"/> so LINQ-to-SQL queries like
    /// <c>x.AuditTags["ab-bucket"] == "A"</c> or
    /// <c>x.AuditTags.ContainsKey("tier")</c> run server-side via
    /// <c>redb.Query&lt;MessageProps&gt;()</c>. Captured pre-call (fluent
    /// <c>.Audit(k,v)</c> or <c>llm.audit.&lt;name&gt;</c> headers) and stamped
    /// identically on every row of the same turn.
    /// </summary>
    public Dictionary<string, string>? AuditTags { get; set; }
}

/// <summary>
/// Typed nested representation of an <c>LlmContentBlock</c>. Discriminated
/// by <see cref="Kind"/>; only the fields used by the active variant are set.
/// </summary>
public class MessageContentBlock
{
    /// <summary>Block kind: "text" / "tool_use" / "tool_result".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Set when <see cref="Kind"/> == "text".</summary>
    public string? Text { get; set; }

    /// <summary>Set when <see cref="Kind"/> == "tool_use" or "tool_result".</summary>
    public string? ToolUseId { get; set; }

    /// <summary>Tool name; set when <see cref="Kind"/> == "tool_use".</summary>
    public string? ToolName { get; set; }

    /// <summary>Raw tool input JSON; set when <see cref="Kind"/> == "tool_use".</summary>
    public string? InputJson { get; set; }

    /// <summary>Raw tool output JSON; set when <see cref="Kind"/> == "tool_result".</summary>
    public string? OutputJson { get; set; }

    /// <summary>Error flag for tool results; set when <see cref="Kind"/> == "tool_result".</summary>
    public bool IsError { get; set; }
}
