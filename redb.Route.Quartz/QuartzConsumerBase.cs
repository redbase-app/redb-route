using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Quartz;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Quartz;

/// <summary>
/// Base class for Quartz-based consumers. Handles shared scheduler lifecycle,
/// job creation, orphan cleanup, and concurrency control via semaphore.
/// Subclasses provide trigger configuration (simple schedule vs cron expression).
/// </summary>
public abstract class QuartzConsumerBase : IConsumer
{
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

    // Set by Start: the store is shared with the next start or the other nodes (it decides what Stop may touch), the
    // claim on the job key in the registry, and the context served (for a collision message).
    private bool _persistentStore;
    private string? _registryKey;
    private string? _contextId;

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
        var logger = (_endpoint.Component as ComponentBase)?.Logger;

        // The context's scheduler (handed in, or the context's own) — started once the context has started.
        var holder = QuartzContextScheduler.For(routeContext, logger);
        _scheduler = await holder.GetAsync(ct).ConfigureAwait(false);
        var metaData = await _scheduler.GetMetaData(ct).ConfigureAwait(false);
        _persistentStore = metaData.JobStoreSupportsPersistence;

        var effectiveJobName = _prefixJobNameWithEndpointId
            ? $"{EndpointUriKey}-{_jobName}"
            : _jobName;
        var jobKey = new JobKey(effectiveJobName, _groupName);

        // One job key, one consumer per scheduler — claimed before the scheduler is touched, so a second claimant
        // fails instead of replacing the first one's schedule (AddJob replace + RescheduleJob below).
        _registryKey = QuartzConsumerRegistry.KeyOf(_scheduler, jobKey);
        if (!QuartzConsumerRegistry.TryRegister(_registryKey, this, out var owner))
        {
            _registryKey = null;
            throw new InvalidOperationException(
                $"Quartz job '{jobKey}' of {EndpointUri.Sanitize(EndpointUriKey)} (context '{routeContext.ContextId}') is " +
                $"already scheduled by {EndpointUri.Sanitize(owner.EndpointUriKey)} (context '{owner.ContextId}') on the " +
                $"same scheduler. One job key, one schedule: give it its own name (another path, or jobName=/groupName=).");
        }

        try
        {
            WarnOnConfiguration(routeContext, metaData, logger);

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

            var jobBuilder = _stateful
                ? JobBuilder.Create<QuartzStatefulRouteJob>()
                : JobBuilder.Create<QuartzRouteJob>();

            var job = jobBuilder
                .WithIdentity(jobKey)
                .UsingJobData("endpointUri", EndpointUriKey)
                // For the log: the job is found by its key, not by the context id (another node writes its own).
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

            _contextId = routeContext.ContextId;

            // A route started in a running context starts the scheduler itself; otherwise the context's start does.
            await holder.StartIfContextRunningAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            QuartzConsumerRegistry.Unregister(_registryKey, this);
            _registryKey = null;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task Stop(CancellationToken ct = default)
    {
        // Unregister first: a fire from now on finds no consumer on this node
        if (_registryKey is not null)
        {
            QuartzConsumerRegistry.Unregister(_registryKey, this);
            _registryKey = null;
        }

        if (_scheduler == null || _scheduler.IsShutdown)
            return;

        var effectiveJobName = _prefixJobNameWithEndpointId
            ? $"{EndpointUriKey}-{_jobName}"
            : _jobName;
        var triggerKey = new TriggerKey(effectiveJobName, _groupName);
        var jobKey = new JobKey(effectiveJobName, _groupName);

        if (_persistentStore)
        {
            // The job and its trigger live in a store the next start of this node, or the other nodes of a cluster,
            // share: this node stopping its consumer is not a decision about the schedule. deleteJob / pauseJob apply to
            // an in-memory store only; a schedule is paused or removed for the cluster explicitly.
        }
        else if (_pauseJob)
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

    /// <summary>Context id of the route this consumer serves, for the collision message of another claimant.</summary>
    internal string? ContextId => _contextId;

    private void WarnOnConfiguration(IRouteContext routeContext, SchedulerMetaData metaData, ILogger? logger)
    {
        if (logger is null)
            return;

        if (metaData.JobStoreSupportsPersistence
            && (_endpoint.Uri.RawParameters.ContainsKey("deleteJob") || _endpoint.Uri.RawParameters.ContainsKey("pauseJob")))
        {
            logger.LogWarning("{Endpoint}: deleteJob / pauseJob do not apply to a persistent job store — stopping a route on one node does " +
                "not delete or pause the schedule the store shares. Pause or remove it explicitly.",
                EndpointUri.Sanitize(EndpointUriKey));
        }

        // A clustered store already fires a job on one node of the cluster; a leader-only route policy on top keeps the
        // consumer on the leader alone, and a fire taken by another node finds no consumer there and is lost. A policy
        // factory that declines the route (Tsak's does for Quartz routes) leaves it on every node: nothing to warn about.
        if (metaData.JobStoreClustered
            && routeContext.Routes.FirstOrDefault(r => ReferenceEquals(r.Consumer, this))
                is { PolicyDescriptor: { RequestedCluster: true, EffectivePolicy: not "AllNodes" } } route)
        {
            logger.LogWarning("Route '{RouteId}': .Cluster(true) on a Quartz consumer with a clustered job store — Quartz already fires " +
                "the job on one node; with the route on the leader only, a fire taken by another node is lost. Drop .Cluster(true).",
                route.RouteId);
        }
    }

    /// <summary>
    /// Executes the consumer pipeline for a job fire. Called by <see cref="QuartzRouteJob"/>.
    /// </summary>
    /// <param name="jobContext">Quartz job execution context.</param>
    internal async Task ExecuteJob(IJobExecutionContext jobContext)
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
            // A fire carries no trace context: its span is a root, never a child of the scheduler thread's activity.
            using var span = RouteTelemetryExtensions.StartConsumerSpan<object?>(
                (_endpoint.Component as ComponentBase)?.Context,
                $"{_endpoint.Component.Scheme} receive", ActivityKind.Consumer, "redb.system", _endpoint.Component.Scheme,
                _endpoint.Uri.NormalizedKey, null, static (_, _) => null, operation: "receive");
            try
            {
                await _processor.Process(exchange, processingCt).ConfigureAwait(false);

                // The route's error scope has run inside the pipeline — its OnException blocks, its builder's, and the
                // context's handlers (a host's dead letter queue among them) for a failure none took. A failure left on
                // the exchange is the outcome of the fire: recorded, not handled again. As DrainableConsumer.
                // Our own stop is not a failure of the fire; any other cancellation is.
                if (exchange.Exception != null && !exchange.ExceptionHandled
                    && (exchange.Exception is not OperationCanceledException || !processingCt.IsCancellationRequested))
                    span.Activity.RecordFailure(exchange.Exception);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !processingCt.IsCancellationRequested)
            {
                span.Activity.RecordFailure(ex);
                throw;
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
