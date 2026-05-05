using Quartz;
using redb.Route.Abstractions;

namespace redb.Route.Quartz;

/// <summary>
/// Universal Quartz job that bridges between Quartz scheduler and redb.Route consumers.
/// Created via reflection by Quartz. Resolves RouteContext from scheduler context,
/// finds the running consumer instance via <see cref="QuartzConsumerRegistry"/>,
/// and delegates execution. Concurrency is controlled by the consumer's semaphore,
/// NOT by Quartz's [DisallowConcurrentExecution] — this allows configurable threads &gt; 1
/// and is cluster-friendly (cluster support in a separate project).
/// </summary>
public class QuartzRouteJob : IJob
{
    private const string SchedulerContextKey = "redb.Route.Quartz.RouteContext";

    /// <summary>Parameterless constructor required by Quartz reflection-based instantiation.</summary>
    public QuartzRouteJob() { }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        var endpointUri = context.JobDetail.JobDataMap.GetString("endpointUri");
        var contextId = context.JobDetail.JobDataMap.GetString("contextId");

        if (string.IsNullOrEmpty(endpointUri) || string.IsNullOrEmpty(contextId))
        {
            throw new InvalidOperationException(
                "QuartzRouteJob requires 'endpointUri' and 'contextId' in JobDataMap.");
        }

        // 1. Find the running consumer instance via Registry
        var consumer = QuartzConsumerRegistry.Get(endpointUri);
        if (consumer == null)
        {
            // Consumer unregistered (route stopped) — clean up orphaned job + trigger
            await QuartzConsumerBase.CleanupOrphanedJob(
                context.Scheduler, context.JobDetail.Key, context.Trigger.Key).ConfigureAwait(false);
            return;
        }

        // 2. Resolve RouteContext from scheduler context (stored during consumer Start)
        var routeContext = context.Scheduler.Context.Get($"{SchedulerContextKey}_{contextId}") as IRouteContext;
        if (routeContext == null)
        {
            // Context gone — clean up orphaned job
            await QuartzConsumerBase.CleanupOrphanedJob(
                context.Scheduler, context.JobDetail.Key, context.Trigger.Key).ConfigureAwait(false);
            return;
        }

        // 3. Delegate to the consumer (semaphore + processor pipeline + exception handling)
        await consumer.ExecuteJob(context, routeContext).ConfigureAwait(false);
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
