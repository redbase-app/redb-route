using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Telemetry;
using redb.Route.Llm.Tools;
using redb.Route.Telemetry;

namespace redb.Route.Llm;

/// <summary>
/// LLM producer. Treats the inbound exchange as a single user turn, runs the
/// agent engine to completion, and writes the assistant text into <c>exchange.Out.Body</c>.
/// </summary>
public sealed class LlmProducer : ConnectableProducer
{
    private readonly LlmEndpoint _endpoint;
    private readonly LlmEndpointOptions _options;

    /// <summary>Creates a producer.</summary>
    public LlmProducer(LlmEndpoint endpoint, LlmEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"llm:{_endpoint.ConnectionFactoryName}";

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        // MessagesIn stays with the connector: it means "a user turn arrived at this llm
        // endpoint", which no core wrapper counts for a producer. MessagesOut/Errors/Time are
        // the core's (ToProcessor for a routed .To(), the ProducerTemplate for template sends) -
        // recording them here as well double-counted every turn (the ownership audit).
        _endpoint.RecordMessageIn();

        await ProcessCoreAsync(exchange, ct).ConfigureAwait(false);
    }

    private async Task ProcessCoreAsync(IExchange exchange, CancellationToken ct)
    {
        // Make the configured named-redb visible to every downstream store. Read
        // by RedbConversationStore / RedbApprovalStore / ... via
        // context.GetRedbService(name, exchange), which already handles
        // per-exchange scoping and disposal.
        if (!string.IsNullOrEmpty(_options.Redb))
            exchange.Properties[LlmKeys.RedbName] = _options.Redb;

        var factory = ResolveFactory(exchange)
            ?? throw new InvalidOperationException(
                $"LLM connection factory '{_endpoint.ConnectionFactoryName}' " +
                $"is not registered in the route context.");

        // Stream mode bypasses the agent engine (no tool-loop, no governance) and
        // writes an IAsyncEnumerable<string> of token deltas into Out.Body so HTTP
        // / SSE consumers can forward chunks as they arrive. Tool-using agents
        // must remain on the non-streaming path until the engine grows a
        // streaming surface.
        if (_options.Stream)
        {
            await ProcessStreamingAsync(exchange, factory, ct).ConfigureAwait(false);
            return;
        }

        var engine = ResolveEngine(exchange)
            ?? throw new InvalidOperationException(
                "No IAgentEngine is registered. Call services.AddRedbRouteLlm() — it registers the engine " +
                "together with the producer template, claims source and cache the tool loop needs. A host that " +
                "registers one itself should use context.AddService(typeof(IAgentEngine), " +
                "AgentEngine.FromContext(context)), which wires the same seams: a bare new AgentEngine() " +
                "answers no claims and cannot dispatch a tool.");

        using var activity = RouteActivitySource.Source.StartActivity(
            $"llm {factory.Provider}:{factory.ModelId}", ActivityKind.Client);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("llm.provider", factory.Provider);
            activity.SetTag("llm.model.id", factory.ModelId);
            activity.SetTag("messaging.system", "llm");
            activity.SetTag("messaging.operation", "complete");
        }

        var userContent = BuildUserContent(exchange);
        var systemPrompt = await ResolveSystemPromptAsync(exchange, ct).ConfigureAwait(false);
        var preamble = ResolvePreamble(exchange);
        var conversationId = ResolveConversationId(exchange);
        var tools = ResolveTools(exchange);
        var userId = ResolveUserId(exchange);
        var auditTags = ResolveAuditTags(exchange);

        // Endpoint-level statistics (consumed by tsak / tsak.web dashboard).
        // MessagesOut + Errors are tracked by ToProcessor; here we add bytes and timing.
        // The prompt LEAVES the endpoint. It used to be recorded as BytesIn because until Ф14
        // RecordBytesOut did not exist. Counted in UTF-8 bytes, not UTF-16 chars.
        var promptBytes = userContent.OfType<LlmTextBlock>()
            .Sum(b => b.Text is null ? 0 : Encoding.UTF8.GetByteCount(b.Text));
        if (promptBytes > 0) _endpoint.RecordBytesOut(promptBytes);

