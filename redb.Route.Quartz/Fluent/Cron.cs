using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Quartz;

/// <summary>
/// Fluent entry point for Cron consumer endpoints (Quartz-based).
/// <example><code>
/// // Simple
/// .From(Cron.Schedule("every5s", "0/5 * * * * ?"))
///
/// // With group
/// .From(Cron.Schedule("billing/invoice", "0 0 2 * * ?"))
///
/// // Full options
/// .From(Cron.Schedule("billing/invoice", "0 0 2 * * ?")
///     .TimeZone("Europe/Moscow")
///     .Stateful()
///     .Recoverable()
///     .MisfireInstruction(QuartzMisfirePolicy.CronDoNothing)
///     .Threads(4))
/// </code></example>
/// </summary>
public static class Cron
{
    /// <summary>Create a cron-scheduled consumer.</summary>
    /// <param name="name">Logical job name, optionally "group/name" (default group: "redb").</param>
    /// <param name="schedule">Quartz cron expression (6-7 fields).</param>
    public static CronBuilder Schedule(string name, string schedule) => new(name, schedule);
}

/// <summary>Fluent builder for Cron endpoint URIs. Scheme: <c>cron</c>.</summary>
public sealed class CronBuilder
{
    private readonly string _name;
    private readonly string _schedule;
    private string? _threads;

    // Job lifecycle
    private bool? _deleteJob;
    private bool? _pauseJob;
    private bool? _durableJob;
    private bool? _stateful;
    private bool? _recoverableJob;
    private bool? _prefixJobName;

    // Trigger timing
    private QuartzMisfirePolicy? _misfireInstruction;
    private string? _timeZone;
    private string? _startAt;
    private string? _endAt;
    private string? _customCalendar;
    private long? _triggerStartDelay;

    internal CronBuilder(string name, string schedule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule);
        _name = name;
        _schedule = schedule;
    }

    /// <summary>Max concurrent fires. Default 1.</summary>
    public CronBuilder Threads(int count) { _threads = count.ToString(); return this; }
    /// <summary>Threads from an expression.</summary>
    public CronBuilder Threads(IExpression count) { _threads = count.ToTemplateString(); return this; }

    // ── Job lifecycle ──

    /// <summary>Delete the job when the route stops (default: true).</summary>
    public CronBuilder DeleteJob(bool value = true) { _deleteJob = value; return this; }
    /// <summary>Pause the trigger on route stop instead of deleting.</summary>
    public CronBuilder PauseJob(bool value = true) { _pauseJob = value; return this; }
    /// <summary>Keep the job even if no triggers reference it.</summary>
    public CronBuilder DurableJob(bool value = true) { _durableJob = value; return this; }
    /// <summary>PersistJobDataAfterExecution + DisallowConcurrentExecution.</summary>
    public CronBuilder Stateful(bool value = true) { _stateful = value; return this; }
    /// <summary>Re-execute the job on failover/recovery.</summary>
    public CronBuilder Recoverable(bool value = true) { _recoverableJob = value; return this; }
    /// <summary>Prefix the job name with the endpoint ID.</summary>
    public CronBuilder PrefixJobNameWithEndpointId(bool value = true) { _prefixJobName = value; return this; }

    // ── Trigger timing ──

    /// <summary>Misfire handling strategy.</summary>
    public CronBuilder MisfireInstruction(QuartzMisfirePolicy policy) { _misfireInstruction = policy; return this; }
    /// <summary>Time zone for the cron trigger (IANA ID, e.g. "Europe/Moscow").</summary>
    public CronBuilder TimeZone(string tz) { _timeZone = tz; return this; }
    /// <summary>Trigger start date (ISO 8601).</summary>
    public CronBuilder StartAt(string date) { _startAt = date; return this; }
    /// <summary>Trigger start date.</summary>
    public CronBuilder StartAt(DateTimeOffset date) { _startAt = date.ToString("o"); return this; }
    /// <summary>Trigger end date (ISO 8601).</summary>
    public CronBuilder EndAt(string date) { _endAt = date; return this; }
    /// <summary>Trigger end date.</summary>
    public CronBuilder EndAt(DateTimeOffset date) { _endAt = date.ToString("o"); return this; }
    /// <summary>Reference to an ICalendar in the route context registry.</summary>
    public CronBuilder CustomCalendar(string calendarName) { _customCalendar = calendarName; return this; }
    /// <summary>Delay in ms before the first trigger fire (default: 500).</summary>
    public CronBuilder TriggerStartDelay(long ms) { _triggerStartDelay = ms; return this; }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("cron:");
        sb.Append(_name);
        sb.Append("?schedule=");
        sb.Append(Uri.EscapeDataString(_schedule));

        void AppendIf(string key, string? v) { if (v != null) { sb.Append('&'); sb.Append(key); sb.Append('='); sb.Append(v); } }
        void AppendBool(string key, bool? v) { if (v.HasValue) { sb.Append('&'); sb.Append(key); sb.Append('='); sb.Append(v.Value ? "true" : "false"); } }

        AppendIf("threads", _threads);
        AppendBool("deleteJob", _deleteJob);
        AppendBool("pauseJob", _pauseJob);
        AppendBool("durableJob", _durableJob);
        AppendBool("stateful", _stateful);
        AppendBool("recoverableJob", _recoverableJob);
        AppendBool("prefixJobNameWithEndpointId", _prefixJobName);
        if (_misfireInstruction.HasValue) AppendIf("misfireInstruction", _misfireInstruction.Value.ToString());
        AppendIf("timeZone", _timeZone);
        AppendIf("startAt", _startAt != null ? Uri.EscapeDataString(_startAt) : null);
        AppendIf("endAt", _endAt != null ? Uri.EscapeDataString(_endAt) : null);
        AppendIf("customCalendar", _customCalendar);
        if (_triggerStartDelay.HasValue) AppendIf("triggerStartDelay", _triggerStartDelay.Value.ToString());

        return sb.ToString();
    }

    public static implicit operator string(CronBuilder b) => b.Build();
    public override string ToString() => Build();
}

