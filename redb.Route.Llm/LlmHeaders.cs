namespace redb.Route.Llm;

/// <summary>
/// Header keys used by the LLM connector. All keys are lowercase to match
/// existing transport conventions (Kafka, RabbitMQ, Http).
/// </summary>
public static class LlmHeaders
{
    /// <summary>Conversation identifier — links a message to a multi-turn dialog.</summary>
    public const string ConversationId = "llm.conversation.id";

    /// <summary>
    /// Marks the outbound message as a streaming response so transports
    /// (<c>HttpConsumer</c> in <c>redb.Route.Http</c>, WebSocket) switch to
    /// chunked / SSE / per-frame wire encoding instead of buffering the body.
    /// Set by <see cref="LlmProducer"/> for <c>stream=body</c>; <c>stream=calls</c> returns the final text and
    /// does not set it.
    /// </summary>
    public const string Streaming = "llm.streaming";

    /// <summary>System prompt override for a single call.</summary>
    public const string SystemPrompt = "llm.system";

    /// <summary>
    /// Fixed opening messages placed before the loaded conversation history on this call — the
    /// value is an <see cref="IEnumerable{T}"/> of <see cref="Providers.LlmMessage"/>, first one a
    /// <c>user</c> turn. Rides into <see cref="Engine.AgentRequest.Preamble"/>: sent on every
    /// iteration, never persisted, survives a branch rebuild. Mark the last message with
    /// <see cref="Providers.LlmMessage.CacheBreakpoint"/> to extend the cached prefix past the
    /// system prompt.
    /// </summary>
    public const string Preamble = "llm.preamble";

    /// <summary>Logical role of the message: "user", "assistant", "system", "tool".</summary>
    public const string Role = "llm.role";

    /// <summary>Model identifier resolved from the connection factory.</summary>
    public const string ModelId = "llm.model.id";

    /// <summary>Provider identifier resolved from the connection factory ("anthropic", "openai", ...).</summary>
    public const string ProviderId = "llm.provider.id";

    /// <summary>Token usage written by the producer after a call (input).</summary>
    public const string TokensIn = "llm.tokens.in";

    /// <summary>Token usage written by the producer after a call (output).</summary>
    public const string TokensOut = "llm.tokens.out";

    /// <summary>
    /// Prompt tokens WRITTEN to the provider's cache on this exchange, billed at a premium over
    /// normal input. Zero while <see cref="LlmEndpointOptions.CacheSystemPrompt"/> is on usually
    /// means the prefix is below the provider's length floor.
    /// </summary>
    public const string CacheWriteTokens = "llm.tokens.cache.write";

    /// <summary>
    /// Prompt tokens SERVED FROM the provider's cache on this exchange, billed at a fraction of
    /// normal input.
    ///
    /// <para><b>This is the header that says caching works.</b> Zero across repeated calls with an
    /// unchanged prefix means something in that prefix is not byte-stable — and nothing else
    /// reports it: <see cref="TokensIn"/> is the uncached remainder, so a broken cache looks
    /// exactly like an expensive prompt.</para>
    /// </summary>
    public const string CacheReadTokens = "llm.tokens.cache.read";

    /// <summary>Estimated cost in USD written by the producer (optional).</summary>
    public const string CostUsd = "llm.cost.usd";

    // ── Transcription (stt://) ────────────────────────────────────────────────

    /// <summary>
    /// ISO-639-1 language hint for <c>stt://</c>, overriding the endpoint option. Written
    /// back after the call when the provider reports which language it heard.
    /// </summary>
    public const string TranscriptionLanguage = "llm.transcription.language";

    /// <summary>Per-exchange decoding hint for <c>stt://</c>, overriding the endpoint option.</summary>
    public const string TranscriptionPrompt = "llm.transcription.prompt";

    /// <summary>
    /// Name to upload the recording under, overriding the endpoint option. Decides which
    /// demuxer the speech endpoint picks, so it is the difference between a decoded voice
    /// note and a rejected upload.
    /// </summary>
    public const string TranscriptionFileName = "llm.transcription.fileName";

