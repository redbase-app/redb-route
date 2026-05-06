using System.Text;
using redb.Route.Abstractions;

namespace redb.Route;

/// <summary>
/// Fluent entry point for Timer consumer endpoints.
/// <example><code>
/// .From(Timer.Every("heartbeat").Period(5000).Delay(1000))
/// .From(Timer.Once("init"))
/// </code></example>
/// </summary>
public static class TimerDsl
{
    /// <summary>Create a named repeating timer.</summary>
    public static TimerBuilder Every(string name) => new(name);

    /// <summary>Create a timer that fires once (repeatCount=1).</summary>
    public static TimerBuilder Once(string name) => new TimerBuilder(name).RepeatCount(1);
}

/// <summary>Fluent builder for Timer endpoint URIs.</summary>
public sealed class TimerBuilder
{
    private readonly string _name;
    private string? _period;
    private string? _delay;
    private string? _repeatCount;

    internal TimerBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>Interval between ticks in milliseconds. Default 1000.</summary>
    public TimerBuilder Period(int ms) { _period = ms.ToString(); return this; }
    /// <summary>Interval from an expression.</summary>
    public TimerBuilder Period(IExpression ms) { _period = ms.ToTemplateString(); return this; }

    /// <summary>Initial delay before first tick in milliseconds.</summary>
    public TimerBuilder Delay(int ms) { _delay = ms.ToString(); return this; }
    /// <summary>Initial delay from an expression.</summary>
    public TimerBuilder Delay(IExpression ms) { _delay = ms.ToTemplateString(); return this; }

    /// <summary>Number of times to fire. Default unlimited (0).</summary>
    public TimerBuilder RepeatCount(int count) { _repeatCount = count.ToString(); return this; }
    /// <summary>Repeat count from an expression.</summary>
    public TimerBuilder RepeatCount(IExpression count) { _repeatCount = count.ToTemplateString(); return this; }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("timer:");
        sb.Append(_name);

        var sep = '?';
        void AppendIf(string key, string? v) { if (v != null) { sb.Append(sep); sb.Append(key); sb.Append('='); sb.Append(v); sep = '&'; } }

        AppendIf("period", _period);
        AppendIf("delay", _delay);
        AppendIf("repeatCount", _repeatCount);

        return sb.ToString();
    }

    public static implicit operator string(TimerBuilder b) => b.Build();
    public override string ToString() => Build();
}
