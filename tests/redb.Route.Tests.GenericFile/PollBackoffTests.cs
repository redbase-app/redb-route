using FluentAssertions;
using redb.Route.GenericFile;
using Xunit;

namespace redb.Route.Tests.GenericFile;

/// <summary>
/// Unit tests for the pure poll-backoff decision (no timers, no I/O): thresholds arm the skip countdown,
/// exactly <c>multiplier</c> polls are skipped, then counters reset. Mirrors Apache Camel semantics.
/// </summary>
public class PollBackoffTests
{
    private static int SkipsFor(PollBackoff b)
    {
        var skips = 0;
        while (b.ShouldSkip()) skips++;
        return skips;
    }

    [Fact]
    public void Error_Threshold_Arms_Then_Skips_Exactly_Multiplier_Then_Resets()
    {
        var b = new PollBackoff(multiplier: 3, idleThreshold: 0, errorThreshold: 2);

        b.Record(PollOutcome.Error).Should().BeNull("one error is below the threshold of 2");
        SkipsFor(b).Should().Be(0);

        b.Record(PollOutcome.Error).Should().NotBeNull("the second error hits the threshold");

        SkipsFor(b).Should().Be(3, "the multiplier is 3");

        // After the skips the counters reset, so a fresh error is below the threshold again.
        b.Record(PollOutcome.Error).Should().BeNull();
        SkipsFor(b).Should().Be(0);
    }

    [Fact]
    public void Idle_Threshold_Arms_The_Skip()
    {
        var b = new PollBackoff(multiplier: 2, idleThreshold: 3, errorThreshold: 0);

        b.Record(PollOutcome.Idle).Should().BeNull();
        b.Record(PollOutcome.Idle).Should().BeNull();
        b.Record(PollOutcome.Idle).Should().NotBeNull("the third idle hits the threshold of 3");

        SkipsFor(b).Should().Be(2);
    }

    [Fact]
    public void Success_Resets_The_Error_Count()
    {
        var b = new PollBackoff(multiplier: 5, idleThreshold: 0, errorThreshold: 2);

        b.Record(PollOutcome.Error);
        b.Record(PollOutcome.Success).Should().BeNull("success clears the error count");
        b.Record(PollOutcome.Error).Should().BeNull("only one error since the reset — below threshold 2");
        SkipsFor(b).Should().Be(0);
    }

    [Fact]
    public void Error_Resets_The_Idle_Count()
    {
        var b = new PollBackoff(multiplier: 2, idleThreshold: 2, errorThreshold: 0);

        b.Record(PollOutcome.Idle);
        b.Record(PollOutcome.Error).Should().BeNull("error has no error-threshold and clears idle");
        b.Record(PollOutcome.Idle).Should().BeNull("idle count restarted — one idle, below threshold 2");
        SkipsFor(b).Should().Be(0);
    }

    [Fact]
    public void Trigger_Reports_Reason_And_Skip_Count()
    {
        var byError = new PollBackoff(1, 0, 1).Record(PollOutcome.Error);
        byError!.Value.ByError.Should().BeTrue();
        byError.Value.Skips.Should().Be(1);

        var byIdle = new PollBackoff(4, 1, 0).Record(PollOutcome.Idle);
        byIdle!.Value.ByError.Should().BeFalse();
        byIdle.Value.Skips.Should().Be(4);
    }

    [Fact]
    public void Classify_MapsTallyToOutcome()
    {
        // No exchange → idle, regardless of the flag.
        PollBackoff.Classify(new PollTally(0, 0), false).Should().Be(PollOutcome.Idle);
        PollBackoff.Classify(new PollTally(0, 0), true).Should().Be(PollOutcome.Idle);

        // Some created → success by default (Camel parity), even if all failed.
        PollBackoff.Classify(new PollTally(2, 2), false).Should().Be(PollOutcome.Success);

        // All created failed + flag → error (the Camel-superset behaviour).
        PollBackoff.Classify(new PollTally(2, 2), true).Should().Be(PollOutcome.Error);

        // Partial success + flag → success (progress was made, don't back off).
        PollBackoff.Classify(new PollTally(2, 1), true).Should().Be(PollOutcome.Success);
    }

    [Theory]
    [InlineData(0, 0, 0)]   // nothing configured
    [InlineData(0, 2, 3)]   // thresholds but no multiplier
    [InlineData(3, 0, 0)]   // multiplier but no threshold
    public void Disabled_Never_Skips_And_Ignores_Outcomes(int mult, int idle, int error)
    {
        var b = new PollBackoff(mult, idle, error);
        b.Enabled.Should().BeFalse();

        for (var i = 0; i < 10; i++)
        {
            b.Record(PollOutcome.Error).Should().BeNull();
            b.Record(PollOutcome.Idle).Should().BeNull();
            b.ShouldSkip().Should().BeFalse();
        }
    }
}