        var agentRequest = new AgentRequest
        {
            Factory = factory,
            Exchange = exchange,
            UserContent = userContent,
            SystemPrompt = systemPrompt,
            CacheSystemPrompt = _options.CacheSystemPrompt,
            Preamble = preamble,
            Tools = tools,
            ConversationId = conversationId,
            MaxIterations = _options.MaxIterations,
            Budget = AgentBudgetFactory.From(
                _options.BudgetInputTokens, _options.BudgetOutputTokens, _options.BudgetCostUsd),
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
            PromptTemplateName = _options.PromptTemplateName,
            PromptTemplateVersion = _options.PromptTemplateVersion,
            UserId = userId,
            AuditTags = auditTags,
            PropagateToolHeaders = ToolHeaderPolicy.ParseCsv(_options.PropagateToolHeaders)
        };

        var sw = Stopwatch.StartNew();
        var response = await engine.RunAsync(agentRequest, ct).ConfigureAwait(false);
        sw.Stop();

        WriteResponse(exchange, response, factory);

        var providerTag = new KeyValuePair<string, object?>("llm.provider", factory.Provider);
        var modelTag = new KeyValuePair<string, object?>("llm.model.id", factory.ModelId);
        var factoryTag = new KeyValuePair<string, object?>("llm.factory", factory.Name);
        var stopTag = new KeyValuePair<string, object?>("llm.stop_reason", response.StopReason.ToString());

