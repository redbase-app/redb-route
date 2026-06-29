using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Throttle EIP.
/// Limits the rate at which the downstream pipeline processes exchanges.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// child steps build the <em>downstream</em> pipeline.
/// Close with <see cref="EndThrottle"/>.
/// </summary>
public class ThrottleDefinition : RouteDefinitionBase<ThrottleDefinition>, IRouteScope
{
    private readonly int _maxPerPeriod;
    private TimeSpan? _period;
    private bool _rejectOnOverflow;

    internal ThrottleDefinition(int maxPerPeriod)
    {
        if (maxPerPeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPerPeriod), "Must be > 0.");
        _maxPerPeriod = maxPerPeriod;
    }

    // ── Options ─────────────────────────────────────────────────────────────────

    /// <summary>Sets the time period for the rate limit (default: 1 second).</summary>
    public ThrottleDefinition Period(TimeSpan period) { _period = period; return this; }

    /// <summary>
    /// Opt into RFC 6585 §4 rejection semantics: overflow exchanges are short-circuited with
    /// HTTP 429 + <c>Retry-After</c> (RFC 7231 §7.1.3) instead of semaphore-waiting until a
    /// slot frees. Recommended for any HTTP-facing endpoint — silent waits look like a hung
    /// server to clients and break liveness probes. Default is <c>false</c> (legacy wait).
    /// </summary>
    public ThrottleDefinition RejectOnOverflow(bool reject = true)
    {
        _rejectOnOverflow = reject;
        return this;
    }

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
        return new ThrottleProcessor(downstream, _maxPerPeriod, _period, _rejectOnOverflow, logger);
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
}

/// <summary>
/// Scope-opener definition for the Debounce EIP.
/// Suppresses rapid-fire exchanges per key, forwarding only the last exchange after
/// a configurable quiet period.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// child steps build the <em>downstream</em> pipeline.
/// Close with <see cref="EndDebounce"/>.
/// </summary>
public class DebounceDefinition : RouteDefinitionBase<DebounceDefinition>, IRouteScope
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
}

/// <summary>
/// Scope-opener definition for the keyed (per-key) Throttle EIP.
/// Limits the rate at which each unique key can enter the downstream pipeline.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>.
/// Close with <see cref="EndKeyedThrottle"/>.
/// </summary>
public class KeyedThrottleDefinition : RouteDefinitionBase<KeyedThrottleDefinition>, IRouteScope
{
    private readonly Func<IExchange, string> _keyExtractor;
    private readonly int _maxPerPeriod;
    private readonly TimeSpan? _period;
    private bool _rejectOnOverflow;

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

    // ── Options ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opt into RFC 6585 §4 rejection semantics: overflow exchanges are short-circuited with
    /// HTTP 429 + <c>Retry-After</c> (RFC 7231 §7.1.3) instead of semaphore-waiting until a
    /// slot frees. Recommended for any HTTP-facing endpoint — silent waits look like a hung
    /// server to clients and break liveness probes. Default is <c>false</c> (legacy wait).
    /// </summary>
    public KeyedThrottleDefinition RejectOnOverflow(bool reject = true)
    {
        _rejectOnOverflow = reject;
        return this;
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
        return new KeyedThrottleProcessor(downstream, _keyExtractor, _maxPerPeriod, _period, _rejectOnOverflow, logger);
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
}
