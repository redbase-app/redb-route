namespace redb.Route.Quartz;

/// <summary>
/// Misfire handling strategies for Quartz triggers.
/// Maps to Quartz.NET <c>MisfireInstruction</c> constants.
/// </summary>
public enum QuartzMisfirePolicy
{
    /// <summary>Use the default misfire policy for the trigger type.</summary>
    Default = 0,

    // ── SimpleTrigger ──

    /// <summary>SimpleTrigger: fire now, then continue schedule. Default for simple triggers.</summary>
    SimpleFireNow = 1,

    /// <summary>SimpleTrigger: reschedule to now with the original repeat count.</summary>
    SimpleRescheduleNowWithExistingRepeatCount = 2,

    /// <summary>SimpleTrigger: reschedule to now with remaining repeat count.</summary>
    SimpleRescheduleNowWithRemainingRepeatCount = 3,

    /// <summary>SimpleTrigger: reschedule to next scheduled time with remaining count.</summary>
    SimpleRescheduleNextWithRemainingCount = 4,

    /// <summary>SimpleTrigger: reschedule to next scheduled time with existing count.</summary>
    SimpleRescheduleNextWithExistingCount = 5,

    // ── CronTrigger ──

    /// <summary>CronTrigger: fire once now, then continue schedule. Default for cron triggers.</summary>
    CronFireOnceNow = 101,

    /// <summary>CronTrigger: do nothing, wait for next scheduled time.</summary>
    CronDoNothing = 102,
}
