using Quartz;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Quartz;

/// <summary>
/// Quartz-based timer component. Scheme: "qtimer".
/// Like the built-in "timer" but backed by Quartz.NET SimpleSchedule — supports
/// FixedRate mode, misfire handling, and shared scheduler.
/// URI: qtimer://group/name?period=1000&amp;delay=0&amp;fixedRate=true&amp;threads=1
/// URI: qtimer://name?period=1000 (group defaults to "redb")
/// </summary>
public class QuartzTimerComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "qtimer";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new QuartzTimerEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        ParseGroupAndJob(uri.Path, options);
        options.Validate();
        return new QuartzTimerEndpoint(uri, this, options);
    }

    /// <summary>Parses "group/job" or "job" from the URI path into options.</summary>
    internal static void ParseGroupAndJob(string path, QuartzTimerEndpointOptions options)
    {
        var slashIndex = path.IndexOf('/');
        if (slashIndex > 0 && slashIndex < path.Length - 1)
        {
            options.GroupName = path[..slashIndex];
            options.JobName = path[(slashIndex + 1)..];
        }
        else
        {
            options.JobName = path;
            // GroupName defaults to "redb"
        }
    }
}

/// <summary>
/// Quartz timer endpoint options.
/// </summary>
public class QuartzTimerEndpointOptions : EndpointOptions
{
    /// <summary>Period between fires in milliseconds (default: 1000).</summary>
    public int Period { get; set; } = 1000;

    /// <summary>Initial delay before first fire in milliseconds (default: 0).</summary>
    public int Delay { get; set; }

    /// <summary>
    /// When true, fires at fixed rate regardless of processing time.
    /// When false (default), next fire starts after previous completes + period.
    /// </summary>
    public bool FixedRate { get; set; }

    /// <summary>Maximum concurrent fires (default: 1).</summary>
    public int Threads { get; set; } = 1;

    // ── Group / Job (parsed from URI path: qtimer://group/job or qtimer://job) ──

    /// <summary>Quartz group name. Parsed from URI path or defaults to "redb".</summary>
    public string GroupName { get; set; } = "redb";

    /// <summary>Quartz job/trigger name. Parsed from URI path.</summary>
    public string JobName { get; set; } = string.Empty;

    // ── Job lifecycle ──

    /// <summary>Delete the job when the route stops (default: true).</summary>
    public bool DeleteJob { get; set; } = true;

    /// <summary>Pause the trigger when the route stops instead of deleting (default: false).
    /// Cannot be true simultaneously with <see cref="DeleteJob"/>.</summary>
    public bool PauseJob { get; set; }

    /// <summary>Keep the job in the scheduler even if no triggers reference it (default: false).</summary>
    public bool DurableJob { get; set; }

    /// <summary>Use PersistJobDataAfterExecution + DisallowConcurrentExecution (default: false).</summary>
    public bool Stateful { get; set; }

    /// <summary>Re-execute the job on failover/recovery (default: false).</summary>
    public bool RecoverableJob { get; set; }

    /// <summary>Whether to prefix the job name with the endpoint ID (default: false).</summary>
    public bool PrefixJobNameWithEndpointId { get; set; }

    // ── Trigger timing ──

    /// <summary>Misfire handling strategy (default: trigger-type-specific default).</summary>
    public QuartzMisfirePolicy MisfireInstruction { get; set; } = QuartzMisfirePolicy.Default;

    /// <summary>Number of times to repeat. -1 = forever (default).</summary>
    public int RepeatCount { get; set; } = -1;

    /// <summary>Trigger start date (ISO 8601 format). If not set, uses Delay.</summary>
    public string? StartAt { get; set; }

    /// <summary>Trigger end date (ISO 8601 format). If not set, runs indefinitely.</summary>
    public string? EndAt { get; set; }

    /// <summary>Name of an ICalendar in the route context registry to use for exclusions.</summary>
    public string? CustomCalendar { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (Period <= 0)
            throw new ArgumentOutOfRangeException(nameof(Period), Period, "Period must be greater than 0.");

        if (Threads <= 0)
            throw new ArgumentOutOfRangeException(nameof(Threads), Threads, "Threads must be greater than 0.");

        if (DeleteJob && PauseJob)
            throw new ArgumentException("Cannot set both DeleteJob and PauseJob to true.");
    }
}