    /// <summary>Speech model that produced the transcription.</summary>
    public const string TranscriptionModel = "llm.transcription.model";

    /// <summary>Provider that produced the transcription.</summary>
    public const string TranscriptionProvider = "llm.transcription.provider";

    /// <summary>
    /// Number of characters recognised (<c>int</c>). Deliberately the length and not the
    /// text: a header travels into logs and dead letters, and a transcription is the
    /// speaker's own words. Zero says the recording held no speech.
    /// </summary>
    public const string TranscriptionChars = "llm.transcription.chars";

    /// <summary>
    /// Length of the recording in seconds (<c>double</c>), when the provider reports it —
    /// the unit speech APIs bill in. Absent on providers that answer with the plain
    /// <c>{"text": ...}</c> shape, which is most local servers; a caller that must have the
    /// number should take it from the transport that delivered the audio.
    /// </summary>
    public const string TranscriptionDuration = "llm.transcription.duration";

    /// <summary>Stop reason returned by the provider: "end_turn", "tool_use", "max_tokens", "stop_sequence".</summary>
    public const string StopReason = "llm.stop_reason";

    /// <summary>Number of tool-loop iterations consumed by the agent for this exchange.</summary>
    public const string ToolIterations = "llm.tool.iterations";

    /// <summary>Name of the tool currently being executed (set on the child exchange forwarded by the bridge).</summary>
    public const string ToolName = "llm.tool.name";

    /// <summary>Target endpoint URI invoked by a <see cref="Tools.RouteToolBridge"/>.</summary>
    public const string ToolBridgeEndpoint = "llm.tool.bridge.endpoint";

    /// <summary>Tool-use identifier from the model — propagated to audit/idempotency stores.</summary>
    public const string ToolUseId = "llm.tool.use_id";

    /// <summary>Approval identifier when a tool call awaits or has received approval.</summary>
    public const string ApprovalId = "llm.approval.id";

    /// <summary>Async-batch identifier set by <c>LlmCallbackProcessor</c> when a webhook arrives.</summary>
    public const string BatchId = "llm.batch.id";

    /// <summary>Async-batch lifecycle status ("completed" / "failed" / "cancelled").</summary>
    public const string BatchStatus = "llm.batch.status";

    /// <summary>True when the callback was a duplicate (already-processed batch); routes can short-circuit on this.</summary>
    public const string BatchDuplicate = "llm.batch.duplicate";

    /// <summary>
    /// Conversation message id assigned by <see cref="redb.Route.Llm.Engine.Storage.IConversationStore.AppendAsync"/>
    /// when <see cref="redb.Route.Llm.LlmCallbackProcessor"/> appends an assistant turn from a completed batch callback
    /// (set only when the originating <see cref="redb.Route.Llm.Engine.Storage.BatchJobRecord.AppendToConversation"/> is true).
    /// </summary>
    public const string ConversationMessageId = "llm.conversation.message.id";

    /// <summary>
    /// Stable identifier of the principal that initiated this call. Read by
    /// <see cref="LlmProducer"/> when the fluent builder declares
    /// <c>.User("${header.X-User-Id}")</c> (or any literal expression) and
    /// stamped on every persisted row of the run as
    /// <c>MessageProps.UserId</c> / <c>ConversationMessageMeta.UserId</c>.
    /// </summary>
    public const string UserId = "llm.user.id";

    /// <summary>
    /// Header-name prefix for free-form audit tags. Any inbound exchange
    /// header named <c>llm.audit.&lt;tag&gt;</c> is captured by
    /// <see cref="LlmProducer"/> and stamped on every persisted row of the
    /// run as <c>MessageProps.AuditTags</c> entry <c>{ Key=&lt;tag&gt;, Value=&lt;header value&gt; }</c>.
    /// Tags also accumulate from the fluent <c>.Audit(k, v)</c> builder; both
    /// sources merge (header overrides builder).
    /// </summary>
    public const string AuditTagPrefix = "llm.audit.";
}
