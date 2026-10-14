using Quartz;
using Quartz.Impl;
using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// Tests for QuartzRouteJob — the universal Quartz IJob that bridges to consumers.
/// </summary>
public class QuartzRouteJobTests
{
    [Fact]
    public async Task Execute_NoConsumerRegistered_CleansUpOrphan()
    {
        var job = new QuartzRouteJob();
        var scheduler = await StdSchedulerFactory.GetDefaultScheduler();
        await scheduler.Start();

        try
        {
            var jobDetail = JobBuilder.Create<QuartzRouteJob>()
                .UsingJobData("endpointUri", "orphan://test")
                .UsingJobData("contextId", "orphan-ctx")
                .Build();

            var trigger = TriggerBuilder.Create().StartNow().Build();
            var context = new TestJobExecutionContext(scheduler, jobDetail, trigger);

            // No consumer registered, no context — should not throw, just clean up
            var act = () => job.Execute(context);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }

    /// <summary>
    /// Minimal IJobExecutionContext implementation for testing QuartzRouteJob.Execute() directly.
    /// </summary>
    private sealed class TestJobExecutionContext : IJobExecutionContext
    {
        public TestJobExecutionContext(IScheduler scheduler, IJobDetail jobDetail, ITrigger trigger)
        {
            Scheduler = scheduler;
            JobDetail = jobDetail;
            Trigger = trigger;
            FireTimeUtc = DateTimeOffset.UtcNow;
            ScheduledFireTimeUtc = DateTimeOffset.UtcNow;
        }

        public IScheduler Scheduler { get; }
        public ITrigger Trigger { get; }
        public ICalendar? Calendar => null;
        public bool Recovering => false;
        public TriggerKey RecoveringTriggerKey => null!;
        public int RefireCount => 0;
        public JobDataMap MergedJobDataMap => JobDetail.JobDataMap;
        public IJobDetail JobDetail { get; }
        public IJob JobInstance => null!;
        public DateTimeOffset FireTimeUtc { get; }
        public DateTimeOffset? ScheduledFireTimeUtc { get; }
        public DateTimeOffset? PreviousFireTimeUtc => null;
        public DateTimeOffset? NextFireTimeUtc => null;
        public string FireInstanceId => "test-fire-id";
        public object? Result { get; set; }
        public TimeSpan JobRunTime => TimeSpan.Zero;
        public CancellationToken CancellationToken => CancellationToken.None;

        public void Put(object key, object objectValue) { }
        public object? Get(object key) => null;
    }
}
