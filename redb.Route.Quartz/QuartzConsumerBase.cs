using System.Collections.Specialized;
using Quartz;
using Quartz.Impl;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Quartz;

/// <summary>
/// Base class for Quartz-based consumers. Handles shared scheduler lifecycle,
/// job creation, orphan cleanup, and concurrency control via semaphore.
/// Subclasses provide trigger configuration (simple schedule vs cron expression).
/// </summary>
public abstract class QuartzConsumerBase : IConsumer
{
    private const string SchedulerContextKey = "redb.Route.Quartz.RouteContext";

    private readonly IEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly int _maxThreads;
    private readonly SemaphoreSlim _semaphore;
    private IScheduler? _scheduler;

    // Job/trigger identity
    private readonly string _groupName;
    private readonly string _jobName;

    // Job lifecycle
    private readonly bool _deleteJob;
    private readonly bool _pauseJob;
    private readonly bool _durableJob;
    private readonly bool _stateful;
    private readonly bool _recoverableJob;
    private readonly bool _prefixJobNameWithEndpointId;
    private readonly string? _customCalendar;

    private CancellationTokenSource? _processingCts;
    private int _inflightCount;

    /// <summary>Maximum time to wait for in-flight jobs to complete during stop.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Creates a Quartz consumer base.</summary>
    /// <param name="endpoint">Owning endpoint.</param>
    /// <param name="processor">Downstream processor pipeline.</param>
    /// <param name="maxThreads">Max concurrent job executions (default 1).</param>
    /// <param name="groupName">Quartz group name (default "redb").</param>
    /// <param name="jobName">Quartz job/trigger name (default: endpoint path).</param>
    /// <param name="deleteJob">Delete job on route stop (default true).</param>
    /// <param name="pauseJob">Pause trigger on route stop (default false).</param>
    /// <param name="durableJob">Keep job without triggers (default false).</param>
    /// <param name="stateful">PersistJobData + DisallowConcurrent (default false).</param>
    /// <param name="recoverableJob">Re-execute on failover (default false).</param>
    /// <param name="prefixJobNameWithEndpointId">Prefix job name with endpoint ID (default false).</param>
    /// <param name="customCalendar">Calendar name from registry (default null).</param>
    protected QuartzConsumerBase(
        IEndpoint endpoint,
        IProcessor processor,
        int maxThreads = 1,
        string? groupName = null,
        string? jobName = null,
        bool deleteJob = true,
        bool pauseJob = false,
        bool durableJob = false,
        bool stateful = false,
        bool recoverableJob = false,
        bool prefixJobNameWithEndpointId = false,
        string? customCalendar = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _maxThreads = maxThreads > 0 ? maxThreads : 1;
        _semaphore = new SemaphoreSlim(_maxThreads);

        _groupName = string.IsNullOrWhiteSpace(groupName) ? "redb" : groupName;
        _jobName = string.IsNullOrWhiteSpace(jobName) ? (endpoint.Uri?.Path ?? "default") : jobName;
        _deleteJob = deleteJob;
        _pauseJob = pauseJob;
        _durableJob = durableJob;
        _stateful = stateful;
        _recoverableJob = recoverableJob;
        _prefixJobNameWithEndpointId = prefixJobNameWithEndpointId;
        _customCalendar = customCalendar;
    }

    /// <summary>Gets the endpoint URI string used as job/trigger group key.</summary>
    protected string EndpointUriKey => _endpoint.Uri.NormalizedKey;

    /// <summary>
    /// Builds the Quartz trigger for this consumer. Called once during <see cref="Start"/>.
    /// </summary>
    /// <param name="triggerKey">Pre-built trigger key (name + group).</param>
    /// <param name="jobKey">The job key the trigger must reference.</param>
    /// <returns>Configured trigger.</returns>
    protected abstract ITrigger BuildTrigger(TriggerKey triggerKey, JobKey jobKey);

    /// <summary>
    /// Populates exchange with component-specific properties (e.g. cron schedule, timer period, fire time).
    /// </summary>
    /// <param name="exchange">The exchange being built.</param>
    /// <param name="context">Quartz job execution context with fire time info.</param>
    protected abstract void PopulateExchangeProperties(IExchange exchange, IJobExecutionContext context);

