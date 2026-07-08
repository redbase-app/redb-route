using System.Diagnostics;
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

        // Endpoint-level statistics consumed by tsak / tsak.web dashboard.
        // Counted once per agent turn (one inbound exchange == one user message).
        _endpoint.RecordMessageIn();
        var endpointSw = Stopwatch.StartNew();

        try
        {
            await ProcessCoreAsync(exchange, ct).ConfigureAwait(false);
            _endpoint.RecordMessageOut();
        }
        catch (Exception ex)
        {
            _endpoint.RecordError(ex);
            throw;
        }
        finally
        {
            endpointSw.Stop();
            _endpoint.RecordProcessingTime(endpointSw.Elapsed);
        }
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
                "No IAgentEngine registered. Call services.AddRedbRouteLlm() or context.AddService<IAgentEngine>(new AgentEngine()).");

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
        var conversationId = ResolveConversationId(exchange);
        var tools = ResolveTools(exchange);
        var userId = ResolveUserId(exchange);
        var auditTags = ResolveAuditTags(exchange);

        // Endpoint-level statistics (consumed by tsak / tsak.web dashboard).
        // MessagesOut + Errors are tracked by ToProcessor; here we add bytes and timing.
        var bytesIn = userContent.OfType<LlmTextBlock>().Sum(b => b.Text?.Length ?? 0);
        if (bytesIn > 0) _endpoint.RecordBytesIn(bytesIn);

        var agentRequest = new AgentRequest
        {
            Factory = factory,
            Exchange = exchange,
            UserContent = userContent,
            SystemPrompt = systemPrompt,
            Tools = tools,
            ConversationId = conversationId,
            MaxIterations = _options.MaxIterations,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
            PromptTemplateName = _options.PromptTemplateName,
            PromptTemplateVersion = _options.PromptTemplateVersion,
            UserId = userId,
            AuditTags = auditTags
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
        return ctx?.GetService<IAgentEngine>();
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

        var bytesIn = userContent.OfType<LlmTextBlock>().Sum(b => b.Text?.Length ?? 0);
        if (bytesIn > 0) _endpoint.RecordBytesIn(bytesIn);

        var llmRequest = new LlmRequest
        {
            ModelId = factory.ModelId,
            SystemPrompt = systemPrompt,
            Messages = [new LlmMessage { Role = "user", Content = userContent }],
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

        await foreach (var chunk in provider.StreamAsync(llmRequest, ct).ConfigureAwait(false))
        {
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
        exchange.Out.Headers[LlmHeaders.ToolIterations] = 1;
        exchange.Out.Headers[LlmHeaders.StopReason] = stopReason.ToString();

        // bytesOut is captured as text length for endpoint accounting.
        // The IEndpointStatistics surface only exposes RecordBytesIn — outbound
        // bytes accounting belongs to the route processor downstream.
        _ = bytesOut;

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
