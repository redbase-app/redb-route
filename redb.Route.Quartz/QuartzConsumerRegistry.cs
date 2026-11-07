using System.Collections.Concurrent;
using Quartz;

namespace redb.Route.Quartz;

/// <summary>
/// The running Quartz consumers of this process, by the job they claimed on their scheduler — the key Quartz fires by.
/// One job key, one consumer per scheduler: <see cref="TryRegister"/> refuses a second claimant, so two routes never
/// share (and silently replace) one schedule. The same key on another node of a cluster is not a claimant here —
/// it is that node's own consumer of the same schedule.
/// </summary>
internal static class QuartzConsumerRegistry
{
    private static readonly ConcurrentDictionary<string, QuartzConsumerBase> Consumers = new(StringComparer.Ordinal);

    /// <summary>The registry key of <paramref name="jobKey"/> on <paramref name="scheduler"/> (this process's instance of it).</summary>
    internal static string KeyOf(IScheduler scheduler, JobKey jobKey)
        => $"{scheduler.SchedulerName}|{scheduler.SchedulerInstanceId}|{jobKey.Group}|{jobKey.Name}";

    /// <summary>Claims <paramref name="key"/> for <paramref name="consumer"/>; <c>false</c> with the current owner when taken.</summary>
    internal static bool TryRegister(string key, QuartzConsumerBase consumer, out QuartzConsumerBase owner)
    {
        owner = Consumers.GetOrAdd(key, consumer);
        return ReferenceEquals(owner, consumer);
    }

    /// <summary>Releases <paramref name="key"/> if <paramref name="consumer"/> holds it.</summary>
    internal static void Unregister(string key, QuartzConsumerBase consumer)
        => Consumers.TryRemove(new KeyValuePair<string, QuartzConsumerBase>(key, consumer));

    /// <summary>The consumer that claimed <paramref name="jobKey"/> on <paramref name="scheduler"/>, or <c>null</c>.</summary>
    internal static QuartzConsumerBase? Get(IScheduler scheduler, JobKey jobKey)
        => Consumers.TryGetValue(KeyOf(scheduler, jobKey), out var consumer) ? consumer : null;
}
