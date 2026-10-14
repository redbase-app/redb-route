namespace redb.Route.Quartz;

/// <summary>
/// How the Quartz component builds the scheduler of a route context — the store, the cluster, the serializer, the
/// thread pool — when nobody handed the context a ready <c>IScheduler</c>. Register it on the context
/// (<c>AddService</c>) or in DI; the component then creates one scheduler per context, as Apache Camel's
/// <c>QuartzComponent</c> does per CamelContext, named <c>{InstanceNamePrefix}-{context id}</c>.
/// <para>
/// A host shares the options, not a scheduler: each context gets a job namespace of its own (two packages that both
/// say <c>cron://nightly</c> do not collide), and in a clustered store a context's cluster is made of the nodes that
/// run that context. A context without a <c>cron:</c>/<c>qtimer:</c> route creates no scheduler at all.
/// </para>
/// </summary>
public sealed class QuartzSchedulerOptions
{
    /// <summary>Prefix of the scheduler name; the context id follows it. Default <c>redb-route</c>.</summary>
    public string InstanceNamePrefix { get; init; } = "redb-route";

    /// <summary>
    /// Quartz properties of the scheduler (<c>quartz.jobStore.type</c>, <c>quartz.jobStore.clustered</c>,
    /// <c>quartz.dataSource.*</c>, <c>quartz.serializer.type</c>, <c>quartz.threadPool.maxConcurrency</c>...).
    /// <c>quartz.scheduler.instanceName</c> is set by the component.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}