        LlmMetrics.AgentRuns.Add(1, providerTag, modelTag, factoryTag, stopTag);
        LlmMetrics.AgentIterations.Record(response.Iterations, providerTag, modelTag, factoryTag);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("llm.tokens.in", response.Usage.InputTokens);
            activity.SetTag("llm.tokens.out", response.Usage.OutputTokens);
            activity.SetTag("llm.tool.iterations", response.Iterations);
            activity.SetTag("llm.stop_reason", response.StopReason.ToString());
            activity.SetTag("llm.duration.ms", sw.Elapsed.TotalMilliseconds);
        }
    }

    private LlmConnectionFactory? ResolveFactory(IExchange exchange)
    {
        if (_endpoint.ResolvedFactory is not null) return _endpoint.ResolvedFactory;

        // Late resolution — useful when the factory is registered after the endpoint is built.
        var ctx = (_endpoint.Component as ComponentBase)?.Context;
        return ctx?.GetFromRegistry<LlmConnectionFactory>(_endpoint.ConnectionFactoryName);
    }

    private IAgentEngine? ResolveEngine(IExchange exchange)
    {
        if (_endpoint.ResolvedEngine is not null) return _endpoint.ResolvedEngine;
        var ctx = (_endpoint.Component as ComponentBase)?.Context;
        return ctx is null ? null : AgentEngine.FindRegistered(ctx);
    }

    private static IReadOnlyList<LlmContentBlock> BuildUserContent(IExchange exchange)
    {
        var body = exchange.In.Body;
        var text = body switch
        {
            null => string.Empty,
            string s => s,
            _ => body.ToString() ?? string.Empty
        };
        return [new LlmTextBlock(text)];
    }

    private async ValueTask<string?> ResolveSystemPromptAsync(IExchange exchange, CancellationToken ct)
    {
        // Header wins, then endpoint option. Endpoint option supports the framework-wide
        // "#name" registry-ref convention: ?systemPromptRef=#watchdog resolves to the latest
        // PromptTemplate named "watchdog", falling back to a string in the route-context registry.
        if (exchange.In.Headers.TryGetValue(LlmHeaders.SystemPrompt, out var hdr) && hdr is string s && s.Length > 0)
            return s;

        var ctx = (_endpoint.Component as ComponentBase)?.Context;
        var sp = ctx?.GetServiceProvider();
        var templates = sp?.GetService<IPromptTemplateRegistry>()
            ?? ctx?.GetService<IPromptTemplateRegistry>();
        return await PromptRef.ResolveAsync(_options.SystemPromptRef, templates, ctx, exchange, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fixed opening messages from the <see cref="LlmHeaders.Preamble"/> header. Header only —
    /// there is no endpoint option: a preamble is assembled per call by whoever assembles the
    /// system prompt (it is the same kind of thing), and a URI has nowhere to carry a list of
    /// typed messages. Anything but a sequence of <see cref="LlmMessage"/> is treated as absent.
    /// </summary>
    private static IReadOnlyList<LlmMessage> ResolvePreamble(IExchange exchange)
        => exchange.In.Headers.TryGetValue(LlmHeaders.Preamble, out var hdr)
           && hdr is IEnumerable<LlmMessage> messages
            ? [.. messages]
            : [];

    private string? ResolveConversationId(IExchange exchange) => _options.Conversation switch
    {
        "header" => exchange.In.GetHeader<string>(LlmHeaders.ConversationId),
        "property" => exchange.RouteId,
        _ => null
    };

    private IReadOnlyList<ILlmToolDescriptor> ResolveTools(IExchange exchange)
    {
        var ctx = (_endpoint.Component as ComponentBase)?.Context;
        var registry = ctx?.GetService<IToolDescriptorRegistry>();
        return ToolFilter.Resolve(registry, _options.Tools);
    }

    /// <summary>
    /// Resolves the principal id stamped on every persisted row of this run.
    /// Order: <c>?user=</c> option (literal or <c>${header.X}</c> expression),
    /// then the <c>llm.user.id</c> header. Returns null when neither is set —
    /// callers downstream record the row with a null UserId rather than fail.
    /// </summary>
    private string? ResolveUserId(IExchange exchange)
    {
        var resolved = ResolveExpression(_options.User, exchange);
        if (!string.IsNullOrEmpty(resolved)) return resolved;

        return exchange.In.GetHeader<string>(LlmHeaders.UserId);
    }

    /// <summary>
    /// Resolves audit tags from two sources: the <c>?audit=</c> CSV option and
    /// any inbound header named <c>llm.audit.&lt;name&gt;</c>. Headers merge on
    /// top of option values (header wins on collision). Returns null when no
    /// tags are configured so the persistence layer can skip the column.
    /// </summary>
    private IReadOnlyDictionary<string, string>? ResolveAuditTags(IExchange exchange)
    {
        Dictionary<string, string>? tags = null;

        // 1. Builder-side CSV (key=value,key=${header.X}).
        if (!string.IsNullOrEmpty(_options.Audit))
        {
            foreach (var pair in _options.Audit.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = System.Web.HttpUtility.UrlDecode(pair[..eq]);
                var rawValue = System.Web.HttpUtility.UrlDecode(pair[(eq + 1)..]);
                var resolved = ResolveExpression(rawValue, exchange) ?? string.Empty;
                if (string.IsNullOrEmpty(key)) continue;
                tags ??= new Dictionary<string, string>(StringComparer.Ordinal);
                tags[key] = resolved;
            }
        }

        // 2. Header-driven tags (llm.audit.<name>) — header wins on collision.
        foreach (var kv in exchange.In.Headers)
        {
            if (kv.Key.Length <= LlmHeaders.AuditTagPrefix.Length) continue;
            if (!kv.Key.StartsWith(LlmHeaders.AuditTagPrefix, StringComparison.Ordinal)) continue;

            var key = kv.Key[LlmHeaders.AuditTagPrefix.Length..];
            if (string.IsNullOrEmpty(key)) continue;

            var value = kv.Value switch
            {
                null => string.Empty,
                string s => s,
                _ => kv.Value.ToString() ?? string.Empty
            };
            tags ??= new Dictionary<string, string>(StringComparer.Ordinal);
            tags[key] = value;
        }

        return tags;
    }

    /// <summary>
    /// Lightweight resolver for <c>${header.NAME}</c> / <c>${property.NAME}</c>
    /// expressions. Anything else is returned as a literal so callers can use
    /// fixed values (<c>"system"</c>) just as easily as references.
    /// </summary>
    private static string? ResolveExpression(string? expression, IExchange exchange)
    {
        if (string.IsNullOrEmpty(expression)) return expression;
        if (!expression.StartsWith("${", StringComparison.Ordinal) ||
            !expression.EndsWith("}", StringComparison.Ordinal))
            return expression;

        var inner = expression[2..^1];
        if (inner.StartsWith("header.", StringComparison.Ordinal))
        {
            var name = inner[7..];
            return exchange.In.Headers.TryGetValue(name, out var v) && v is not null
                ? v.ToString()
                : null;
        }
        if (inner.StartsWith("property.", StringComparison.Ordinal))
        {
            var name = inner[9..];
            return exchange.Properties.TryGetValue(name, out var v) && v is not null
                ? v.ToString()
                : null;
        }
        // Unknown prefix — keep the original expression so producers / observers
        // can detect the misconfiguration instead of silently dropping it.
        return expression;
    }

    private static void WriteResponse(IExchange exchange, AgentResponse response, LlmConnectionFactory factory)
    {
        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = response.Text;
        exchange.Out.Headers[LlmHeaders.ProviderId] = factory.Provider;
        exchange.Out.Headers[LlmHeaders.ModelId] = factory.ModelId;
        exchange.Out.Headers[LlmHeaders.TokensIn] = response.Usage.InputTokens;
        exchange.Out.Headers[LlmHeaders.TokensOut] = response.Usage.OutputTokens;
        // Written unconditionally, zeros included: a caller that reads "cache read = 0" learns
        // something (the cache is not being hit), while a missing header is indistinguishable
        // from an older engine that never reported it.
        exchange.Out.Headers[LlmHeaders.CacheWriteTokens] = response.Usage.CacheCreationInputTokens;
        exchange.Out.Headers[LlmHeaders.CacheReadTokens] = response.Usage.CacheReadInputTokens;
        exchange.Out.Headers[LlmHeaders.ToolIterations] = response.Iterations;
        exchange.Out.Headers[LlmHeaders.StopReason] = response.StopReason.ToString();
    }

    private async Task ProcessStreamingAsync(IExchange exchange, LlmConnectionFactory factory, CancellationToken ct)
    {
        using var activity = RouteActivitySource.Source.StartActivity(
            $"llm {factory.Provider}:{factory.ModelId} stream", ActivityKind.Client);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("llm.provider", factory.Provider);
            activity.SetTag("llm.model.id", factory.ModelId);
            activity.SetTag("llm.streaming", true);
            activity.SetTag("messaging.system", "llm");
            activity.SetTag("messaging.operation", "stream");
        }

        var userContent = BuildUserContent(exchange);
        var systemPrompt = await ResolveSystemPromptAsync(exchange, ct).ConfigureAwait(false);
        var preamble = ResolvePreamble(exchange);

        var promptBytes = userContent.OfType<LlmTextBlock>()
            .Sum(b => b.Text is null ? 0 : Encoding.UTF8.GetByteCount(b.Text));
        if (promptBytes > 0) _endpoint.RecordBytesOut(promptBytes);

        var llmRequest = new LlmRequest
        {
            ModelId = factory.ModelId,
            SystemPrompt = systemPrompt,
            CacheSystemPrompt = _options.CacheSystemPrompt,
            // The preamble opens the transcript here too: stream mode has no history, but the
            // fixed opening exchange is part of the assistant, not of the history.
            Messages = [.. preamble, new LlmMessage { Role = "user", Content = userContent }],
            // Tools are intentionally not passed in stream mode — the producer
            // does not run a tool-loop here. Use the non-streaming path with
            // ?tools= when tool dispatch is required.
            Tools = [],
            Temperature = _options.Temperature ?? factory.Temperature,
            MaxTokens = _options.MaxTokens ?? factory.MaxTokens,
            TopP = factory.TopP
        };

        var provider = factory.Build();
        var stream = StreamTextDeltasAsync(provider, llmRequest, factory, exchange, activity, ct);

        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = stream;
        exchange.Out.ContentType ??= "text/event-stream";
        exchange.Out.Headers[LlmHeaders.Streaming] = true;
        exchange.Out.Headers[LlmHeaders.ProviderId] = factory.Provider;
        exchange.Out.Headers[LlmHeaders.ModelId] = factory.ModelId;
    }

    private async IAsyncEnumerable<string> StreamTextDeltasAsync(
        ILlmProvider provider,
        LlmRequest llmRequest,
        LlmConnectionFactory factory,
        IExchange exchange,
        Activity? activity,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var providerTag = new KeyValuePair<string, object?>("llm.provider", factory.Provider);
        var modelTag = new KeyValuePair<string, object?>("llm.model.id", factory.ModelId);
        var factoryTag = new KeyValuePair<string, object?>("llm.factory", factory.Name);

        LlmUsage? finalUsage = null;
        LlmStopReason? finalStop = null;
        var bytesOut = 0;
        var sw = Stopwatch.StartNew();
        LlmMetrics.Calls.Add(1, providerTag, modelTag, factoryTag);

        // Manual enumeration so a mid-stream failure can be recorded (the stream is drained by the consumer,
        // outside Producer.Process, so an exception there would otherwise vanish — no endpoint error, no metric).
        await using var enumerator = provider.StreamAsync(llmRequest, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _endpoint.RecordError(ex);
                LlmMetrics.AgentRuns.Add(1, providerTag, modelTag, factoryTag,
                    new KeyValuePair<string, object?>("llm.stop_reason", "error"));
                throw;
            }

            var chunk = enumerator.Current;
            foreach (var block in chunk.Content)
            {
                if (block is LlmTextBlock { Text: { Length: > 0 } text })
                {
                    bytesOut += text.Length;
                    yield return text;
                }
            }
            if (chunk.StopReason is not null) finalStop = chunk.StopReason;
            if (chunk.Usage is not null) finalUsage = chunk.Usage;
        }

        sw.Stop();
        var usage = finalUsage ?? LlmUsage.Empty;
        var stopReason = finalStop ?? LlmStopReason.EndTurn;

        // Late-bind summary headers — readable after the consumer drained the stream.
        exchange.Out!.Headers[LlmHeaders.TokensIn] = usage.InputTokens;
        exchange.Out.Headers[LlmHeaders.TokensOut] = usage.OutputTokens;
        exchange.Out.Headers[LlmHeaders.CacheWriteTokens] = usage.CacheCreationInputTokens;
        exchange.Out.Headers[LlmHeaders.CacheReadTokens] = usage.CacheReadInputTokens;
        exchange.Out.Headers[LlmHeaders.ToolIterations] = 1;
        exchange.Out.Headers[LlmHeaders.StopReason] = stopReason.ToString();

        // The response ARRIVES at the endpoint. This value used to be computed and then thrown
        // away (`_ = bytesOut;`) because the statistics surface had no outgoing counter and the
        // incoming one was already taken by the prompt; both halves have their own place now.
        if (bytesOut > 0) _endpoint.RecordBytesIn(bytesOut);

        var stopTag = new KeyValuePair<string, object?>("llm.stop_reason", stopReason.ToString());
        LlmMetrics.AgentRuns.Add(1, providerTag, modelTag, factoryTag, stopTag);
        LlmMetrics.AgentIterations.Record(1, providerTag, modelTag, factoryTag);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("llm.tokens.in", usage.InputTokens);
            activity.SetTag("llm.tokens.out", usage.OutputTokens);
            activity.SetTag("llm.stop_reason", stopReason.ToString());
            activity.SetTag("llm.duration.ms", sw.Elapsed.TotalMilliseconds);
        }
    }
}