    /// <inheritdoc />
    public async Task Start(CancellationToken ct = default)
    {
        _processingCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _inflightCount, 0);

        var routeContext = (_endpoint.Component as ComponentBase)?.Context;
        if (routeContext == null)
            throw new InvalidOperationException("Quartz consumer requires a RouteContext. Register the component via AddRedbRouteQuartz().");

        // Shared scheduler: reuse if exists, create if not
        _scheduler = routeContext.GetService<IScheduler>();
        if (_scheduler == null)
        {
            // Create a scheduler unique to this RouteContext (avoids "already exists" in multi-context scenarios)
            var props = new NameValueCollection
            {
                ["quartz.scheduler.instanceName"] = $"redb-route-{routeContext.ContextId}"
            };
            var factory = new StdSchedulerFactory(props);
            _scheduler = await factory.GetScheduler(ct).ConfigureAwait(false);
            routeContext.AddService(typeof(IScheduler), _scheduler);
            await _scheduler.Start(ct).ConfigureAwait(false);
        }
        else if (_scheduler.IsShutdown)
        {
            throw new InvalidOperationException("Quartz scheduler has been shut down.");
        }
        else if (!_scheduler.IsStarted)
        {
            await _scheduler.Start(ct).ConfigureAwait(false);
        }

        // Store RouteContext in scheduler context for job access
        var contextKey = $"{SchedulerContextKey}_{routeContext.ContextId}";
        _scheduler.Context.Put(contextKey, routeContext);

        // Register custom calendar if specified
        if (!string.IsNullOrEmpty(_customCalendar))
        {
            var calendar = routeContext.GetFromRegistry<global::Quartz.ICalendar>(_customCalendar);
            var existingCalendars = await _scheduler.GetCalendarNames(ct).ConfigureAwait(false);
            if (calendar != null && !existingCalendars.Contains(_customCalendar))
            {
                await _scheduler.AddCalendar(_customCalendar, calendar, replace: true, updateTriggers: true, ct)
                    .ConfigureAwait(false);
            }
        }

        // Build job with group/name identity
        var effectiveJobName = _prefixJobNameWithEndpointId
            ? $"{EndpointUriKey}-{_jobName}"
            : _jobName;
        var jobKey = new JobKey(effectiveJobName, _groupName);

        var jobBuilder = _stateful
            ? JobBuilder.Create<QuartzStatefulRouteJob>()
            : JobBuilder.Create<QuartzRouteJob>();

        var job = jobBuilder
            .WithIdentity(jobKey)
            .UsingJobData("endpointUri", EndpointUriKey)
            .UsingJobData("contextId", routeContext.ContextId)
            .UsingJobData("consumerType", GetType().AssemblyQualifiedName)
            .StoreDurably(_durableJob)
            .RequestRecovery(_recoverableJob)
            .Build();

        await _scheduler.AddJob(job, replace: true, storeNonDurableWhileAwaitingScheduling: true, ct).ConfigureAwait(false);

        // Build trigger from subclass
        var triggerKey = new TriggerKey(effectiveJobName, _groupName);
        var trigger = BuildTrigger(triggerKey, jobKey);

        // Replace or create trigger
        if (await _scheduler.CheckExists(triggerKey, ct).ConfigureAwait(false))
        {
            await _scheduler.RescheduleJob(triggerKey, trigger, ct).ConfigureAwait(false);
        }
        else
        {
            await _scheduler.ScheduleJob(trigger, ct).ConfigureAwait(false);
        }

        // Register this consumer instance so QuartzRouteJob can find it
        QuartzConsumerRegistry.Register(EndpointUriKey, this);
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // Unregister first
        QuartzConsumerRegistry.Unregister(EndpointUriKey);

        if (_scheduler == null || _scheduler.IsShutdown)
            return;

        var effectiveJobName = _prefixJobNameWithEndpointId
            ? $"{EndpointUriKey}-{_jobName}"
            : _jobName;
        var triggerKey = new TriggerKey(effectiveJobName, _groupName);
        var jobKey = new JobKey(effectiveJobName, _groupName);

