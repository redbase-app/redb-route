using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

public class CronBuilderTests
{
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Schedule_StartsWithCronScheme()
    {
        var uri = Cron.Schedule("job1", "0/5 * * * * ?").Build();
        uri.Should().StartWith("cron:job1?");
    }

    [Fact]
    public void Schedule_ContainsEncodedExpression()
    {
        var uri = Cron.Schedule("job1", "0/5 * * * * ?").Build();
        uri.Should().Contain("schedule=");
    }

    [Fact]
    public void NullName_Throws()
    {
        var act = () => Cron.Schedule(null!, "0/5 * * * * ?");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NullSchedule_Throws()
    {
        var act = () => Cron.Schedule("job1", null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyName_Throws()
    {
        var act = () => Cron.Schedule("", "0/5 * * * * ?");
        act.Should().Throw<ArgumentException>();
    }

    // ── Round-trip guard ─────────────────────────────────────────────
    // Build() must survive EndpointUriParser (the ecosystem reader). Regression: HttpUtility.UrlEncode
    // wrote a space as '+', but the parser's Uri.UnescapeDataString leaves '+' as-is — so every
    // space-bearing cron expression came back corrupted and CronEndpointOptions.Validate() threw inside
    // context.Start(), taking down the whole module. Uri.EscapeDataString writes %20, which round-trips.

    [Theory]
    [InlineData("0 */5 * * * ?")]
    [InlineData("0/30 * * * * ?")]
    [InlineData("0 0 12 ? * MON-FRI")]
    [InlineData("0 15 10 ? * 6L")]
    public void Build_ScheduleRoundTripsThroughParser(string schedule)
    {
        var parsed = EndpointUriParser.Parse(Cron.Schedule("job1", schedule).Build());
        parsed.RawParameters["schedule"].Should().Be(schedule);
    }

    // ── Params ──────────────────────────────────────────────────────

    [Fact]
    public void Threads_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").Threads(4).Build();
        uri.Should().Contain("threads=4");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = Cron.Schedule("job1", "0/5 * * * * ?").Threads(2);
        uri.Should().StartWith("cron:job1?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = Cron.Schedule("j", "0/5 * * * * ?").Threads(2);
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = Cron.Schedule("job1", "0/5 * * * * ?").Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("cron");
        parsed.Path.Should().Be("job1");
        parsed.RawParameters.Should().ContainKey("schedule");
    }

    // ── Group/name in path ──────────────────────────────────────────

    [Fact]
    public void GroupAndName_EncodesInPath()
    {
        var uri = Cron.Schedule("billing/invoice", "0 0 2 * * ?").Build();
        uri.Should().StartWith("cron:billing/invoice?");
    }

    // ── Job lifecycle params ────────────────────────────────────────

    [Fact]
    public void DeleteJob_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").DeleteJob(false).Build();
        uri.Should().Contain("deleteJob=false");
    }

    [Fact]
    public void PauseJob_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").PauseJob().Build();
        uri.Should().Contain("pauseJob=true");
    }

    [Fact]
    public void DurableJob_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").DurableJob().Build();
        uri.Should().Contain("durableJob=true");
    }

