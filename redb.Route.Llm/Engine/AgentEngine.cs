using System.Diagnostics;
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
    private static readonly JsonSerializerOptions JsonOptions = new();

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

        var runCtx = new AgentRunContext
        {
            ConversationId = request.ConversationId,
            FactoryName = request.Factory.Name,
            ProviderId = request.Factory.Provider,
            ModelId = request.Factory.ModelId ?? string.Empty,
            ExchangeId = request.Exchange.ExchangeId
        };
        await _observer.OnRunStartedAsync(runCtx, ct).ConfigureAwait(false);

        var transcript = new List<LlmMessage>();
        string? attachUnderId = request.ConversationParentMessageId;

        if (_conversation is not null && request.ConversationId is { } convIdToLoad)
        {
            var path = await _conversation.LoadPathAsync(
                convIdToLoad, request.ConversationParentMessageId, ct).ConfigureAwait(false);
            foreach (var node in path)
                transcript.Add(node.Message);
            if (path.Count > 0)
                attachUnderId = path[^1].Id;
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
                    Temperature = request.Temperature ?? request.Factory.Temperature,
                    MaxTokens = request.MaxTokens ?? request.Factory.MaxTokens,
                    TopP = request.Factory.TopP
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
                    toolUseId: null, ct).ConfigureAwait(false);

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
                    toolUseId: null, ct).ConfigureAwait(false);
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
                await _approvalStore.RecordAsync(approvalReq, decision, ct).ConfigureAwait(false);
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
            reservation = await _idempotency.TryReserveAsync(convId, use.ToolUseId, ct).ConfigureAwait(false);
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
            var output = await DispatchToolEndpointAsync(tool, use, request.Exchange, ct).ConfigureAwait(false);
            sw.Stop();
            LlmMetrics.ToolInvocations.Add(1, new KeyValuePair<string, object?>("llm.tool.name", use.Name));

            if (reservation is not null && request.ConversationId is { } completeConvId)
                await _idempotency!.CompleteAsync(completeConvId, use.ToolUseId, output, ct).ConfigureAwait(false);

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
                await _idempotency!.ReleaseAsync(releaseConvId, use.ToolUseId, ct).ConfigureAwait(false);

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
                ToolUseId = toolUseId
            },
            ct).ConfigureAwait(false);
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

    private async Task<string> DispatchToolEndpointAsync(
        ILlmToolDescriptor descriptor,
        LlmToolUseBlock use,
        IExchange parentExchange,
        CancellationToken ct)
    {
        if (_producerTemplate is null)
            throw new InvalidOperationException(
                "AgentEngine has no IProducerTemplate. Register the engine via AddRedbRouteLlm() so the producer template is injected.");

        var endpointUri = descriptor.BuildEndpointUri(use.InputJson, parentExchange);
        if (string.IsNullOrWhiteSpace(endpointUri))
            throw new InvalidOperationException(
                $"Tool '{descriptor.Capability.Name}' returned an empty endpoint URI.");

        var msg = new Message(use.InputJson) { ContentType = "application/json" };
        msg.Headers[LlmHeaders.ToolName] = descriptor.Capability.Name;
        msg.Headers[LlmHeaders.ToolBridgeEndpoint] = endpointUri;

        if (parentExchange.In?.Headers is { } srcHeaders)
        {
            CopyHeader(srcHeaders, msg.Headers, LlmHeaders.ConversationId);
            CopyHeader(srcHeaders, msg.Headers, "X-Correlation-Id");
            CopyHeader(srcHeaders, msg.Headers, "CorrelationId");
        }

        ct.ThrowIfCancellationRequested();
        var reply = await _producerTemplate.RequestBody(endpointUri, msg).ConfigureAwait(false);
        return SerializeReply(reply);
    }

    private static void CopyHeader(IDictionary<string, object?> from, IDictionary<string, object?> to, string key)
    {
        if (from.TryGetValue(key, out var v) && v is not null)
            to[key] = v;
    }

    private static readonly JsonSerializerOptions ToolReplyJsonOptions = new(JsonSerializerDefaults.Web);

    private static string SerializeReply(object? reply)
    {
        if (reply is null) return "null";
        if (reply is string s) return JsonSerializer.Serialize(s, ToolReplyJsonOptions);
        if (reply is IMessage m) return SerializeReply(m.Body);
        if (reply is byte[] bytes) return JsonSerializer.Serialize(Convert.ToBase64String(bytes), ToolReplyJsonOptions);
        return JsonSerializer.Serialize(reply, reply.GetType(), ToolReplyJsonOptions);
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
