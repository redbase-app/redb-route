using System.Diagnostics;
using System.Security.Claims;
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
using redb.Route.Llm.Tools;

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
    private readonly IToolClaimsSource? _claimsSource;
    private readonly IToolCacheStore? _toolCache;
    private readonly ICostCalculator _costCalculator;

    /// <summary>
    /// Backwards-compatible constructor — wires every dependency to its no-op
    /// implementation. Use the DI-driven constructor for production wiring.
    /// </summary>
    public AgentEngine(ILogger<AgentEngine>? logger = null)
        : this(logger, producerTemplate: null, observer: null, budget: null, approval: null, redaction: null, shadow: null, conversation: null, idempotency: null, approvalStore: null)
    {
    }

    /// <summary>Creates an engine with explicit governance / storage dependencies.</summary>
    /// <param name="logger">Optional logger for governance decisions and tool failures.</param>
    /// <param name="producerTemplate">Producer used to dispatch tool calls onto their endpoints.</param>
    /// <param name="observer">Observability sink; defaults to <see cref="NoopAgentObserver"/>.</param>
    /// <param name="budget">Budget enforcer; defaults to <see cref="NoopBudgetEnforcer"/> (no ceiling).</param>
    /// <param name="approval">Approval gate; defaults to <see cref="AutoApproveGate"/>.</param>
    /// <param name="redaction">Redaction filter; defaults to <see cref="NoopRedactionFilter"/>.</param>
    /// <param name="shadow">Shadow runner; defaults to <see cref="NoopShadowRunner"/>.</param>
    /// <param name="conversation">Conversation store; absent means the run is not persisted.</param>
    /// <param name="idempotency">Idempotency store, consulted for tools with a declared side effect.</param>
    /// <param name="approvalStore">Audit sink for approval decisions; absent means none are recorded.</param>
    /// <param name="claimsSource">
    /// Optional claims source. When absent, a tool that declares
    /// <see cref="LlmToolSafety.RequiredClaims"/> is denied — fail closed by design.
    /// </param>
    /// <param name="toolCache">
    /// Optional cross-run tool cache. Consulted only for <c>ToolCachingPolicy.Persist</c>;
    /// <c>Memoize</c> is served by a run-scoped in-process cache. When absent, persisted caching
    /// degrades to "not cached" rather than failing.
    /// </param>
    /// <param name="costCalculator">
    /// Prices provider responses for cost budgets; defaults to <see cref="NullCostCalculator"/>, which
    /// makes a run that requests a cost ceiling fail fast instead of enforcing a zero ceiling.
    /// </param>
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
        IApprovalStore? approvalStore,
        IToolClaimsSource? claimsSource = null,
        IToolCacheStore? toolCache = null,
        ICostCalculator? costCalculator = null)
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
        _claimsSource = claimsSource;
        _toolCache = toolCache;
        _costCalculator = costCalculator ?? new NullCostCalculator();
    }

    /// <summary>
    /// Builds an engine from whatever the route context and its DI container can provide — the
    /// production composition path. Every seam is optional here (an absent store means "not configured",
    /// not "broken"), so a host only has to call <c>AddRedbRouteLlm()</c>, and the engine it resolves
    /// carries the governance the package registered.
    /// <para>
    /// Constructing an engine by hand is still possible, but then every seam is the caller's to wire —
    /// including the ones added since: use this factory (or DI) unless you intend to replace them all.
    /// </para>
    /// </summary>
    /// <param name="context">Route context whose locator and DI provider are searched for the dependencies.</param>
    public static AgentEngine FromContext(IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AgentEngine(
            logger: Resolve<ILogger<AgentEngine>>(context)
                ?? Resolve<ILoggerFactory>(context)?.CreateLogger<AgentEngine>(),
            producerTemplate: Resolve<IProducerTemplate>(context),
            // Absent collaborators fall back to the same no-ops <c>AddRedbRouteLlm()</c> registers, so a
            // bare context (a demo, a hand-rolled host) gets a working engine instead of null references.
            observer: Resolve<IAgentObserver>(context) ?? new NoopAgentObserver(),
            budget: Resolve<IBudgetEnforcer>(context) ?? new NoopBudgetEnforcer(),
            approval: Resolve<IApprovalGate>(context) ?? new AutoApproveGate(),
            redaction: Resolve<IRedactionFilter>(context) ?? new NoopRedactionFilter(),
            shadow: Resolve<IShadowRunner>(context) ?? new NoopShadowRunner(),
            conversation: Resolve<IConversationStore>(context),
            idempotency: Resolve<IToolIdempotencyStore>(context),
            approvalStore: Resolve<IApprovalStore>(context),
            claimsSource: Resolve<IToolClaimsSource>(context),
            toolCache: Resolve<IToolCacheStore>(context),
            costCalculator: Resolve<ICostCalculator>(context));
    }

    /// <summary>
    /// Finds the engine a host registered — first in the route context's own locator, then in the DI
    /// container — or <c>null</c> when the host registered none. Producers, consumers and the inline step
    /// must all use this: <see cref="IRouteContext.GetService{T}"/> deliberately looks only at the locator,
    /// so asking it alone misses the engine <c>AddRedbRouteLlm()</c> registered in the container.
    /// </summary>
    public static IAgentEngine? FindRegistered(IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.GetService<IAgentEngine>()
               ?? context.GetServiceProvider()?.GetService(typeof(IAgentEngine)) as IAgentEngine;
    }

    /// <summary>
    /// The context's own locator wins (a host may explicitly override a seam there), then the DI
    /// provider. <see cref="IRouteContext.GetService{T}"/> deliberately looks only at the locator, so
    /// asking one place would miss everything a container registered.
    /// </summary>
    private static T? Resolve<T>(IRouteContext context) where T : class
        => context.GetService<T>()
           ?? context.GetServiceProvider()?.GetService(typeof(T)) as T;

    /// <inheritdoc />
    public async Task<AgentResponse> RunAsync(AgentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A cost ceiling that cannot be priced must fail loudly and before the first provider call:
        // the alternative is a run that reports "budget enforced" while pricing every call as zero.
        if (request.Budget.MaxCostUsd > 0m
            && _costCalculator.Estimate(LlmUsage.Empty, request.Factory) is null)
        {
            throw new InvalidOperationException(
                $"This run requests a cost budget of {request.Budget.MaxCostUsd:F4} USD, but the registered "
                + $"ICostCalculator cannot price model '{request.Factory.ModelId}'. Register a calculator that "
                + "knows the model's rates, or drop the cost ceiling.");
        }

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

        // One memo per run, dropped when this method returns: "memoised for the lifetime of the agent
        // run" is exactly what ToolCachingPolicy.Memoize promises, and nothing may outlive it.
        var runCache = new AgentRunToolCache();

        // The preamble opens the transcript and stays out of the store: it is part of how the
        // assistant is assembled, not something said in this dialog (see AgentRequest.Preamble).
        var transcript = new List<LlmMessage>(request.Preamble);
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

        // Cache counters ride alongside AgentUsage rather than inside it. AgentUsage is the
        // BUDGET currency — what the run is allowed to spend — and cache reads/writes are not
        // billable input; folding them in would make every budget silently wrong. But dropping
        // them was worse: the run's own answer to "is the prompt cache working?" is these two
        // numbers, and until now RunAsync summed them per iteration and then threw them away.
        var cacheWrite = 0;
        var cacheRead = 0;

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

                var pre = await OutsideTransactionAsync(
                    () => _budget.PreCheckAsync(request.ConversationId, request.Budget, totalUsage, ct)).ConfigureAwait(false);
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
                    // Every iteration of the tool loop resends the same system prompt, so the
                    // flag rides along — the second turn onward is where the cache pays off.
                    CacheSystemPrompt = request.CacheSystemPrompt,
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

                // Cost is what the deployment can price, not a placeholder zero: a cost ceiling must
                // not silently collapse into a tokens-only budget.
                var iterUsage = new AgentUsage(
                    last.Usage.InputTokens,
                    last.Usage.OutputTokens,
                    _costCalculator.Estimate(last.Usage, request.Factory) ?? 0m);
                totalUsage = totalUsage.Add(iterUsage);

                cacheWrite += last.Usage.CacheCreationInputTokens;
                cacheRead += last.Usage.CacheReadInputTokens;

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

                var post = await OutsideTransactionAsync(() => _budget.RecordAndCheckAsync(
                    request.ConversationId, request.Budget, iterUsage, totalUsage, ct)).ConfigureAwait(false);
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

                    var resultBlock = await DispatchToolAsync(request, runCtx, runCache, use, parentMessageId, ct).ConfigureAwait(false);
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
                TotalUsage = new LlmUsage(
                    totalUsage.InputTokens, totalUsage.OutputTokens, cacheWrite, cacheRead),
                StopReason = last?.StopReason ?? LlmStopReason.Other,
                Exception = terminalException,
                Cancelled = cancelled
            }, CancellationToken.None).ConfigureAwait(false);
        }

        // Return the last ASSISTANT content, not transcript[^1]: when the loop stops mid-round (MaxIterations
        // or budget) the tail is a tool-results user message with no text, which would surface as an empty
        // answer. The last assistant message carries the model's actual output.
        // ...and never the preamble's assistant turn: a run that stopped before its first call
        // (budget pre-check, MaxIterations=0) has no answer, and the fixed opening line must not
        // be handed back as if the model had just said it.
        LlmMessage? lastAssistant = null;
        for (var i = transcript.Count - 1; i >= request.Preamble.Count; i--)
            if (transcript[i].Role == "assistant") { lastAssistant = transcript[i]; break; }

        return new AgentResponse
        {
            Content = (lastAssistant ?? transcript[^1]).Content,
            Usage = new LlmUsage(
                totalUsage.InputTokens, totalUsage.OutputTokens, cacheWrite, cacheRead),
            Iterations = iter,
            StopReason = last?.StopReason ?? LlmStopReason.Other
        };
    }

    private async Task<LlmToolResultBlock> DispatchToolAsync(
        AgentRequest request, AgentRunContext runCtx, AgentRunToolCache runCache,
        LlmToolUseBlock use, string? messageId, CancellationToken ct)
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
            var result = await DispatchToolCoreAsync(request, runCtx, runCache, use, messageId, ct).ConfigureAwait(false);
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
        AgentRequest request, AgentRunContext runCtx, AgentRunToolCache runCache,
        LlmToolUseBlock use, string? messageId, CancellationToken ct)
    {
        var tool = FindTool(request.Tools, use.Name);
        if (tool is null)
        {
            return new LlmToolResultBlock(use.ToolUseId,
                JsonSerializer.Serialize(new { error = "unknown_tool", name = use.Name }, JsonOptions), IsError: true);
        }

        var redactedInput = _redaction.Redact(use.InputJson, RedactionContext.ToolInput);

        // Claims are verified before approval: a caller that cannot prove its identity must not even
        // reach the approver's queue. Fail closed — a tool that declares claims this run cannot
        // verify is never invoked. The model is told the error code only; the missing-claim list
        // stays in the observer/audit channel, so the tool's policy never leaks to the model.
        var requiredClaims = tool.Capability.Safety.RequiredClaims;
        if (requiredClaims.Count > 0)
        {
            var held = _claimsSource?.GetClaims(request.Exchange);
            var missing = held is null
                ? [.. requiredClaims]
                : requiredClaims.Where(c => !held.Contains(c, StringComparer.Ordinal)).ToArray();

            if (missing.Length > 0)
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
                    SkipReason = $"{ToolSkipReasons.ClaimsMissing}:{string.Join(",", missing)}"
                }, ct).ConfigureAwait(false);

                if (held is null)
                {
                    // No principal at all is usually a transport/wiring problem, so it earns a warning.
                    _logger?.LogWarning(
                        "Tool '{Tool}' denied: no verifiable principal. Missing: [{Claims}]. Claims source: {Source}.",
                        use.Name, string.Join(", ", missing),
                        _claimsSource is null ? "none registered" : _claimsSource.GetType().Name);
                }
                else
                {
                    // A caller that simply does not hold the claim is a normal decision, not an incident.
                    _logger?.LogInformation(
                        "Tool '{Tool}' denied: the principal lacks the required claims. Missing: [{Claims}].",
                        use.Name, string.Join(", ", missing));
                }

                return new LlmToolResultBlock(
                    use.ToolUseId,
                    JsonSerializer.Serialize(new { error = ToolResultErrors.ClaimsMissing }, JsonOptions),
                    IsError: true);
            }
        }

        // An external tool (mail, payment, deployment) does something no transaction can take back. Inside an
        // ambient transaction - a route's .Transacted() - the run's database work and deferred sends roll back
        // together, but the external action would stay done, and a retry after the rollback would do it again.
        // So it is not run there: the model gets an error it can answer around, the audit channel records why.
        // Checked before approval, so nobody is asked to approve a call that cannot run.
        if (tool.Capability.Safety.SideEffect == ToolSideEffect.External
            && System.Transactions.Transaction.Current is not null)
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
                SkipReason = ToolSkipReasons.ExternalInTransaction
            }, ct).ConfigureAwait(false);

            _logger?.LogWarning(
                "Tool '{Tool}' declares SideEffect=External and was called inside an ambient transaction; it was not run, " +
                "because its action cannot be rolled back with the transaction. Call it from a route without .Transacted().",
                use.Name);

            return new LlmToolResultBlock(
                use.ToolUseId,
                JsonSerializer.Serialize(new { error = ToolResultErrors.ExternalInTransaction }, JsonOptions),
                IsError: true);
        }

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
                    SkipReason = $"{ToolSkipReasons.ApprovalDeniedPrefix} {decision.Reason ?? "no reason"}"
                }, ct).ConfigureAwait(false);
                return new LlmToolResultBlock(use.ToolUseId,
                    JsonSerializer.Serialize(new { error = ToolResultErrors.ApprovalDenied, reason = decision.Reason }, JsonOptions),
                    IsError: true);
            }
        }

        // Cache lookup runs before the idempotency reservation: a hit means "this exact input already
        // produced this output", which is cheaper than asking "was this tool_use already executed?".
        // Approval ran first, so a cached result can never bypass a gate.
        //
        // The gate is the tool's own declaration, checked here as well as at registration: only a
        // read-only tool may be cached, because a cached entry suppresses the side effect it stands for.
        // A descriptor that reached the engine without that check (a hand-built AgentRequest) is therefore
        // not cached — and says so, instead of silently mutating nothing.
        //
        // The address is resolved BEFORE the key is built and then REUSED for the dispatch: a descriptor
        // may address a per-tenant endpoint, and a BuildEndpointUri that is not pure must not produce one
        // address for the key and another for the call.
        string? memoKey = null;
        string? storeKey = null;
        string? resolvedToolUri = null;
        var lookedUp = false;
        var caching = tool.Capability.Safety.Caching;
        var cacheable = caching != ToolCachingPolicy.None
            && tool.Capability.Safety.SideEffect == ToolSideEffect.ReadOnly;

        if (caching != ToolCachingPolicy.None && !cacheable)
        {
            _logger?.LogWarning(
                "Tool '{Tool}' declares Caching={Caching} with SideEffect={SideEffect}; its result is not cached. "
                + "Only read-only tools may be cached — registration rejects this combination, so this descriptor "
                + "reached the engine without it.",
                use.Name, caching, tool.Capability.Safety.SideEffect);
        }

        if (cacheable)
        {
            resolvedToolUri = tool.BuildEndpointUri(use.InputJson, request.Exchange);
            var policyFingerprint = ToolCacheKey.PolicyFingerprint(tool.Capability.Safety);
            var callerFingerprint = CallerFingerprint(request);
            memoKey = caching == ToolCachingPolicy.Memoize
                ? ToolCacheKey.MemoKey(
                    tool.Capability.Name, resolvedToolUri, policyFingerprint, callerFingerprint, use.InputJson)
                : null;
            storeKey = ToolCacheKey.StoreKey(
                tool.Capability.Name, resolvedToolUri, use.InputJson, caching, policyFingerprint, callerFingerprint);

            string? cachedOutput = null;
            if (memoKey is not null)
            {
                cachedOutput = runCache.Get(memoKey);
                lookedUp = true;
            }
            if (cachedOutput is null && storeKey is not null && _toolCache is not null)
            {
                // Best effort: a cache that cannot answer must not fail the run. A store outage is an
                // operational incident, not a reason to refuse work the tool itself can still do.
                try
                {
                    cachedOutput = await _toolCache.GetAsync(storeKey, request.Exchange, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex,
                        "Tool-cache read failed for '{Tool}'; continuing without the cache.", use.Name);
                    cachedOutput = null;
                }

                lookedUp = true;
            }

            if (cachedOutput is not null)
            {
                LlmMetrics.ToolCacheHits.Add(1, new KeyValuePair<string, object?>("llm.tool.name", use.Name));
                await _observer.OnToolInvokedAsync(new AgentToolInvocationContext
                {
                    Run = runCtx,
                    Tool = tool.Capability,
                    InputJson = redactedInput,
                    OutputJson = cachedOutput,
                    ToolUseId = use.ToolUseId,
                    Duration = TimeSpan.Zero,
                    Skipped = true,
                    SkipReason = ToolSkipReasons.CacheHit
                }, ct).ConfigureAwait(false);
                return new LlmToolResultBlock(use.ToolUseId, cachedOutput);
            }

            if (lookedUp)
                LlmMetrics.ToolCacheMisses.Add(1, new KeyValuePair<string, object?>("llm.tool.name", use.Name));
        }

        // Idempotency is opt-in per side-effect class, as the tool's Safety declares: a read-only tool
        // has no side effect to de-duplicate, and reserving it costs store round-trips on every call.
        // Note what this implies for tools that declare nothing — SideEffect defaults to ReadOnly, so
        // "no declaration" means "no replay protection".
        var needsIdempotency = tool.Capability.Safety.SideEffect != ToolSideEffect.ReadOnly;

        ToolIdempotencyReservation? reservation = null;
        if (_idempotency is not null && request.ConversationId is { } convId && needsIdempotency)
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
                    SkipReason = ToolSkipReasons.IdempotencyHit
                }, ct).ConfigureAwait(false);
                return new LlmToolResultBlock(use.ToolUseId, reservation.CachedOutputJson ?? "{}");
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var output = await DispatchToolEndpointAsync(request, tool, use, resolvedToolUri, ct).ConfigureAwait(false);
            sw.Stop();
            LlmMetrics.ToolInvocations.Add(1, new KeyValuePair<string, object?>("llm.tool.name", use.Name));

            if (reservation is not null && request.ConversationId is { } completeConvId)
                await _idempotency!.CompleteAsync(completeConvId, use.ToolUseId, output, request.Exchange, ct).ConfigureAwait(false);

            var redactedOutput = _redaction.Redact(output, RedactionContext.ToolOutput);

            // Store only an output the redaction filter left untouched. Storing the redacted body
            // would hand the model different data on a hit than on a miss (the returned block below
            // carries the raw body); storing the raw body would persist redaction-bypassing content in
            // the cache for the whole TTL. The price is honest and documented: a tool whose output is
            // redacted is not cached at all.
            if (!string.Equals(output, redactedOutput, StringComparison.Ordinal))
            {
                _logger?.LogDebug(
                    "Tool '{Name}' output was redacted — result is not cached.", use.Name);
            }
            else
            {
                if (memoKey is not null) runCache.Set(memoKey, output);
                if (storeKey is not null && _toolCache is not null)
                {
                    // Best effort, same reasoning as the read: a failed write loses a future hit, it does
                    // not invalidate a tool call that already succeeded. Letting it throw here would turn
                    // a successful call into an error the model sees — and may retry.
                    try
                    {
                        await _toolCache.SetAsync(storeKey, output, ToolCacheKey.StoreTtl, request.Exchange, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex,
                            "Tool-cache write failed for '{Tool}'; the result is not cached.", use.Name);
                    }
                }
            }

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
                JsonSerializer.Serialize(new { error = ex.GetType().Name, message = ex.Message }, JsonOptions), IsError: true);
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
        // The shadow runs beside the primary run, not inside its work: it must not write into the route's
        // transaction, and a command of its own on that transaction's connection at the same time as the
        // primary run's would be refused.
        using var outside = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        try { await _shadow.RunAsync(provider, request, response, ct).ConfigureAwait(false); }
        catch { /* shadow failures must never affect the primary run */ }
    }

    /// <summary>
    /// Runs a budget call outside the ambient transaction. Tokens are paid when the provider answers, so a
    /// rollback of the route must not un-count them: a retry after the rollback would otherwise run against
    /// a budget that forgot the spend.
    /// </summary>
    private static async ValueTask<BudgetDecision> OutsideTransactionAsync(Func<ValueTask<BudgetDecision>> call)
    {
        using var outside = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        var decision = await call().ConfigureAwait(false);
        outside.Complete();
        return decision;
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
    /// Everything a tool receives that is NOT the input JSON and still changes its answer: the caller's
    /// identity and the header values the route opted into propagating. Both belong in the cache key —
    /// a tool route sees a real principal and real headers (see <see cref="ToolHeaderPolicy"/>), so
    /// "same input" does not mean "same answer", and without this a persisted entry fetched for one
    /// caller could be served to another.
    /// <para>
    /// The ambient conversation / correlation ids and the audit tags are deliberately left out: they are
    /// per-run bookkeeping, and folding them in would make a cross-run cache useless. A tool whose output
    /// depends on them must not declare <c>Persist</c>.
    /// </para>
    /// </summary>
    private static string CallerFingerprint(AgentRequest request)
    {
        var sb = new StringBuilder();

        var principal = ExchangePrincipal.Get(request.Exchange);
        string? subject = null;
        if (principal?.Identity is { IsAuthenticated: true } identity)
            subject = identity.Name ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        sb.Append("caller:").Append(subject ?? request.UserId ?? string.Empty).Append('\n');

        if (request.PropagateToolHeaders is { Count: > 0 } names)
        {
            var headers = request.Exchange.In?.Headers;
            foreach (var name in names
                         .Where(n => !string.IsNullOrWhiteSpace(n))
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("header:").Append(name).Append('=');
                if (headers is not null && headers.TryGetValue(name, out var value) && value is not null)
                    sb.Append(value);
                sb.Append('\n');
            }
        }

        return sb.ToString();
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
        string? resolvedUri,
        CancellationToken ct)
    {
        if (_producerTemplate is null)
            throw new InvalidOperationException(
                "AgentEngine has no IProducerTemplate. Register the engine via AddRedbRouteLlm() so the producer template is injected.");

        var parentExchange = request.Exchange;

        // The cache block resolves the address when it needs a key; passing it in keeps one resolution
        // per dispatch, so a descriptor that is not pure cannot index the cache by one address and call
        // another.
        var endpointUri = resolvedUri ?? descriptor.BuildEndpointUri(use.InputJson, parentExchange);
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

            // Safety is part of the tool surface the model was given: a policy change between two runs
            // is exactly the drift an auditor is looking for ("the same prompt, a weaker gate").
            sb.Append("safety:")
                .Append(cap.Safety.SideEffect).Append('/')
                .Append(cap.Safety.Caching).Append('/')
                .Append(cap.Safety.Cost).Append('/')
                .Append(cap.Safety.RequiresApproval ? '1' : '0').Append('\n');

            // Claims are length-prefixed: joining on a comma would hash ["a,b"] and ["a","b"]
            // identically, which is the one collision an audit hash must not have.
            foreach (var claim in cap.Safety.RequiredClaims.OrderBy(c => c, StringComparer.Ordinal))
                sb.Append("claim:").Append(claim.Length).Append(':').Append(claim).Append('\n');

            sb.Append("---\n");
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
