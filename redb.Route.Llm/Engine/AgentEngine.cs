using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Expressions;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Telemetry;

namespace redb.Route.Llm.Engine;

/// <summary>
/// Default <see cref="IAgentEngine"/> implementation. Drives the tool-loop with
/// governance, idempotency, approval, conversation persistence and audit
/// observability — every dependency is optional and defaults to a Noop
/// implementation so that simple cases (a tool-less, persistence-less call
/// against <see cref="StubProvider"/>) work out of the box.
/// </summary>
public sealed class AgentEngine : IAgentEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // UTF-8, not \uXXXX — keep non-ASCII readable and token-cheap on the wire.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger<AgentEngine>? _logger;
    private readonly IProducerTemplate? _producerTemplate;
    private readonly IAgentObserver _observer;
    private readonly IBudgetEnforcer _budget;
    private readonly IApprovalGate _approval;
    private readonly IRedactionFilter _redaction;
    private readonly IShadowRunner _shadow;
    private readonly IConversationStore? _conversation;
    private readonly IToolIdempotencyStore? _idempotency;
    private readonly IApprovalStore? _approvalStore;

    /// <summary>
    /// Backwards-compatible constructor — wires every dependency to its no-op
    /// implementation. Use the DI-driven constructor for production wiring.
    /// </summary>
    public AgentEngine(ILogger<AgentEngine>? logger = null)
        : this(logger, producerTemplate: null, observer: null, budget: null, approval: null, redaction: null, shadow: null, conversation: null, idempotency: null, approvalStore: null)
    {
    }

    /// <summary>Creates an engine with explicit governance / storage dependencies.</summary>
    public AgentEngine(
        ILogger<AgentEngine>? logger,
        IProducerTemplate? producerTemplate,
        IAgentObserver? observer,
        IBudgetEnforcer? budget,
        IApprovalGate? approval,
        IRedactionFilter? redaction,
        IShadowRunner? shadow,
        IConversationStore? conversation,
        IToolIdempotencyStore? idempotency,
        IApprovalStore? approvalStore)
    {
        _logger = logger;
        _producerTemplate = producerTemplate;
        _observer = observer ?? new NoopAgentObserver();
        _budget = budget ?? new NoopBudgetEnforcer();
        _approval = approval ?? new AutoApproveGate();
        _redaction = redaction ?? new NoopRedactionFilter();
        _shadow = shadow ?? new NoopShadowRunner();
        _conversation = conversation;
        _idempotency = idempotency;
        _approvalStore = approvalStore;
    }

    /// <inheritdoc />
    public async Task<AgentResponse> RunAsync(AgentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = request.Factory.Build();
        var capabilities = ProjectCapabilities(request.Tools);
        var toolSetHash = ComputeToolSetHash(capabilities);
        var effectiveTemperature = request.Temperature ?? request.Factory.Temperature;
        var effectiveMaxTokens = request.MaxTokens ?? request.Factory.MaxTokens;
        var effectiveTopP = request.Factory.TopP;

        var runCtx = new AgentRunContext
        {
            ConversationId = request.ConversationId,
            FactoryName = request.Factory.Name,
            ProviderId = request.Factory.Provider,
            ModelId = request.Factory.ModelId ?? string.Empty,
            ExchangeId = request.Exchange.ExchangeId
        };
        await _observer.OnRunStartedAsync(runCtx, ct).ConfigureAwait(false);

        var retryCount = ReadRetryCount(request.Exchange);

        var transcript = new List<LlmMessage>();
        string? attachUnderId = request.ConversationParentMessageId;

        if (_conversation is not null && request.ConversationId is { } convIdToLoad)
        {
            var path = await _conversation.LoadPathAsync(
                convIdToLoad, request.ConversationParentMessageId, request.Exchange, ct).ConfigureAwait(false);
            foreach (var node in path)
                transcript.Add(node.Message);
            if (path.Count > 0)
                attachUnderId = path[^1].Id;

            // Orphan tool_use recovery: if the loaded transcript ends with an
            // assistant message that has unmatched tool_use blocks (e.g. a prior
            // run was cancelled / timed out between persisting the assistant
            // turn and dispatching tools), synthesize error tool_result blocks
            // so the next provider call doesn't 400 on "tool_use without matching
            // tool_result". Without this, the conversation poisons itself
            // permanently for InMemory and any persistent store alike.
            if (transcript.Count > 0 && transcript[^1].Role == "assistant")
            {
                var orphanResults = new List<LlmContentBlock>();
                foreach (var block in transcript[^1].Content)
                {
                    if (block is LlmToolUseBlock orphan)
                        orphanResults.Add(new LlmToolResultBlock(
                            orphan.ToolUseId,
                            "{\"error\":\"orphaned_tool_use_recovered\"}",
                            IsError: true));
                }

                if (orphanResults.Count > 0)
                {
                    var recovery = new LlmMessage { Role = "user", Content = orphanResults };
                    transcript.Add(recovery);
                    attachUnderId = await PersistMessageAsync(
                        request, attachUnderId, recovery,
                        iteration: 0, stopReason: null, usage: LlmUsage.Empty,
                        toolUseId: null,
                        temperature: null, maxTokens: null, topP: null,
                        toolSetHash: null, providerSystemFingerprint: null,
                        providerResponseId: null, latencyMs: null,
                        retryCount: retryCount,
                        ct).ConfigureAwait(false);
                    _logger?.LogWarning(
                        "Recovered {Count} orphaned tool_use block(s) in conversation {Conv} on load.",
                        orphanResults.Count, convIdToLoad);
                }
            }
        }

        transcript.Add(new LlmMessage { Role = "user", Content = request.UserContent });

        PublishConversationContext(request, transcript, iterations: 0, totalUsage: AgentUsage.Zero);

        var parentMessageId = await PersistMessageAsync(
            request,
            parentId: attachUnderId,
            message: transcript[^1],
            iteration: 0,
            stopReason: null,
            usage: LlmUsage.Empty,
            toolUseId: null,
            temperature: null, maxTokens: null, topP: null,
            toolSetHash: null, providerSystemFingerprint: null,
            providerResponseId: null, latencyMs: null,
            retryCount: retryCount,
            ct).ConfigureAwait(false);

        var iter = 0;
        var totalUsage = AgentUsage.Zero;
        LlmResponse? last = null;
        Exception? terminalException = null;
        var cancelled = false;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (iter >= request.MaxIterations)
                {
                    _logger?.LogWarning("Agent reached MaxIterations={Max}; stopping.", request.MaxIterations);
                    break;
                }

                var pre = await _budget.PreCheckAsync(request.ConversationId, request.Budget, totalUsage, ct).ConfigureAwait(false);
                if (!pre.Continue)
                {
                    _logger?.LogInformation("Budget pre-check stopped run: {Reason}", pre.Reason);
                    break;
                }

                iter++;

                var llmRequest = new LlmRequest
                {
                    ModelId = request.Factory.ModelId,
                    SystemPrompt = request.SystemPrompt,
                    Messages = transcript,
                    Tools = capabilities,
                    Temperature = effectiveTemperature,
                    MaxTokens = effectiveMaxTokens,
                    TopP = effectiveTopP
                };

                var providerTag = new KeyValuePair<string, object?>("llm.provider", request.Factory.Provider);
                var modelTag = new KeyValuePair<string, object?>("llm.model.id", request.Factory.ModelId);
                var factoryTag = new KeyValuePair<string, object?>("llm.factory", request.Factory.Name);

                var sw = Stopwatch.StartNew();
                try
                {
                    last = await provider.CompleteAsync(llmRequest, ct).ConfigureAwait(false);
                    LlmMetrics.Calls.Add(1, providerTag, modelTag, factoryTag);
                }
                catch
                {
                    LlmMetrics.CallsFailed.Add(1, providerTag, modelTag, factoryTag);
                    throw;
                }
                finally
                {
                    LlmMetrics.CallDuration.Record(sw.Elapsed.TotalMilliseconds, providerTag, modelTag, factoryTag);
                }

                if (last.Usage.InputTokens > 0)
                    LlmMetrics.TokensIn.Add(last.Usage.InputTokens, providerTag, modelTag, factoryTag);
                if (last.Usage.OutputTokens > 0)
                    LlmMetrics.TokensOut.Add(last.Usage.OutputTokens, providerTag, modelTag, factoryTag);

                var iterUsage = new AgentUsage(last.Usage.InputTokens, last.Usage.OutputTokens, 0m);
                totalUsage = totalUsage.Add(iterUsage);

                await _observer.OnIterationCompletedAsync(new AgentIterationContext
                {
                    Run = runCtx,
                    Iteration = iter,
                    StopReason = last.StopReason,
                    IterationUsage = last.Usage,
                    Duration = sw.Elapsed
                }, ct).ConfigureAwait(false);

                if (_shadow.Enabled)
                    _ = SafeRunShadowAsync(provider, llmRequest, last, ct);

                transcript.Add(new LlmMessage { Role = "assistant", Content = last.Content });
                parentMessageId = await PersistMessageAsync(
                    request, parentMessageId, transcript[^1],
                    iteration: iter, stopReason: last.StopReason, usage: last.Usage,
                    toolUseId: null,
                    temperature: effectiveTemperature,
                    maxTokens: effectiveMaxTokens,
                    topP: effectiveTopP,
                    toolSetHash: toolSetHash,
                    providerSystemFingerprint: last.ProviderSystemFingerprint,
                    providerResponseId: last.ProviderResponseId,
                    latencyMs: (long)sw.Elapsed.TotalMilliseconds,
                    retryCount: retryCount,
                    ct).ConfigureAwait(false);

                PublishConversationContext(request, transcript, iter, totalUsage);

                var post = await _budget.RecordAndCheckAsync(
                    request.ConversationId, request.Budget, iterUsage, totalUsage, ct).ConfigureAwait(false);
                if (!post.Continue)
                {
                    _logger?.LogInformation("Budget post-check stopped run: {Reason}", post.Reason);
                    break;
                }

                if (last.StopReason != LlmStopReason.ToolUse) break;

                var toolResults = new List<LlmContentBlock>(last.Content.Count);
                foreach (var block in last.Content)
                {
                    if (block is not LlmToolUseBlock use) continue;

                    var resultBlock = await DispatchToolAsync(request, runCtx, use, parentMessageId, ct).ConfigureAwait(false);
                    toolResults.Add(resultBlock);
                }

                transcript.Add(new LlmMessage { Role = "user", Content = toolResults });
                parentMessageId = await PersistMessageAsync(
                    request, parentMessageId, transcript[^1],
                    iteration: iter, stopReason: null, usage: LlmUsage.Empty,
                    toolUseId: null,
                    temperature: null, maxTokens: null, topP: null,
                    toolSetHash: null, providerSystemFingerprint: null,
                    providerResponseId: null, latencyMs: null,
                    retryCount: retryCount,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            throw;
        }
        catch (Exception ex)
        {
            terminalException = ex;
            throw;
        }
        finally
        {
            await _observer.OnRunCompletedAsync(new AgentRunCompletedContext
            {
                Run = runCtx,
                Iterations = iter,
                TotalUsage = new LlmUsage(totalUsage.InputTokens, totalUsage.OutputTokens),
                StopReason = last?.StopReason ?? LlmStopReason.Other,
                Exception = terminalException,
                Cancelled = cancelled
            }, CancellationToken.None).ConfigureAwait(false);
        }

        return new AgentResponse
        {
            Content = transcript[^1].Content,
            Usage = new LlmUsage(totalUsage.InputTokens, totalUsage.OutputTokens),
            Iterations = iter,
            StopReason = last?.StopReason ?? LlmStopReason.Other
        };
    }

    private async Task<LlmToolResultBlock> DispatchToolAsync(
        AgentRequest request, AgentRunContext runCtx, LlmToolUseBlock use, string? messageId, CancellationToken ct)
    {
        request.Exchange.setProperty(LlmExpressionKeys.Tool, new LlmToolContext
        {
            Name = use.Name,
            ToolUseId = use.ToolUseId,
            InputJson = use.InputJson,
            ResultJson = null,
            Duration = TimeSpan.Zero
        });

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await DispatchToolCoreAsync(request, runCtx, use, messageId, ct).ConfigureAwait(false);
            sw.Stop();
            request.Exchange.setProperty(LlmExpressionKeys.Tool, new LlmToolContext
            {
                Name = use.Name,
                ToolUseId = use.ToolUseId,
                InputJson = use.InputJson,
                ResultJson = result.OutputJson,
                Duration = sw.Elapsed
            });
            return result;
        }
        finally
        {
            // Tool context lives only for the duration of dispatch — clear after the
            // caller has had a chance to read ${tool.*} for logging/metrics.
            request.Exchange.Properties.Remove(LlmExpressionKeys.Tool);
        }
    }

    private async Task<LlmToolResultBlock> DispatchToolCoreAsync(
        AgentRequest request, AgentRunContext runCtx, LlmToolUseBlock use, string? messageId, CancellationToken ct)
    {
        var tool = FindTool(request.Tools, use.Name);
        if (tool is null)
        {
            return new LlmToolResultBlock(use.ToolUseId,
                $"{{\"error\":\"unknown tool '{use.Name}'\"}}", IsError: true);
        }

        var redactedInput = _redaction.Redact(use.InputJson, RedactionContext.ToolInput);

        if (tool.Capability.Safety.RequiresApproval)
        {
            var approvalReq = new ApprovalRequest
            {
                ConversationId = request.ConversationId,
                Tool = tool.Capability,
                InputJson = redactedInput,
                Exchange = request.Exchange,
                ToolUseId = use.ToolUseId
            };
            var decision = await _approval.AwaitAsync(approvalReq, ct).ConfigureAwait(false);
            if (_approvalStore is not null)
                await _approvalStore.RecordAsync(approvalReq, decision, request.Exchange, ct).ConfigureAwait(false);
            if (!decision.Approved)
            {
                await _observer.OnToolInvokedAsync(new AgentToolInvocationContext
                {
                    Run = runCtx,
                    Tool = tool.Capability,
                    InputJson = redactedInput,
                    OutputJson = null,
                    ToolUseId = use.ToolUseId,
                    Duration = TimeSpan.Zero,
                    Skipped = true,
                    SkipReason = $"denied: {decision.Reason ?? "no reason"}"
                }, ct).ConfigureAwait(false);
                return new LlmToolResultBlock(use.ToolUseId,
                    JsonSerializer.Serialize(new { error = "approval_denied", reason = decision.Reason }, JsonOptions),
                    IsError: true);
            }
        }

        ToolIdempotencyReservation? reservation = null;
        if (_idempotency is not null && request.ConversationId is { } convId)
        {
            reservation = await _idempotency.TryReserveAsync(convId, use.ToolUseId, request.Exchange, ct).ConfigureAwait(false);
            if (!reservation.IsNew)
            {
                await _observer.OnToolInvokedAsync(new AgentToolInvocationContext
                {
                    Run = runCtx,
                    Tool = tool.Capability,
                    InputJson = redactedInput,
                    OutputJson = reservation.CachedOutputJson,
                    ToolUseId = use.ToolUseId,
                    Duration = TimeSpan.Zero,
                    Skipped = true,
                    SkipReason = "idempotent_cache_hit"
                }, ct).ConfigureAwait(false);
                return new LlmToolResultBlock(use.ToolUseId, reservation.CachedOutputJson ?? "{}");
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var output = await DispatchToolEndpointAsync(request, tool, use, ct).ConfigureAwait(false);
            sw.Stop();
            LlmMetrics.ToolInvocations.Add(1, new KeyValuePair<string, object?>("llm.tool.name", use.Name));

            if (reservation is not null && request.ConversationId is { } completeConvId)
                await _idempotency!.CompleteAsync(completeConvId, use.ToolUseId, output, request.Exchange, ct).ConfigureAwait(false);

            var redactedOutput = _redaction.Redact(output, RedactionContext.ToolOutput);

            await _observer.OnToolInvokedAsync(new AgentToolInvocationContext
            {
                Run = runCtx,
                Tool = tool.Capability,
                InputJson = redactedInput,
                OutputJson = redactedOutput,
                ToolUseId = use.ToolUseId,
                Duration = sw.Elapsed
            }, ct).ConfigureAwait(false);

            return new LlmToolResultBlock(use.ToolUseId, output);
        }
        catch (Exception ex)
        {
            sw.Stop();
            LlmMetrics.ToolFailures.Add(1,
                new KeyValuePair<string, object?>("llm.tool.name", use.Name),
                new KeyValuePair<string, object?>("exception.type", ex.GetType().Name));
            _logger?.LogError(ex, "Tool '{Name}' failed", use.Name);

            if (reservation is not null && request.ConversationId is { } releaseConvId)
                await _idempotency!.ReleaseAsync(releaseConvId, use.ToolUseId, request.Exchange, ct).ConfigureAwait(false);

            await _observer.OnToolInvokedAsync(new AgentToolInvocationContext
            {
                Run = runCtx,
                Tool = tool.Capability,
                InputJson = redactedInput,
                OutputJson = null,
                ToolUseId = use.ToolUseId,
                Duration = sw.Elapsed,
                Exception = ex
            }, ct).ConfigureAwait(false);

            return new LlmToolResultBlock(use.ToolUseId,
                $"{{\"error\":\"{ex.GetType().Name}: {EscapeJson(ex.Message)}\"}}", IsError: true);
        }
    }

    private async Task<string?> PersistMessageAsync(
        AgentRequest request,
        string? parentId,
        LlmMessage message,
        int iteration,
        LlmStopReason? stopReason,
        LlmUsage usage,
        string? toolUseId,
        double? temperature,
        int? maxTokens,
        double? topP,
        string? toolSetHash,
        string? providerSystemFingerprint,
        string? providerResponseId,
        long? latencyMs,
        int? retryCount,
        CancellationToken ct)
    {
        if (_conversation is null || request.ConversationId is null) return parentId;

        return await _conversation.AppendAsync(
            request.ConversationId,
            parentId,
            message,
            new ConversationMessageMeta
            {
                CreatedAtUtc = DateTime.UtcNow,
                Iteration = iteration,
                ProviderId = request.Factory.Provider,
                ModelId = request.Factory.ModelId,
                StopReason = stopReason,
                Usage = usage,
                ToolUseId = toolUseId,
                Temperature = temperature,
                MaxTokens = maxTokens,
                TopP = topP,
                PromptTemplateName = request.PromptTemplateName,
                PromptTemplateVersion = request.PromptTemplateVersion,
                ToolSetHash = toolSetHash,
                ProviderSystemFingerprint = providerSystemFingerprint,
                UserId = request.UserId,
                AuditTags = request.AuditTags,
                FactoryName = string.IsNullOrEmpty(request.Factory.Name) ? null : request.Factory.Name,
                BaseUrl = request.Factory.BaseUrl?.ToString(),
                ProviderResponseId = providerResponseId,
                LatencyMs = latencyMs,
                ApiKeyFingerprint = ComputeApiKeyFingerprint(request.Factory.ApiKey),
                RetryCount = retryCount
            },
            request.Exchange,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the retry counter the route framework stamps on the inbound
    /// exchange before the engine runs. Sources, in priority order: the
    /// per-step <c>RetryProcessor</c> property, the <c>OnExceptionProcessor</c>
    /// header, then the <c>DeadLetterProcessor</c> header. Returns null when
    /// none are present (first / only delivery).
    /// </summary>
    private static int? ReadRetryCount(IExchange exchange)
    {
        if (exchange.Properties.TryGetValue("RetryAttempt", out var prop) && TryToInt(prop, out var fromProp))
            return fromProp;
        if (exchange.In?.Headers is { } headers)
        {
            if (headers.TryGetValue("CamelRedeliveryCounter", out var redeliv) && TryToInt(redeliv, out var fromRedeliv))
                return fromRedeliv;
            if (headers.TryGetValue("CamelDeadLetterRedeliveryCount", out var dlq) && TryToInt(dlq, out var fromDlq))
                return fromDlq;
        }
        return null;

        static bool TryToInt(object? raw, out int value)
        {
            switch (raw)
            {
                case int i: value = i; return true;
                case long l: value = (int)l; return true;
                case string s when int.TryParse(s, out var parsed): value = parsed; return true;
                default: value = 0; return false;
            }
        }
    }

    /// <summary>
    /// Stable, non-secret fingerprint of the API key — SHA-256 of the UTF-8
    /// bytes, first 16 hex chars. Empty / null key → null fingerprint.
    /// </summary>
    private static string? ComputeApiKeyFingerprint(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(apiKey), hash);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private async Task SafeRunShadowAsync(ILlmProvider provider, LlmRequest request, LlmResponse response, CancellationToken ct)
    {
        try { await _shadow.RunAsync(provider, request, response, ct).ConfigureAwait(false); }
        catch { /* shadow failures must never affect the primary run */ }
    }

    private static void PublishConversationContext(
        AgentRequest request, IReadOnlyList<LlmMessage> transcript, int iterations, AgentUsage totalUsage)
    {
        LlmMessage? lastAssistant = null;
        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            if (transcript[i].Role == "assistant") { lastAssistant = transcript[i]; break; }
        }

        request.Exchange.setProperty(LlmExpressionKeys.Conversation, new LlmConversationContext
        {
            Id = request.ConversationId,
            MessageCount = transcript.Count,
            Tokens = new LlmUsage(totalUsage.InputTokens, totalUsage.OutputTokens),
            Iterations = iterations,
            LastMessage = lastAssistant
        });
    }

    private static LlmToolCapability[] ProjectCapabilities(IReadOnlyList<ILlmToolDescriptor> tools)
    {
        if (tools.Count == 0) return [];
        var arr = new LlmToolCapability[tools.Count];
        for (var i = 0; i < tools.Count; i++) arr[i] = tools[i].Capability;
        return arr;
    }

    private static ILlmToolDescriptor? FindTool(IReadOnlyList<ILlmToolDescriptor> tools, string name)
    {
        foreach (var t in tools)
            if (t.Capability.Name == name) return t;
        return null;
    }

    /// <summary>
    /// Dispatches one tool call onto its redb.Route endpoint.
    /// <para>
    /// The tool runs on a <b>child of the agent exchange</b>, not on a freshly minted
    /// one: <see cref="IExchange.CreateLinkedChild"/> carries over the parent's
    /// <c>Properties</c> (including <c>LlmKeys.RedbName</c>), its <c>RouteId</c> and its
    /// DI scope, so scoped services the conversation resolved — principal, tenant
    /// accessor, per-exchange <c>IRedbService</c> — are the same instances inside the
    /// tool. This is what <see cref="ILlmToolDescriptor"/> documents and what
    /// <c>docs/LLM/PLAN.md §4.1</c> specified; building a naked exchange severed all of it.
    /// </para>
    /// <para>
    /// <b>Linked</b> (shared scope) rather than a scope of its own: tool calls are
    /// dispatched sequentially inside one iteration (see the <c>foreach</c> in
    /// <see cref="RunAsync"/>), so there is no concurrent resolution on the shared
    /// scope — the race that <c>docs/DI_SCOPE_PER_EXCHANGE.md</c> warns about applies to
    /// parallel forks, not to an inline request/reply. A child scope would also defeat
    /// the point: the tool would get an empty scope instead of the conversation's.
    /// </para>
    /// <para>
    /// Headers are filtered by <see cref="ToolHeaderPolicy"/> (default-deny), never
    /// copied wholesale.
    /// </para>
    /// </summary>
    private async Task<string> DispatchToolEndpointAsync(
        AgentRequest request,
        ILlmToolDescriptor descriptor,
        LlmToolUseBlock use,
        CancellationToken ct)
    {
        if (_producerTemplate is null)
            throw new InvalidOperationException(
                "AgentEngine has no IProducerTemplate. Register the engine via AddRedbRouteLlm() so the producer template is injected.");

        var parentExchange = request.Exchange;

        var endpointUri = descriptor.BuildEndpointUri(use.InputJson, parentExchange);
        if (string.IsNullOrWhiteSpace(endpointUri))
            throw new InvalidOperationException(
                $"Tool '{descriptor.Capability.Name}' returned an empty endpoint URI.");

        var msg = new Message(use.InputJson) { ContentType = "application/json" };
        msg.Headers[LlmHeaders.ToolName] = descriptor.Capability.Name;
        msg.Headers[LlmHeaders.ToolBridgeEndpoint] = endpointUri;

        ToolHeaderPolicy.Apply(request, msg.Headers);

        var child = parentExchange.CreateLinkedChild(msg);
        try
        {
            ct.ThrowIfCancellationRequested();
            var done = await _producerTemplate.RequestAsync(endpointUri, child, ct).ConfigureAwait(false);
            return SerializeReply(done.Out?.Body ?? done.In.Body);
        }
        finally
        {
            // Releases only what the tool route itself opened — the named
            // `__redb_scope:*` entries it resolved (CreateLinkedChild does not copy the
            // parent's). The shared parent scope is marked not-owned on the child, so
            // the conversation keeps its scope and connections after the tool returns.
            await child.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static readonly JsonSerializerOptions ToolReplyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Non-ASCII tool output stays UTF-8 (not \uXXXX): escaped Cyrillic / CJK
        // would ~6× the tokens the model sees and can surface literal \u escapes.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string SerializeReply(object? reply)
    {
        if (reply is null) return "null";
        if (reply is string s) return JsonSerializer.Serialize(s, ToolReplyJsonOptions);
        if (reply is IMessage m) return SerializeReply(m.Body);
        if (reply is byte[] bytes) return JsonSerializer.Serialize(Convert.ToBase64String(bytes), ToolReplyJsonOptions);
        return JsonSerializer.Serialize(reply, reply.GetType(), ToolReplyJsonOptions);
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Stable hash of the tool-capability set exposed to the model on a single
    /// run. Sorted by name; the source <c>InputSchema</c> string is folded in
    /// verbatim — if its key order or whitespace changes between runs, the
    /// hash changes too, which is exactly the signal an auditor wants
    /// ("the same prompt saw a different tool surface yesterday"). Returns
    /// null for the empty set so unused-tool runs don't pollute the column.
    /// </summary>
    private static string? ComputeToolSetHash(IReadOnlyList<LlmToolCapability> capabilities)
    {
        if (capabilities.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var cap in capabilities.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            sb.Append(cap.Name).Append('\n');
            sb.Append(cap.Description ?? string.Empty).Append('\n');
            sb.Append(cap.InputSchema ?? string.Empty).Append('\n');
            sb.Append("---\n");
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