    [Fact]
    public void Stateful_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").Stateful().Build();
        uri.Should().Contain("stateful=true");
    }

    [Fact]
    public void Recoverable_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").Recoverable().Build();
        uri.Should().Contain("recoverableJob=true");
    }

    [Fact]
    public void PrefixJobNameWithEndpointId_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").PrefixJobNameWithEndpointId().Build();
        uri.Should().Contain("prefixJobNameWithEndpointId=true");
    }

    // ── Trigger timing params ───────────────────────────────────────

    [Fact]
    public void MisfireInstruction_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?")
            .MisfireInstruction(QuartzMisfirePolicy.CronDoNothing).Build();
        uri.Should().Contain("misfireInstruction=CronDoNothing");
    }

    [Fact]
    public void TimeZone_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").TimeZone("Europe/Moscow").Build();
        uri.Should().Contain("timeZone=Europe/Moscow");
    }

    [Fact]
    public void StartAt_String_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").StartAt("2025-01-01T00:00:00Z").Build();
        uri.Should().Contain("startAt=");
    }

    [Fact]
    public void EndAt_String_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").EndAt("2025-12-31T23:59:59Z").Build();
        uri.Should().Contain("endAt=");
    }

    [Fact]
    public void CustomCalendar_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").CustomCalendar("holidays").Build();
        uri.Should().Contain("customCalendar=holidays");
    }

    [Fact]
    public void TriggerStartDelay_SetsParam()
    {
        var uri = Cron.Schedule("j", "0/5 * * * * ?").TriggerStartDelay(2000).Build();
        uri.Should().Contain("triggerStartDelay=2000");
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_AllParams()
    {
        var uri = Cron.Schedule("billing/invoice", "0 0 2 * * ?")
            .Threads(4)
            .DeleteJob(false)
            .DurableJob()
            .Stateful()
            .Recoverable()
            .MisfireInstruction(QuartzMisfirePolicy.CronFireOnceNow)
            .TimeZone("Europe/Moscow")
            .CustomCalendar("holidays")
            .TriggerStartDelay(1000)
            .Build();

        uri.Should().StartWith("cron:billing/invoice?");
        uri.Should().Contain("threads=4");
        uri.Should().Contain("deleteJob=false");
        uri.Should().Contain("durableJob=true");
        uri.Should().Contain("stateful=true");
        uri.Should().Contain("recoverableJob=true");
        uri.Should().Contain("misfireInstruction=CronFireOnceNow");
        uri.Should().Contain("timeZone=Europe/Moscow");
        uri.Should().Contain("customCalendar=holidays");
        uri.Should().Contain("triggerStartDelay=1000");
    }

    // ── Round-trip with new params ──────────────────────────────────

    [Fact]
    public void RoundTrip_GroupName_ParsedCorrectly()
    {
        var original = Cron.Schedule("billing/invoice", "0 0 2 * * ?")
            .Stateful().Recoverable().Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("cron");
        parsed.Path.Should().Be("billing/invoice");
        parsed.RawParameters["stateful"].Should().Be("true");
        parsed.RawParameters["recoverableJob"].Should().Be("true");
    }
}

public class QTimerBuilderTests
{
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Every_StartsWithQTimerScheme()
    {
        var uri = QTimer.Every("heartbeat").Build();
        uri.Should().StartWith("qtimer:heartbeat");
    }

