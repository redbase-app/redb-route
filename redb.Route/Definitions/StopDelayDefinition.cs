using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that stops exchange processing (sets <c>exchange.Stop()</c>).
/// </summary>
public sealed class StopDefinition : ProcessorDefinition
{
    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange => exchange.Stop());
}

/// <summary>
/// Leaf definition that delays processing by a fixed <see cref="TimeSpan"/>.
/// </summary>
public sealed class DelayDefinition : ProcessorDefinition
{
    private readonly TimeSpan _duration;

    /// <summary>Creates a delay definition with a fixed duration.</summary>
    public DelayDefinition(TimeSpan duration) { _duration = duration; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelayProcessor(_duration);
}

/// <summary>
/// Leaf definition that delays processing by a duration computed from the exchange.
/// </summary>
public sealed class DelayFactoryDefinition : ProcessorDefinition
{
    private readonly Func<IExchange, TimeSpan> _durationFactory;

    /// <summary>Creates a delay definition using a factory.</summary>
    public DelayFactoryDefinition(Func<IExchange, TimeSpan> durationFactory)
    {
        ArgumentNullException.ThrowIfNull(durationFactory);
        _durationFactory = durationFactory;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(async (exchange, ct) =>
        {
            var delay = _durationFactory(exchange);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        });
}

/// <summary>
/// Leaf definition that delays processing by a duration resolved from a string expression template.
/// </summary>
public sealed class DelayExpressionDefinition : ProcessorDefinition
{
    private readonly string _expression;

    /// <summary>Creates a delay definition from a string expression template.</summary>
    public DelayExpressionDefinition(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        _expression = expression;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(async (exchange, ct) =>
        {
            var raw = Expressions.ExpressionResolver.ProcessTemplate(_expression, exchange);
            var delay = ConvertToTimeSpan(raw);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        });

    private static TimeSpan ConvertToTimeSpan(object? value) => value switch
    {
        TimeSpan ts => ts,
        int ms => TimeSpan.FromMilliseconds(ms),
        long ms => TimeSpan.FromMilliseconds(ms),
        double ms => TimeSpan.FromMilliseconds(ms),
        string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var ms) => TimeSpan.FromMilliseconds(ms),
        string s when TimeSpan.TryParse(s, out var ts) => ts,
        _ => throw new InvalidOperationException($"Cannot convert '{value}' to TimeSpan for delay.")
    };
}

/// <summary>
/// Leaf definition that samples messages by passing every N-th message (count-based).
/// </summary>
public sealed class SampleCountDefinition : ProcessorDefinition
{
    private readonly long _messageFrequency;

    /// <summary>Gets the message frequency — every N-th message passes.</summary>
    public long MessageFrequency => _messageFrequency;

    /// <summary>Creates a sampling definition that allows every Nth message through.</summary>
    public SampleCountDefinition(long messageFrequency)
    {
        if (messageFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(messageFrequency), "Must be > 0.");
        _messageFrequency = messageFrequency;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new SamplingProcessor(_messageFrequency);
}

/// <summary>
/// Leaf definition that samples messages by period (at most one message per period).
/// </summary>
public sealed class SamplePeriodDefinition : ProcessorDefinition
{
    private readonly TimeSpan _period;

    /// <summary>Gets the minimum period between forwarded messages.</summary>
    public TimeSpan Period => _period;

    /// <summary>Creates a sampling definition with a period gate.</summary>
    public SamplePeriodDefinition(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), "Must be positive.");
        _period = period;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new SamplingProcessor(_period);
}