        if (_pauseJob)
        {
            // Pause instead of delete — trigger stays in scheduler for reuse
            if (await _scheduler.CheckExists(triggerKey, ct).ConfigureAwait(false))
            {
                await _scheduler.PauseTrigger(triggerKey, ct).ConfigureAwait(false);
            }
        }
        else if (_deleteJob)
        {
            // Unschedule trigger
            if (await _scheduler.CheckExists(triggerKey, ct).ConfigureAwait(false))
            {
                await _scheduler.UnscheduleJob(triggerKey, ct).ConfigureAwait(false);
            }

            // Delete job only if no more triggers reference it and not durable
            if (!_durableJob && await _scheduler.CheckExists(jobKey, ct).ConfigureAwait(false))
            {
                var triggers = await _scheduler.GetTriggersOfJob(jobKey, ct).ConfigureAwait(false);
                if (triggers.Count == 0)
                {
                    await _scheduler.DeleteJob(jobKey, ct).ConfigureAwait(false);
                }
            }
        }

        // Drain in-flight jobs
        var logger = (_endpoint.Component as ComponentBase)?.Logger;
        await DrainAwaiter.WaitAsync(
            () => Volatile.Read(ref _inflightCount),
            DrainTimeout, ct, logger, GetType().Name).ConfigureAwait(false);

        // Cancel any still-running processing
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = null;
    }

    /// <summary>
    /// Executes the consumer pipeline for a job fire. Called by <see cref="QuartzRouteJob"/>.
    /// </summary>
    /// <param name="jobContext">Quartz job execution context.</param>
    /// <param name="routeContext">The route context this job belongs to.</param>
    internal async Task ExecuteJob(IJobExecutionContext jobContext, IRouteContext routeContext)
    {
        var processingCt = _processingCts?.Token ?? CancellationToken.None;
        if (processingCt.IsCancellationRequested)
            return; // stop accepted, reject new jobs

        if (!await _semaphore.WaitAsync(0).ConfigureAwait(false))
            return; // all threads busy, skip this fire

        Interlocked.Increment(ref _inflightCount);
        try
        {
            var exchange = CreateExchange(jobContext);
            try
            {
                await _processor.Process(exchange, processingCt).ConfigureAwait(false);

                if (exchange.Exception != null && !exchange.ExceptionHandled)
                {
                    await routeContext.HandleException(exchange, exchange.Exception, processingCt).ConfigureAwait(false);
                }
            }
            finally
            {
                await exchange.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _semaphore.Release();
            Interlocked.Decrement(ref _inflightCount);
        }
    }

    private Exchange CreateExchange(IJobExecutionContext context)
    {
        var message = new Message { Body = null };
        var exchange = Exchange.Create(message, _endpoint.ScopeFactory);
        exchange.Pattern = ExchangePattern.InOnly;

        exchange.Properties["CamelQuartzFireTime"] = context.FireTimeUtc;
        exchange.Properties["CamelQuartzScheduledFireTime"] = context.ScheduledFireTimeUtc;
        exchange.Properties["CamelQuartzNextFireTime"] = context.NextFireTimeUtc;
        exchange.Properties["CamelQuartzPreviousFireTime"] = context.PreviousFireTimeUtc;

        PopulateExchangeProperties(exchange, context);
        return exchange;
    }

    /// <summary>
    /// Removes orphaned job/trigger from scheduler when the endpoint is no longer present.
    /// </summary>
    internal static async Task CleanupOrphanedJob(IScheduler scheduler, JobKey jobKey, TriggerKey triggerKey, CancellationToken ct = default)
    {
        if (await scheduler.CheckExists(triggerKey, ct).ConfigureAwait(false))
        {
            await scheduler.UnscheduleJob(triggerKey, ct).ConfigureAwait(false);
        }

        if (await scheduler.CheckExists(jobKey, ct).ConfigureAwait(false))
        {
            var triggers = await scheduler.GetTriggersOfJob(jobKey, ct).ConfigureAwait(false);
            if (triggers.Count == 0)
            {
                await scheduler.DeleteJob(jobKey, ct).ConfigureAwait(false);
            }
        }
    }
}
