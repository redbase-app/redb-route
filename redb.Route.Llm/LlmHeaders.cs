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
    /// (<see cref="redb.Route.Http.HttpConsumer"/>, WebSocket) switch to
    /// chunked / SSE / per-frame wire encoding instead of buffering the body.
    /// Set by <see cref="LlmProducer"/> when <c>?stream=true</c>.
    /// </summary>
    public const string Streaming = "llm.streaming";

    /// <summary>System prompt override for a single call.</summary>
    public const string SystemPrompt = "llm.system";

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

    /// <summary>Estimated cost in USD written by the producer (optional).</summary>
    public const string CostUsd = "llm.cost.usd";

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
}
