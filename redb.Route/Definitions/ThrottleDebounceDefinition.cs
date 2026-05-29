using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Throttle EIP.
/// Limits the rate at which the downstream pipeline processes exchanges.
/// Leaf methods on this definition build the <em>downstream</em> pipeline.
/// Close with <see cref="EndThrottle"/>.
/// </summary>
public class ThrottleDefinition : RouteDefinition, IRouteScope
{
    private readonly int _maxPerPeriod;
    private TimeSpan? _period;

    internal ThrottleDefinition(int maxPerPeriod)
    {
        if (maxPerPeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPerPeriod), "Must be > 0.");
        _maxPerPeriod = maxPerPeriod;
    }

    // ── Options ─────────────────────────────────────────────────────────────────

    /// <summary>Sets the time period for the rate limit (default: 1 second).</summary>
    public ThrottleDefinition Period(TimeSpan period) { _period = period; return this; }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes the Throttle scope and returns the parent route definition.</summary>
    public IRouteDefinition EndThrottle()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndThrottle() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndThrottle();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor downstream = BuildPipeline(Outputs, context);
        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<ThrottleProcessor>();
        return new ThrottleProcessor(downstream, _maxPerPeriod, _period, logger);
    }

    private static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        return outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => outputs[0].CreateProcessor(context),
            _ => BuildMulti(outputs, context)
        };
    }

    private static PipelineProcessor BuildMulti(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var o in outputs)
            pipeline.Add(o.CreateProcessor(context));
        return pipeline;
    }

    // ── Leaf DSL ───────────────────────────────────────────────────────────────

    /// <summary>Sends the throttled exchange to an endpoint.</summary>
    public ThrottleDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes with a synchronous action.</summary>
    public ThrottleDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes with an asynchronous action.</summary>
    public ThrottleDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes with a pre-built processor.</summary>
    public ThrottleDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body.</summary>
    public ThrottleDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public ThrottleDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header.</summary>
    public ThrottleDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public ThrottleDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

/// <summary>
/// Scope-opener definition for the Debounce EIP.
/// Suppresses rapid-fire exchanges per key, forwarding only the last exchange after
/// a configurable quiet period.
/// Leaf methods on this definition build the <em>downstream</em> pipeline.
/// Close with <see cref="EndDebounce"/>.
/// </summary>
public class DebounceDefinition : RouteDefinition, IRouteScope
{
    private readonly Func<IExchange, string> _keyExtractor;
    private readonly TimeSpan _quietPeriod;

    /// <summary>Gets the key extractor function used to correlate exchanges.</summary>
    public Func<IExchange, string> KeyExtractor => _keyExtractor;

    /// <summary>Gets the quiet period after which the last buffered exchange is forwarded.</summary>
    public TimeSpan QuietPeriod => _quietPeriod;

    internal DebounceDefinition(Func<IExchange, string> keyExtractor, TimeSpan quietPeriod)
    {
        _keyExtractor = keyExtractor ?? throw new ArgumentNullException(nameof(keyExtractor));
        if (quietPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietPeriod), "Must be positive.");
        _quietPeriod = quietPeriod;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes the Debounce scope and returns the parent route definition.</summary>
    public IRouteDefinition EndDebounce()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndDebounce() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndDebounce();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor downstream = BuildPipeline(Outputs, context);
        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<DebounceProcessor>();
        return new DebounceProcessor(downstream, _keyExtractor, _quietPeriod, logger);
    }

    private static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        return outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => outputs[0].CreateProcessor(context),
            _ => BuildMulti(outputs, context)
        };
    }

    private static PipelineProcessor BuildMulti(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var o in outputs)
            pipeline.Add(o.CreateProcessor(context));
        return pipeline;
    }

    // ── Leaf DSL ───────────────────────────────────────────────────────────────

    /// <summary>Sends the debounced exchange to an endpoint.</summary>
    public DebounceDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes the debounced exchange with a synchronous action.</summary>
    public DebounceDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes the debounced exchange with an asynchronous action.</summary>
    public DebounceDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes the debounced exchange with a pre-built processor.</summary>
    public DebounceDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body.</summary>
    public DebounceDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public DebounceDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header.</summary>
    public DebounceDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public DebounceDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

/// <summary>
/// Scope-opener definition for the keyed (per-key) Throttle EIP.
/// Limits the rate at which each unique key can enter the downstream pipeline.
/// Close with <see cref="EndKeyedThrottle"/>.
/// </summary>
public class KeyedThrottleDefinition : RouteDefinition, IRouteScope
{
    private readonly Func<IExchange, string> _keyExtractor;
    private readonly int _maxPerPeriod;
    private readonly TimeSpan? _period;

    /// <summary>Gets the key extractor function.</summary>
    public Func<IExchange, string> KeyExtractor => _keyExtractor;

    /// <summary>Gets the max exchanges per period per key.</summary>
    public int MaxPerPeriod => _maxPerPeriod;

    /// <summary>Gets the time period for the rate limit (null = 1 second default).</summary>
    public TimeSpan? Period => _period;

    internal KeyedThrottleDefinition(Func<IExchange, string> keyExtractor, int maxPerPeriod, TimeSpan? period)
    {
        _keyExtractor = keyExtractor ?? throw new ArgumentNullException(nameof(keyExtractor));
        if (maxPerPeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPerPeriod), "Must be > 0.");
        _maxPerPeriod = maxPerPeriod;
        _period = period;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes the KeyedThrottle scope and returns the parent route definition.</summary>
    public IRouteDefinition EndKeyedThrottle()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndKeyedThrottle() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndKeyedThrottle();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor downstream = BuildPipeline(Outputs, context);
        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<KeyedThrottleProcessor>();
        return new KeyedThrottleProcessor(downstream, _keyExtractor, _maxPerPeriod, _period, logger);
    }

    private static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        return outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => outputs[0].CreateProcessor(context),
            _ => BuildMulti(outputs, context)
        };
    }

    private static PipelineProcessor BuildMulti(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var o in outputs)
            pipeline.Add(o.CreateProcessor(context));
        return pipeline;
    }

    // ── Leaf DSL ───────────────────────────────────────────────────────────────

    /// <summary>Sends the throttled exchange to an endpoint.</summary>
    public KeyedThrottleDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes with a synchronous action.</summary>
    public KeyedThrottleDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes with an asynchronous action.</summary>
    public KeyedThrottleDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes with a pre-built processor.</summary>
    public KeyedThrottleDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body.</summary>
    public KeyedThrottleDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public KeyedThrottleDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header.</summary>
    public KeyedThrottleDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public KeyedThrottleDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}