/// <summary>
/// Fluent entry point for Quartz-based repeating timer.
/// <example><code>
/// // Simple
/// .From(QTimer.Every("heartbeat").Period(5000))
///
/// // With group
/// .From(QTimer.Every("monitoring/heartbeat").Period(5000).FixedRate())
///
/// // Full options
/// .From(QTimer.Every("monitoring/heartbeat")
///     .Period(5000).Delay(1000).FixedRate()
///     .Stateful()
///     .Recoverable()
///     .MisfireInstruction(QuartzMisfirePolicy.SimpleFireNow)
///     .Threads(2))
/// </code></example>
/// </summary>
public static class QTimer
{
    /// <summary>Create a repeating timer consumer.</summary>
    /// <param name="name">Logical job name, optionally "group/name" (default group: "redb").</param>
    public static QTimerBuilder Every(string name) => new(name);
}

/// <summary>Fluent builder for Quartz Timer endpoint URIs. Scheme: <c>qtimer</c>.</summary>
public sealed class QTimerBuilder
{
    private readonly string _name;
    private string? _period;
    private string? _delay;
    private bool _fixedRate;
    private string? _threads;

    // Job lifecycle
    private bool? _deleteJob;
    private bool? _pauseJob;
    private bool? _durableJob;
    private bool? _stateful;
    private bool? _recoverableJob;
    private bool? _prefixJobName;

    // Trigger timing
    private QuartzMisfirePolicy? _misfireInstruction;
    private int? _repeatCount;
    private string? _startAt;
    private string? _endAt;
    private string? _customCalendar;

    internal QTimerBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>Interval between fires in ms. Default 1000.</summary>
    public QTimerBuilder Period(int ms) { _period = ms.ToString(); return this; }
    /// <summary>Period from an expression.</summary>
    public QTimerBuilder Period(IExpression ms) { _period = ms.ToTemplateString(); return this; }

    /// <summary>Initial delay before first fire in ms.</summary>
    public QTimerBuilder Delay(int ms) { _delay = ms.ToString(); return this; }
    /// <summary>Delay from an expression.</summary>
    public QTimerBuilder Delay(IExpression ms) { _delay = ms.ToTemplateString(); return this; }

    /// <summary>Use fixed-rate scheduling instead of fixed-delay.</summary>
    public QTimerBuilder FixedRate() { _fixedRate = true; return this; }

