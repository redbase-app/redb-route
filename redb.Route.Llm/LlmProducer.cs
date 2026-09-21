using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Telemetry;
using redb.Route.Llm.Tools;
using redb.Route.Telemetry;
using redb.Route.Transactions;

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

        var engine = ResolveEngine(exchange)
            ?? throw new InvalidOperationException(
                "No IAgentEngine is registered. Call services.AddRedbRouteLlm() — it registers the engine " +
                "together with the producer template, claims source and cache the tool loop needs. A host that " +
                "registers one itself should use context.AddService(typeof(IAgentEngine), " +
                "AgentEngine.FromContext(context)), which wires the same seams: a bare new AgentEngine() " +
                "answers no claims and cannot dispatch a tool.");

        // stream=body runs the agent when the body is read, after the route. A .Transacted() block around this step
        // would commit before the answer exists, and the run's tools would not join it: the run is a unit of work of
        // its own, like any exchange that outlives its block. Refused here rather than leaving tools silently outside
        // the transaction the route declared, as a transacted send outside a block is refused.
        if (_options.Stream == LlmStreamMode.Body
            && (TransactedActions.IsActive(exchange) || System.Transactions.Transaction.Current is not null))
            throw new InvalidOperationException(
                "'stream=body' runs the agent after the route, when the body is read, so inside .Transacted() its tools "
                + "would not join the transaction and the block would commit before the answer exists. Use "
                + "'stream=calls' inside a transaction: it streams every model call inside the route. Or move this "
                + "step out of the .Transacted() block.");

        // stream=body starts its span when the body is read, where the run is; the other modes run here.
        using var activity = _options.Stream == LlmStreamMode.Body ? null : StartActivity(factory, "complete");

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

        AgentRequest Request(Func<AgentDeltaContext, CancellationToken, Task>? onDelta) => new()
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
            PropagateToolHeaders = ToolHeaderPolicy.ParseCsv(_options.PropagateToolHeaders),
            StreamModelCalls = _options.Stream != LlmStreamMode.Off,
            OnDelta = onDelta
        };

        if (_options.Stream == LlmStreamMode.Body)
        {
            StreamIntoBody(exchange, factory, engine, Request);
            return;
        }

        var sw = Stopwatch.StartNew();
        var response = await engine.RunAsync(Request(null), ct).ConfigureAwait(false);
        sw.Stop();

        WriteResponse(exchange, response, factory);
        RecordRun(factory, response, sw.Elapsed, activity);
    }

    /// <summary>
    /// <c>stream=body</c>: <c>Out.Body</c> becomes a <see cref="LazyAgentBody"/>, and the agent run happens when the body
    /// is read. The summary headers are written after the run, as late-bound headers; the body is released with the
    /// exchange.
    /// </summary>
    private void StreamIntoBody(
        IExchange exchange, LlmConnectionFactory factory, IAgentEngine engine,
        Func<Func<AgentDeltaContext, CancellationToken, Task>?, AgentRequest> request)
    {
        var body = new LazyAgentBody(async (onDelta, ct) =>
        {
            using var activity = StartActivity(factory, "stream");
            var textPieces = 0;
            var bytesIn = 0;
            var sw = Stopwatch.StartNew();

            AgentResponse response;
            try
            {
                response = await engine.RunAsync(request((delta, token) =>
                {
                    if (delta.Kind == AgentDeltaKind.Text)
                    {
                        textPieces++;
                        bytesIn += Encoding.UTF8.GetByteCount(delta.Text);
                    }
                    return onDelta(delta, token);
                }), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The body is read outside Producer.Process, so nothing else counts this failure for the endpoint.
                _endpoint.RecordError(ex);
                LlmMetrics.AgentRuns.Add(1, ProviderTag(factory), ModelTag(factory), FactoryTag(factory),
                    new KeyValuePair<string, object?>("llm.stop_reason", "error"));
                throw;
            }

            // An engine that ignores AgentRequest.OnDelta would leave the body empty and report success.
            if (textPieces == 0 && !string.IsNullOrEmpty(response.Text))
                throw new InvalidOperationException(
                    $"The registered IAgentEngine ({engine.GetType().Name}) answered without streaming a piece of its text: "
                    + "stream=body needs an engine that calls AgentRequest.OnDelta, as AgentEngine does.");

            WriteSummaryHeaders(exchange.Out!, response, factory);
            if (bytesIn > 0) _endpoint.RecordBytesIn(bytesIn);
            RecordRun(factory, response, sw.Elapsed, activity);
        });

        ExchangeResources.ReleaseWithExchange(exchange, body.Release);

        exchange.Out ??= exchange.In.Clone();
        exchange.Out.Body = body;
        // A stream of events whatever type the request carried: Out starts as a copy of In, and a request's
        // text/plain would make the HTTP consumer drop the SSE framing and the summary event.
        exchange.Out.ContentType = "text/event-stream";
        exchange.Out.Headers[LlmHeaders.Streaming] = true;
        exchange.Out.Headers[LlmHeaders.ProviderId] = factory.Provider;
        exchange.Out.Headers[LlmHeaders.ModelId] = factory.ModelId;
    }

    private static Activity? StartActivity(LlmConnectionFactory factory, string operation)
    {
        var activity = RouteActivitySource.Source.StartActivity(
            operation == "stream" ? $"llm {factory.Provider}:{factory.ModelId} stream" : $"llm {factory.Provider}:{factory.ModelId}",
            ActivityKind.Client);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("llm.provider", factory.Provider);
            activity.SetTag("llm.model.id", factory.ModelId);
            activity.SetTag("messaging.system", "llm");
            activity.SetTag("messaging.operation", operation);
            if (operation == "stream") activity.SetTag("llm.streaming", true);
        }

        return activity;
    }

    private static KeyValuePair<string, object?> ProviderTag(LlmConnectionFactory factory) => new("llm.provider", factory.Provider);
    private static KeyValuePair<string, object?> ModelTag(LlmConnectionFactory factory) => new("llm.model.id", factory.ModelId);
    private static KeyValuePair<string, object?> FactoryTag(LlmConnectionFactory factory) => new("llm.factory", factory.Name);

    private static void RecordRun(LlmConnectionFactory factory, AgentResponse response, TimeSpan elapsed, Activity? activity)
    {
        var stopTag = new KeyValuePair<string, object?>("llm.stop_reason", response.StopReason.ToString());
        LlmMetrics.AgentRuns.Add(1, ProviderTag(factory), ModelTag(factory), FactoryTag(factory), stopTag);
        LlmMetrics.AgentIterations.Record(response.Iterations, ProviderTag(factory), ModelTag(factory), FactoryTag(factory));

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("llm.tokens.in", response.Usage.InputTokens);
            activity.SetTag("llm.tokens.out", response.Usage.OutputTokens);
            activity.SetTag("llm.tool.iterations", response.Iterations);
            activity.SetTag("llm.stop_reason", response.StopReason.ToString());
            activity.SetTag("llm.duration.ms", elapsed.TotalMilliseconds);
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
        WriteSummaryHeaders(exchange.Out, response, factory);
    }

    /// <summary>The summary of a run: on the reply of an in-route run, late-bound on a <c>stream=body</c> reply.</summary>
    private static void WriteSummaryHeaders(IMessage message, AgentResponse response, LlmConnectionFactory factory)
    {
        message.Headers[LlmHeaders.ProviderId] = factory.Provider;
        message.Headers[LlmHeaders.ModelId] = factory.ModelId;
        message.Headers[LlmHeaders.TokensIn] = response.Usage.InputTokens;
        message.Headers[LlmHeaders.TokensOut] = response.Usage.OutputTokens;
        // Written unconditionally, zeros included: a caller that reads "cache read = 0" learns
        // something (the cache is not being hit), while a missing header is indistinguishable
        // from an older engine that never reported it.
        message.Headers[LlmHeaders.CacheWriteTokens] = response.Usage.CacheCreationInputTokens;
        message.Headers[LlmHeaders.CacheReadTokens] = response.Usage.CacheReadInputTokens;
        message.Headers[LlmHeaders.ToolIterations] = response.Iterations;
        message.Headers[LlmHeaders.StopReason] = response.StopReason.ToString();
    }
}
