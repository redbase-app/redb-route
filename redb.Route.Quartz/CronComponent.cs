using Quartz;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Quartz;

/// <summary>
/// Cron scheduling component. Scheme: "cron".
/// Fires exchanges on a cron schedule using Quartz.NET CronTrigger.
/// URI: cron://jobName?schedule=0/5 * * * * ?&amp;threads=1
/// </summary>
public class CronComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "cron";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var options = new CronEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        ParseGroupAndJob(uri.Path, options);
        options.Validate();
        return new CronEndpoint(uri, this, options);
    }

    /// <summary>Parses "group/job" or "job" from the URI path into options.</summary>
    internal static void ParseGroupAndJob(string path, CronEndpointOptions options)
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
/// Cron endpoint options.
/// </summary>
public class CronEndpointOptions : EndpointOptions
{
    /// <summary>
    /// Cron expression (Quartz format, 6-7 fields).
    /// Example: "0/5 * * * * ?" (every 5 seconds).
    /// </summary>
    public string Schedule { get; set; } = string.Empty;

    /// <summary>Maximum concurrent fires (default: 1).</summary>
    public int Threads { get; set; } = 1;

    // ── Group / Job (parsed from URI path: cron://group/job or cron://job) ──

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

    /// <summary>Time zone for the cron trigger (IANA ID, e.g. "Europe/Moscow"). Default: local.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Trigger start date (ISO 8601 format). If not set, starts immediately.</summary>
    public string? StartAt { get; set; }

    /// <summary>Trigger end date (ISO 8601 format). If not set, runs indefinitely.</summary>
    public string? EndAt { get; set; }

    /// <summary>Name of an ICalendar in the route context registry to use for exclusions.</summary>
    public string? CustomCalendar { get; set; }

    /// <summary>Delay in ms before the first trigger fire (default: 500).</summary>
    public long TriggerStartDelay { get; set; } = 500;

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Schedule))
            throw new ArgumentException("Cron schedule expression is required. Example: '0/5 * * * * ?'", nameof(Schedule));

        if (Threads <= 0)
            throw new ArgumentOutOfRangeException(nameof(Threads), Threads, "Threads must be greater than 0.");

        if (DeleteJob && PauseJob)
            throw new ArgumentException("Cannot set both DeleteJob and PauseJob to true.");

        // Validate cron expression early
        try
        {
            new CronExpression(Schedule);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Invalid cron expression: '{Schedule}'. {ex.Message}", nameof(Schedule), ex);
        }
    }
}

/// <summary>
/// Cron endpoint. Consumer-only — fires on a cron schedule.
/// </summary>
public class CronEndpoint : EndpointBase<CronEndpointOptions>
{
    /// <summary>Creates a cron endpoint.</summary>
    public CronEndpoint(EndpointUri uri, CronComponent component, CronEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <summary>Gets the cron schedule expression.</summary>
    internal string Schedule => Options.Schedule;

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

    /// <summary>Gets whether the job is stateful (persist data + disallow concurrent).</summary>
    internal bool Stateful => Options.Stateful;

    /// <summary>Gets whether the job is recoverable on failover.</summary>
    internal bool RecoverableJob => Options.RecoverableJob;

    /// <summary>Gets whether to prefix job name with endpoint ID.</summary>
    internal bool PrefixJobNameWithEndpointId => Options.PrefixJobNameWithEndpointId;

    /// <summary>Gets the misfire instruction.</summary>
    internal QuartzMisfirePolicy MisfireInstruction => Options.MisfireInstruction;

    /// <summary>Gets the time zone ID for the cron trigger.</summary>
    internal string? TimeZoneId => Options.TimeZone;

    /// <summary>Gets the trigger start date string.</summary>
    internal string? StartAt => Options.StartAt;

    /// <summary>Gets the trigger end date string.</summary>
    internal string? EndAt => Options.EndAt;

    /// <summary>Gets the custom calendar name from registry.</summary>
    internal string? CustomCalendar => Options.CustomCalendar;

    /// <summary>Gets the trigger start delay in ms.</summary>
    internal long TriggerStartDelay => Options.TriggerStartDelay;

    /// <inheritdoc />
    public override IProducer CreateProducer()
    {
        throw new NotSupportedException("Cron endpoints do not support producers. Use them only as From() source.");
    }

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new CronConsumer(this, processor);
    }
}

/// <summary>
/// Cron consumer. Fires the processor on a Quartz CronTrigger schedule.
/// </summary>
public class CronConsumer : QuartzConsumerBase
{
    private readonly CronEndpoint _endpoint;

    /// <summary>Creates a cron consumer.</summary>
    public CronConsumer(CronEndpoint endpoint, IProcessor processor)
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

        // Cron schedule with optional timezone and misfire
        builder = builder.WithCronSchedule(_endpoint.Schedule, x =>
        {
            if (!string.IsNullOrEmpty(_endpoint.TimeZoneId))
                x.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(_endpoint.TimeZoneId));

            switch (_endpoint.MisfireInstruction)
            {
                case QuartzMisfirePolicy.CronDoNothing:
                    x.WithMisfireHandlingInstructionDoNothing();
                    break;
                case QuartzMisfirePolicy.CronFireOnceNow:
                    x.WithMisfireHandlingInstructionFireAndProceed();
                    break;
                // Default: use Quartz default
            }
        });

        // Start date
        if (!string.IsNullOrEmpty(_endpoint.StartAt))
            builder = builder.StartAt(DateTimeOffset.Parse(_endpoint.StartAt, System.Globalization.CultureInfo.InvariantCulture));
        else if (_endpoint.TriggerStartDelay > 0)
            builder = builder.StartAt(DateTimeOffset.UtcNow.AddMilliseconds(_endpoint.TriggerStartDelay));
        else
            builder = builder.StartNow();

        // End date
        if (!string.IsNullOrEmpty(_endpoint.EndAt))
            builder = builder.EndAt(DateTimeOffset.Parse(_endpoint.EndAt, System.Globalization.CultureInfo.InvariantCulture));

        // Calendar exclusion
        if (!string.IsNullOrEmpty(_endpoint.CustomCalendar))
            builder = builder.ModifiedByCalendar(_endpoint.CustomCalendar);

        return builder.Build();
    }

    /// <inheritdoc />
    protected override void PopulateExchangeProperties(IExchange exchange, IJobExecutionContext context)
    {
        exchange.Properties["CamelCronSchedule"] = _endpoint.Schedule;
        exchange.Properties["CamelCronName"] = _endpoint.JobName;
        exchange.Properties["CamelCronGroup"] = _endpoint.GroupName;
    }
}