    [Fact]
    public void NullName_Throws()
    {
        var act = () => QTimer.Every(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyName_Throws()
    {
        var act = () => QTimer.Every("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Params ──────────────────────────────────────────────────────

    [Fact]
    public void Period_SetsParam()
    {
        var uri = QTimer.Every("hb").Period(5000).Build();
        uri.Should().Contain("period=5000");
    }

    [Fact]
    public void Delay_SetsParam()
    {
        var uri = QTimer.Every("hb").Delay(1000).Build();
        uri.Should().Contain("delay=1000");
    }

    [Fact]
    public void FixedRate_SetsParam()
    {
        var uri = QTimer.Every("hb").FixedRate().Build();
        uri.Should().Contain("fixedRate=true");
    }

    [Fact]
    public void Threads_SetsParam()
    {
        var uri = QTimer.Every("hb").Threads(3).Build();
        uri.Should().Contain("threads=3");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = QTimer.Every("hb").Period(5000).Delay(1000);
        uri.Should().StartWith("qtimer:hb?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = QTimer.Every("hb").Period(5000).FixedRate();
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_BuildsCorrectUri()
    {
        var uri = QTimer.Every("heartbeat")
            .Period(5000)
            .Delay(1000)
            .FixedRate()
            .Threads(2)
            .Build();

        uri.Should().StartWith("qtimer:heartbeat?");
        uri.Should().Contain("period=5000");
        uri.Should().Contain("delay=1000");
        uri.Should().Contain("fixedRate=true");
        uri.Should().Contain("threads=2");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = QTimer.Every("hb").Period(5000).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("qtimer");
        parsed.Path.Should().Be("hb");
        parsed.RawParameters["period"].Should().Be("5000");
    }

    // ── Group/name in path ──────────────────────────────────────────

    [Fact]
    public void GroupAndName_EncodesInPath()
    {
        var uri = QTimer.Every("monitoring/heartbeat").Build();
        uri.Should().StartWith("qtimer:monitoring/heartbeat");
    }

    // ── Job lifecycle params ────────────────────────────────────────

    [Fact]
    public void DeleteJob_SetsParam()
    {
        var uri = QTimer.Every("hb").DeleteJob(false).Build();
        uri.Should().Contain("deleteJob=false");
    }

    [Fact]
    public void PauseJob_SetsParam()
    {
        var uri = QTimer.Every("hb").PauseJob().Build();
        uri.Should().Contain("pauseJob=true");
    }

    [Fact]
    public void DurableJob_SetsParam()
    {
        var uri = QTimer.Every("hb").DurableJob().Build();
        uri.Should().Contain("durableJob=true");
    }

    [Fact]
    public void Stateful_SetsParam()
    {
        var uri = QTimer.Every("hb").Stateful().Build();
        uri.Should().Contain("stateful=true");
    }

    [Fact]
    public void Recoverable_SetsParam()
    {
        var uri = QTimer.Every("hb").Recoverable().Build();
        uri.Should().Contain("recoverableJob=true");
    }

    [Fact]
    public void PrefixJobNameWithEndpointId_SetsParam()
    {
        var uri = QTimer.Every("hb").PrefixJobNameWithEndpointId().Build();
        uri.Should().Contain("prefixJobNameWithEndpointId=true");
    }

    // ── Trigger timing params ───────────────────────────────────────

    [Fact]
    public void MisfireInstruction_SetsParam()
    {
        var uri = QTimer.Every("hb")
            .MisfireInstruction(QuartzMisfirePolicy.SimpleFireNow).Build();
        uri.Should().Contain("misfireInstruction=SimpleFireNow");
    }

    [Fact]
    public void RepeatCount_SetsParam()
    {
        var uri = QTimer.Every("hb").RepeatCount(10).Build();
        uri.Should().Contain("repeatCount=10");
    }

    [Fact]
    public void StartAt_SetsParam()
    {
        var uri = QTimer.Every("hb").StartAt("2025-06-01T00:00:00Z").Build();
        uri.Should().Contain("startAt=");
    }

    [Fact]
    public void EndAt_SetsParam()
    {
        var uri = QTimer.Every("hb").EndAt("2025-12-31T23:59:59Z").Build();
        uri.Should().Contain("endAt=");
    }

    [Fact]
    public void CustomCalendar_SetsParam()
    {
        var uri = QTimer.Every("hb").CustomCalendar("businessDays").Build();
        uri.Should().Contain("customCalendar=businessDays");
    }

    // ── Full chain with all params ──────────────────────────────────

    [Fact]
    public void FullChain_AllNewParams()
    {
        var uri = QTimer.Every("monitoring/heartbeat")
            .Period(5000)
            .Delay(1000)
            .FixedRate()
            .Threads(2)
            .DeleteJob(false)
            .DurableJob()
            .Stateful()
            .Recoverable()
            .MisfireInstruction(QuartzMisfirePolicy.SimpleFireNow)
            .RepeatCount(100)
            .CustomCalendar("holidays")
            .Build();

        uri.Should().StartWith("qtimer:monitoring/heartbeat?");
        uri.Should().Contain("period=5000");
        uri.Should().Contain("delay=1000");
        uri.Should().Contain("fixedRate=true");
        uri.Should().Contain("threads=2");
        uri.Should().Contain("deleteJob=false");
        uri.Should().Contain("durableJob=true");
        uri.Should().Contain("stateful=true");
        uri.Should().Contain("recoverableJob=true");
        uri.Should().Contain("misfireInstruction=SimpleFireNow");
        uri.Should().Contain("repeatCount=100");
        uri.Should().Contain("customCalendar=holidays");
    }

    // ── Round-trip with new params ──────────────────────────────────

    [Fact]
    public void RoundTrip_GroupName_ParsedCorrectly()
    {
        var original = QTimer.Every("monitoring/heartbeat")
            .Period(5000).Stateful().Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("qtimer");
        parsed.Path.Should().Be("monitoring/heartbeat");
        parsed.RawParameters["stateful"].Should().Be("true");
    }
}
