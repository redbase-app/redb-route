using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Quartz;

namespace redb.Route.Quartz;

/// <summary>
/// Universal Quartz job that bridges between Quartz scheduler and redb.Route consumers.
/// Created via reflection by Quartz. Finds the running consumer by the job it claimed on this scheduler
/// (<see cref="QuartzConsumerRegistry"/>) and delegates execution. Concurrency is controlled by the consumer's
/// semaphore, NOT by Quartz's [DisallowConcurrentExecution] — this allows configurable threads &gt; 1.
/// <para>
/// A fire with no consumer on this node: in an in-memory store the job is this process's own and is cleaned up; in a
/// persistent store it belongs to the store — the next start of this node or another node of the cluster runs it (a
/// rolling upgrade, a route stopped on one node) — so it is skipped and logged once, never deleted.
/// </para>
/// </summary>
public class QuartzRouteJob : IJob
{
    /// <summary>Key of the logger the context's scheduler holder leaves in the scheduler context.</summary>
    internal const string LoggerKey = "redb.Route.Quartz.Logger";

    private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.Ordinal);

    /// <summary>Parameterless constructor required by Quartz reflection-based instantiation.</summary>
    public QuartzRouteJob() { }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        var consumer = QuartzConsumerRegistry.Get(context.Scheduler, context.JobDetail.Key);
        if (consumer is not null)
        {
            // The consumer's semaphore + processor pipeline; the route's error scope runs inside it.
            await consumer.ExecuteJob(context).ConfigureAwait(false);
            return;
        }

        var metaData = await context.Scheduler.GetMetaData(context.CancellationToken).ConfigureAwait(false);
        if (!metaData.JobStoreSupportsPersistence)
        {
            // In-memory: the job is this process's own and its consumer is gone — clean up job + trigger.
            await QuartzConsumerBase.CleanupOrphanedJob(
                context.Scheduler, context.JobDetail.Key, context.Trigger.Key).ConfigureAwait(false);
            return;
        }

        if (Warned.TryAdd(QuartzConsumerRegistry.KeyOf(context.Scheduler, context.JobDetail.Key), 0)
            && context.Scheduler.Context.Get(LoggerKey) is ILogger logger)
        {
            logger.LogWarning(
                "Quartz job '{Job}' fired on node {Node} of scheduler {Scheduler}, which runs no consumer for it; the fire is " +
                "skipped and the job kept in the store. Nodes that share a scheduler name must carry the same schedules.",
                context.JobDetail.Key, context.Scheduler.SchedulerInstanceId, context.Scheduler.SchedulerName);
        }
    }
}

/// <summary>
/// Stateful variant of <see cref="QuartzRouteJob"/>. Persists JobDataMap after execution
/// and disallows concurrent execution (single-threaded at Quartz scheduler level).
/// Used when <c>stateful=true</c> is set on the endpoint.
/// </summary>
[DisallowConcurrentExecution]
[PersistJobDataAfterExecution]
public class QuartzStatefulRouteJob : QuartzRouteJob
{
}
