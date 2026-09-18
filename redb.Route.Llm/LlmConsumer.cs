using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Telemetry;
using redb.Route.Llm.Tools;

namespace redb.Route.Llm;

/// <summary>
/// LLM consumer. Fires the connection factory on a fixed interval and pushes
/// the agent response into the route pipeline as a normal exchange.
/// <para>
/// Supported <c>schedule</c> formats:
/// <list type="bullet">
///   <item><c>30s</c> / <c>5m</c> / <c>1h</c> — fixed interval via <see cref="PeriodicTimer"/>.</item>
///   <item>A cron expression — rejected with a redirect to <c>From("quartz://...")</c>
///         which already handles cron scheduling.</item>
/// </list>
/// </para>
/// The initial user message comes from <see cref="LlmEndpointOptions.InitialBodyRef"/>.
/// A leading <c>#</c> turns the value into a registry reference: <c>?initialBodyRef=#daily-brief</c>
/// resolves <c>daily-brief</c> first via <see cref="IPromptTemplateRegistry"/> (latest version),
/// then via <see cref="IRouteContext"/>'s named-object registry. Any other route can
/// rewrite the same key at runtime, giving the scheduled agent a fresh prompt every tick
/// without touching the route definition. Plain values are used verbatim.
/// </summary>
public sealed class LlmConsumer : IConsumer
{
    private static readonly Regex IntervalPattern =
        new(@"^\s*(\d+)\s*(ms|s|m|h)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly LlmEndpoint _endpoint;
    private readonly LlmEndpointOptions _options;
    private readonly IProcessor _processor;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    // Single-threaded scheduler loop: marks a tick whose failure the core already counted.
    private bool _tickFailedInPipeline;

    /// <summary>Creates a consumer.</summary>
    public LlmConsumer(LlmEndpoint endpoint, LlmEndpointOptions options, IProcessor processor)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    /// <inheritdoc />
    public IEndpoint? Endpoint => _endpoint;

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Schedule))
            throw new InvalidOperationException(
                "Consumer mode for 'llm://' requires '?schedule=' (e.g. '30s', '5m', '1h'). " +
                "For cron expressions use From(\"quartz://...\").To(\"llm://factory\"); " +
                "for event-driven agents use From(\"<your-transport>\").To(\"llm://factory\").");

        var interval = ParseInterval(_options.Schedule)
            ?? throw new InvalidOperationException(
                $"Schedule '{_options.Schedule}' is not a valid interval. " +
                "Accepted formats: '500ms', '30s', '5m', '1h'. " +
                "Cron expressions are not supported by the inline consumer — use From(\"quartz://...\").");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunLoopAsync(interval, _cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        if (_cts is null) return;
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch { /* idempotent */ }
        if (_loop is { } loop)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) { return; }

            try
            {
                await FireOnceAsync(ct).ConfigureAwait(false);
            }
            // Only real shutdown stops the loop. An HttpClient timeout also throws OperationCanceledException
            // (TaskCanceledException) but on a different token — that must fall through to the transient catch,
            // not silently terminate the scheduled consumer forever.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Ownership audit: a pipeline failure was already counted by the core's
                // StatisticsProcessor; the consumer records only a tick that died BEFORE the
                // exchange reached the pipeline (factory/engine/prompt resolution, the LLM call).
                if (!_tickFailedInPipeline)
                    _endpoint.RecordError();
                Activity.Current?.AddTag("llm.consumer.error", ex.GetType().Name);
                // Continue — one failed tick must not kill the consumer.
            }
        }
    }

    private Task FireOnceAsync(CancellationToken ct)
    {
        // No RecordMessageOut / RecordProcessingTime: the pipeline leg is wrapped by the core's
        // StatisticsProcessor (MessagesIn, Errors, time), and MessagesOut is a producer-side
        // counter a scheduler tick never is - self-recording doubled both (ownership audit).
        _tickFailedInPipeline = false;
        return FireOnceCoreAsync(ct);
    }

    private async Task FireOnceCoreAsync(CancellationToken ct)
    {
        var ctx = (_endpoint.Component as ComponentBase)?.Context;
        var factory = _endpoint.ResolvedFactory
            ?? ctx?.GetFromRegistry<LlmConnectionFactory>(_endpoint.ConnectionFactoryName)
            ?? throw new InvalidOperationException(
                $"LLM connection factory '{_endpoint.ConnectionFactoryName}' is not registered.");

        var services = ctx?.GetServiceProvider();
        var engine = _endpoint.ResolvedEngine
            ?? (ctx is null ? null : AgentEngine.FindRegistered(ctx))
            ?? throw new InvalidOperationException(
                "No IAgentEngine is registered. Call services.AddRedbRouteLlm() — it registers the engine with " +
                "the producer template, claims source and cache — or register one via " +
                "AgentEngine.FromContext(context).");

        var templateRegistry = services?.GetService<IPromptTemplateRegistry>()
            ?? ctx?.GetService<IPromptTemplateRegistry>();
        // The exchange comes first, with an empty body: the prompt references resolve with it, so a redb-backed
        // template registry works on this tick's scope instead of the one instance every concurrent tick shares.
        var scopeFactory = services?.GetService<IServiceScopeFactory>();
        var exchange = Exchange.Create(new Message(string.Empty), scopeFactory);

        // The scheduler owns this exchange for the whole tick: dispose it in finally so its
        // per-exchange DI scope (and any redb connection resolved downstream) is released every
        // tick — otherwise each scheduled fire would leak a scope and drain the connection pool.
        // The prompt lookups run inside, so a failing template releases the exchange as well.
        try
        {
            // Make the configured named-redb visible to every downstream store, the template registry included.
            if (!string.IsNullOrEmpty(_options.Redb))
                exchange.Properties[LlmKeys.RedbName] = _options.Redb;

            var initialBody = await PromptRef.ResolveAsync(_options.InitialBodyRef, templateRegistry, ctx, exchange, ct).ConfigureAwait(false)
                ?? string.Empty;
            var systemPrompt = await PromptRef.ResolveAsync(_options.SystemPromptRef, templateRegistry, ctx, exchange, ct).ConfigureAwait(false);
            exchange.In.Body = initialBody;

            var registry = services?.GetService<IToolDescriptorRegistry>()
                ?? ctx?.GetService<IToolDescriptorRegistry>();
            var tools = ToolFilter.Resolve(registry, _options.Tools);

            var conversationId = _options.Conversation switch
            {
                "header" => null, // no header on a scheduler-born exchange
                "property" => exchange.RouteId,
                _ => null
            };

            var request = new AgentRequest
            {
                Factory = factory,
                Exchange = exchange,
                UserContent = [new LlmTextBlock(initialBody)],
                SystemPrompt = systemPrompt,
                Tools = tools,
                ConversationId = conversationId,
                MaxIterations = _options.MaxIterations,
                Budget = AgentBudgetFactory.From(
                    _options.BudgetInputTokens, _options.BudgetOutputTokens, _options.BudgetCostUsd),
                Temperature = _options.Temperature,
                MaxTokens = _options.MaxTokens,
                PropagateToolHeaders = ToolHeaderPolicy.ParseCsv(_options.PropagateToolHeaders)
            };

            var response = await engine.RunAsync(request, ct).ConfigureAwait(false);

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = response.Text;
            exchange.Out.Headers[LlmHeaders.ProviderId] = factory.Provider;
            exchange.Out.Headers[LlmHeaders.ModelId] = factory.ModelId;
            exchange.Out.Headers[LlmHeaders.TokensIn] = response.Usage.InputTokens;
            exchange.Out.Headers[LlmHeaders.TokensOut] = response.Usage.OutputTokens;
            exchange.Out.Headers[LlmHeaders.CacheWriteTokens] = response.Usage.CacheCreationInputTokens;
            exchange.Out.Headers[LlmHeaders.CacheReadTokens] = response.Usage.CacheReadInputTokens;
            exchange.Out.Headers[LlmHeaders.ToolIterations] = response.Iterations;
            exchange.Out.Headers[LlmHeaders.StopReason] = response.StopReason.ToString();

            try
            {
                await _processor.Process(exchange, ct).ConfigureAwait(false);
            }
            catch
            {
                // The core's StatisticsProcessor counted this one; the RunLoop catch must not.
                _tickFailedInPipeline = true;
                throw;
            }
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static TimeSpan? ParseInterval(string schedule)
    {
        var m = IntervalPattern.Match(schedule);
        if (!m.Success) return null;

        if (!long.TryParse(m.Groups[1].Value, out var n) || n <= 0) return null;

        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            _ => null
        };
    }
}
