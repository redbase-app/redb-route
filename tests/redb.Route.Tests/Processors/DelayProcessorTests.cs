using FluentAssertions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="DelayProcessor"/>.</summary>
public class DelayProcessorTests
{
    /// <summary>Delay actually waits approximately the specified time.</summary>
    [Fact]
    public async Task Process_DelaysApproximately()
    {
        var processor = new DelayProcessor(TimeSpan.FromMilliseconds(100));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await processor.Process(new Exchange());

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(80));
    }

    /// <summary>Zero delay completes immediately.</summary>
    [Fact]
    public async Task Process_ZeroDelay_Immediate()
    {
        var processor = new DelayProcessor(TimeSpan.Zero);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await processor.Process(new Exchange());

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(50));
    }

    /// <summary>Cancellation during delay throws OperationCanceledException.</summary>
    [Fact]
    public async Task Process_Cancelled_Throws()
    {
        var processor = new DelayProcessor(TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => processor.Process(new Exchange(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Negative delay throws ArgumentOutOfRangeException.</summary>
    [Fact]
    public void Constructor_NegativeDelay_Throws()
    {
        var act = () => new DelayProcessor(TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Delay property returns configured value.</summary>
    [Fact]
    public void Delay_ReturnsConfigured()
    {
        var delay = TimeSpan.FromMilliseconds(250);
        var processor = new DelayProcessor(delay);
        processor.Delay.Should().Be(delay);
    }
}