/// <summary>
/// Quartz timer endpoint. Consumer-only — fires on a simple periodic schedule via Quartz.
/// </summary>
public class QuartzTimerEndpoint : EndpointBase<QuartzTimerEndpointOptions>
{
    /// <summary>Creates a Quartz timer endpoint.</summary>
    public QuartzTimerEndpoint(EndpointUri uri, QuartzTimerComponent component, QuartzTimerEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>Gets the period in milliseconds.</summary>
    internal int Period => Options.Period;

    /// <summary>Gets the delay in milliseconds.</summary>
    internal int Delay => Options.Delay;

    /// <summary>Gets whether fixed-rate mode is enabled.</summary>
    internal bool FixedRate => Options.FixedRate;

    /// <summary>Gets the max concurrent threads.</summary>
    internal int Threads => Options.Threads;

    /// <summary>Gets the Quartz group name.</summary>
    internal string GroupName => Options.GroupName;

    /// <summary>Gets the Quartz job/trigger name.</summary>
    internal string JobName => Options.JobName;

    /// <summary>Gets whether to delete the job on route stop.</summary>
    internal bool DeleteJob => Options.DeleteJob;

    /// <summary>Gets whether to pause the trigger on route stop.</summary>
    internal bool PauseJob => Options.PauseJob;

    /// <summary>Gets whether the job is durable.</summary>
    internal bool DurableJob => Options.DurableJob;

    /// <summary>Gets whether the job is stateful.</summary>
    internal bool Stateful => Options.Stateful;

    /// <summary>Gets whether the job is recoverable on failover.</summary>
    internal bool RecoverableJob => Options.RecoverableJob;

    /// <summary>Gets whether to prefix job name with endpoint ID.</summary>
    internal bool PrefixJobNameWithEndpointId => Options.PrefixJobNameWithEndpointId;

    /// <summary>Gets the misfire instruction.</summary>
    internal QuartzMisfirePolicy MisfireInstruction => Options.MisfireInstruction;

    /// <summary>Gets the repeat count (-1 = forever).</summary>
    internal int RepeatCount => Options.RepeatCount;

    /// <summary>Gets the trigger start date string.</summary>
    internal string? StartAt => Options.StartAt;

    /// <summary>Gets the trigger end date string.</summary>
    internal string? EndAt => Options.EndAt;

    /// <summary>Gets the custom calendar name from registry.</summary>
    internal string? CustomCalendar => Options.CustomCalendar;

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        throw new NotSupportedException("QuartzTimer endpoints do not support producers. Use them only as From() source.");
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new QuartzTimerConsumer(this, processor);
    }
}

/// <summary>
/// Quartz timer consumer. Fires the processor on a Quartz SimpleSchedule.
/// Supports FixedRate mode, configurable delay, misfire policies, and all lifecycle options.
/// </summary>
public class QuartzTimerConsumer : QuartzConsumerBase
{
    private readonly QuartzTimerEndpoint _endpoint;

    /// <summary>Creates a Quartz timer consumer.</summary>
    public QuartzTimerConsumer(QuartzTimerEndpoint endpoint, IProcessor processor)
        : base(endpoint, processor, endpoint.Threads,
               endpoint.GroupName, endpoint.JobName,
               endpoint.DeleteJob, endpoint.PauseJob, endpoint.DurableJob,
               endpoint.Stateful, endpoint.RecoverableJob,
               endpoint.PrefixJobNameWithEndpointId, endpoint.CustomCalendar)
    {
        _endpoint = endpoint;
    }

    /// <inheritdoc />
    protected override ITrigger BuildTrigger(TriggerKey triggerKey, JobKey jobKey)
    {
        var builder = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey);

        // Start date
        if (!string.IsNullOrEmpty(_endpoint.StartAt))
            builder = builder.StartAt(DateTimeOffset.Parse(_endpoint.StartAt, System.Globalization.CultureInfo.InvariantCulture));
        else if (_endpoint.Delay > 0)
            builder = builder.StartAt(DateTimeOffset.UtcNow.AddMilliseconds(_endpoint.Delay));
        else
            builder = builder.StartNow();

        // End date
        if (!string.IsNullOrEmpty(_endpoint.EndAt))
            builder = builder.EndAt(DateTimeOffset.Parse(_endpoint.EndAt, System.Globalization.CultureInfo.InvariantCulture));

        // SimpleSchedule with misfire handling
        builder = builder.WithSimpleSchedule(x =>
        {
            x.WithInterval(TimeSpan.FromMilliseconds(_endpoint.Period));

            if (_endpoint.RepeatCount < 0)
                x.RepeatForever();
            else
                x.WithRepeatCount(_endpoint.RepeatCount);

            // Misfire policy
            switch (_endpoint.MisfireInstruction)
            {
                case QuartzMisfirePolicy.SimpleFireNow:
                    x.WithMisfireHandlingInstructionFireNow();
                    break;
                case QuartzMisfirePolicy.SimpleRescheduleNowWithExistingRepeatCount:
                    x.WithMisfireHandlingInstructionNowWithExistingCount();
                    break;
                case QuartzMisfirePolicy.SimpleRescheduleNowWithRemainingRepeatCount:
                    x.WithMisfireHandlingInstructionNowWithRemainingCount();
                    break;
                case QuartzMisfirePolicy.SimpleRescheduleNextWithRemainingCount:
                    x.WithMisfireHandlingInstructionNextWithRemainingCount();
                    break;
                case QuartzMisfirePolicy.SimpleRescheduleNextWithExistingCount:
                    x.WithMisfireHandlingInstructionNextWithExistingCount();
                    break;
                default:
                    // Default: FixedRate → FireNow, otherwise → NextWithRemaining
                    if (_endpoint.FixedRate)
                        x.WithMisfireHandlingInstructionFireNow();
                    else
                        x.WithMisfireHandlingInstructionNextWithRemainingCount();
                    break;
            }
        });

        // Calendar exclusion
        if (!string.IsNullOrEmpty(_endpoint.CustomCalendar))
            builder = builder.ModifiedByCalendar(_endpoint.CustomCalendar);

        return builder.Build();
    }

    /// <inheritdoc />
    protected override void PopulateExchangeProperties(IExchange exchange, IJobExecutionContext context)
    {
        exchange.Properties["CamelTimerName"] = _endpoint.JobName;
        exchange.Properties["CamelTimerGroup"] = _endpoint.GroupName;
        exchange.Properties["CamelTimerPeriod"] = _endpoint.Period;
        exchange.Properties["CamelTimerFixedRate"] = _endpoint.FixedRate;
    }
}
