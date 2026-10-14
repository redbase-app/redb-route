using System.Collections.Specialized;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Quartz;

/// <summary>
/// The scheduler of one route context, as Apache Camel's <c>QuartzComponent</c> keeps one per CamelContext. A service of
/// the context, created by its first Quartz consumer:
/// <list type="bullet">
/// <item>an <c>IScheduler</c> handed to the context is used as it is — its host owns it;</item>
/// <item>otherwise the context gets its own, built from <see cref="QuartzSchedulerOptions"/> (in-memory without them),
/// named <c>{prefix}-{context id}</c>, and shut down when the context stops;</item>
/// <item>either is started once the context has started — every consumer registered before the first fire (camel-quartz
/// <c>autoStartScheduler</c>) — or at once for a consumer started later in a running context.</item>
/// </list>
/// </summary>
internal sealed class QuartzContextScheduler : IRouteLifecycleListener
{
    private static readonly object HolderLock = new();

    private readonly IRouteContext _context;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IScheduler? _scheduler;
    private bool _owned;

    private QuartzContextScheduler(IRouteContext context, ILogger? logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>The scheduler holder of <paramref name="context"/>, created and registered on first use.</summary>
    internal static QuartzContextScheduler For(IRouteContext context, ILogger? logger)
    {
        lock (HolderLock)
        {
            if (context.GetService<QuartzContextScheduler>() is { } existing)
                return existing;
            var holder = new QuartzContextScheduler(context, logger);
            context.AddService(typeof(QuartzContextScheduler), holder);
            context.AddLifecycleListener(holder);
            return holder;
        }
    }

    /// <summary>
    /// The context's scheduler, not started by this call. A handed-in one that has been shut down is refused; the
    /// context's own is built anew after the context stopped it.
    /// </summary>
    internal async Task<IScheduler> GetAsync(CancellationToken ct)
    {
        var scheduler = await ResolveAsync(ct).ConfigureAwait(false);
        // The job Quartz instantiates has no container to take a logger from: it finds one here.
        if (_logger is not null)
            scheduler.Context.Put(QuartzRouteJob.LoggerKey, _logger);
        return scheduler;
    }

    private async Task<IScheduler> ResolveAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_scheduler is { IsShutdown: false })
                return _scheduler;

            // A handed-in scheduler is any IScheduler of the context that is not the one built here (that one stays
            // registered after the context shut it down, and is built anew below).
            if (_context.GetService<IScheduler>() is { } handedIn && !ReferenceEquals(handedIn, _scheduler))
            {
                if (handedIn.IsShutdown)
                    throw new InvalidOperationException("The Quartz scheduler handed to the route context has been shut down.");
                _scheduler = handedIn;
                _owned = false;
                return handedIn;
            }

            _scheduler = await CreateOwnAsync(ct).ConfigureAwait(false);
            _owned = true;
            // Visible as the context's IScheduler, as it always was; the holder still knows it is its own.
            _context.AddService(typeof(IScheduler), _scheduler);
            return _scheduler;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Whether the context owns its scheduler (built here) rather than a host.</summary>
    internal bool Owned => _owned;

    /// <summary>Starts the scheduler when the context is already running — a consumer started after the context.</summary>
    internal Task StartIfContextRunningAsync(CancellationToken ct)
        => _context.IsStarted ? StartAsync(ct) : Task.CompletedTask;

    /// <inheritdoc />
    public Task OnContextStarted(IRouteContext context, CancellationToken ct) => StartAsync(ct);

    /// <inheritdoc />
    public async Task OnContextStopped(IRouteContext context, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The consumers have stopped and drained; the context's own scheduler goes with the context. A handed-in
            // one belongs to its host.
            if (_owned && _scheduler is { IsShutdown: false } own)
            {
                await own.Shutdown(waitForJobsToComplete: false, ct).ConfigureAwait(false);
                _logger?.LogDebug("Quartz scheduler {Scheduler} of context {Context} shut down.", own.SchedulerName, context.ContextId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_scheduler is { IsStarted: false, IsShutdown: false } scheduler)
                await scheduler.Start(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IScheduler> CreateOwnAsync(CancellationToken ct)
    {
        var options = _context.Resolve<QuartzSchedulerOptions>();
        var properties = new NameValueCollection();
        if (options is not null)
        {
            foreach (var (key, value) in options.Properties)
                properties[key] = value;
        }
        properties["quartz.scheduler.instanceName"] = $"{options?.InstanceNamePrefix ?? "redb-route"}-{_context.ContextId}";

        var scheduler = await new StdSchedulerFactory(properties).GetScheduler(ct).ConfigureAwait(false);
        var metaData = await scheduler.GetMetaData(ct).ConfigureAwait(false);
        if (metaData.JobStoreSupportsPersistence && (_context as RouteContext)?.IsContextIdGenerated == true)
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"The Quartz job store of context '{_context.ContextId}' is persistent, but the context id was generated: " +
                "the scheduler name would differ on every start and every node, and its jobs could not be found again. " +
                "Give the route context an id.");
        }
        return scheduler;
    }
}