    /// <summary>Max concurrent fires. Default 1.</summary>
    public QTimerBuilder Threads(int count) { _threads = count.ToString(); return this; }
    /// <summary>Threads from an expression.</summary>
    public QTimerBuilder Threads(IExpression count) { _threads = count.ToTemplateString(); return this; }

    // ── Job lifecycle ──

    /// <summary>Delete the job when the route stops (default: true).</summary>
    public QTimerBuilder DeleteJob(bool value = true) { _deleteJob = value; return this; }
    /// <summary>Pause the trigger on route stop instead of deleting.</summary>
    public QTimerBuilder PauseJob(bool value = true) { _pauseJob = value; return this; }
    /// <summary>Keep the job even if no triggers reference it.</summary>
    public QTimerBuilder DurableJob(bool value = true) { _durableJob = value; return this; }
    /// <summary>PersistJobDataAfterExecution + DisallowConcurrentExecution.</summary>
    public QTimerBuilder Stateful(bool value = true) { _stateful = value; return this; }
    /// <summary>Re-execute the job on failover/recovery.</summary>
    public QTimerBuilder Recoverable(bool value = true) { _recoverableJob = value; return this; }
    /// <summary>Prefix the job name with the endpoint ID.</summary>
    public QTimerBuilder PrefixJobNameWithEndpointId(bool value = true) { _prefixJobName = value; return this; }

    // ── Trigger timing ──

    /// <summary>Misfire handling strategy.</summary>
    public QTimerBuilder MisfireInstruction(QuartzMisfirePolicy policy) { _misfireInstruction = policy; return this; }
    /// <summary>Number of times to repeat. -1 = forever (default).</summary>
    public QTimerBuilder RepeatCount(int count) { _repeatCount = count; return this; }
    /// <summary>Trigger start date (ISO 8601).</summary>
    public QTimerBuilder StartAt(string date) { _startAt = date; return this; }
    /// <summary>Trigger start date.</summary>
    public QTimerBuilder StartAt(DateTimeOffset date) { _startAt = date.ToString("o"); return this; }
    /// <summary>Trigger end date (ISO 8601).</summary>
    public QTimerBuilder EndAt(string date) { _endAt = date; return this; }
    /// <summary>Trigger end date.</summary>
    public QTimerBuilder EndAt(DateTimeOffset date) { _endAt = date.ToString("o"); return this; }
    /// <summary>Reference to an ICalendar in the route context registry.</summary>
    public QTimerBuilder CustomCalendar(string calendarName) { _customCalendar = calendarName; return this; }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("qtimer:");
        sb.Append(_name);

        var sep = '?';
        void AppendIf(string key, string? v) { if (v != null) { sb.Append(sep); sb.Append(key); sb.Append('='); sb.Append(v); sep = '&'; } }
        void AppendBool(string key, bool? v) { if (v.HasValue) { sb.Append(sep); sb.Append(key); sb.Append('='); sb.Append(v.Value ? "true" : "false"); sep = '&'; } }
        void AppendBoolFlag(string key, bool v) { if (v) { sb.Append(sep); sb.Append(key); sb.Append("=true"); sep = '&'; } }

        AppendIf("period", _period);
        AppendIf("delay", _delay);
        AppendBoolFlag("fixedRate", _fixedRate);
        AppendIf("threads", _threads);
        AppendBool("deleteJob", _deleteJob);
        AppendBool("pauseJob", _pauseJob);
        AppendBool("durableJob", _durableJob);
        AppendBool("stateful", _stateful);
        AppendBool("recoverableJob", _recoverableJob);
        AppendBool("prefixJobNameWithEndpointId", _prefixJobName);
        if (_misfireInstruction.HasValue) AppendIf("misfireInstruction", _misfireInstruction.Value.ToString());
        if (_repeatCount.HasValue) AppendIf("repeatCount", _repeatCount.Value.ToString());
        AppendIf("startAt", _startAt != null ? Uri.EscapeDataString(_startAt) : null);
        AppendIf("endAt", _endAt != null ? Uri.EscapeDataString(_endAt) : null);
        AppendIf("customCalendar", _customCalendar);

        return sb.ToString();
    }

    public static implicit operator string(QTimerBuilder b) => b.Build();
    public override string ToString() => Build();
}
