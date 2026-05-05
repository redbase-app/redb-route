using System.Diagnostics;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="ThrottleProcessor"/>.</summary>
public class ThrottleProcessorTests
{
    [Fact]
    public async Task Process_SingleMessage_PassesThrough()
    {
        var processed = false;
        var next = new DelegateProcessor(_ => processed = true);
        var throttle = new ThrottleProcessor(next, maxPerPeriod: 10);

        await throttle.Process(new Exchange(new Message("hello")));

        processed.Should().BeTrue();
    }

    [Fact]
    public async Task Process_UnderLimit_AllPassImmediately()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        var throttle = new ThrottleProcessor(next, maxPerPeriod: 5, period: TimeSpan.FromSeconds(2));

        for (int i = 0; i < 5; i++)
            await throttle.Process(new Exchange(new Message(i)));

        count.Should().Be(5);
    }

    [Fact]
    public async Task Process_ExceedLimit_ThrottlesExcess()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        var throttle = new ThrottleProcessor(next, maxPerPeriod: 2, period: TimeSpan.FromMilliseconds(500));

        var sw = Stopwatch.StartNew();
        // Send 3 messages — first 2 pass immediately, 3rd must wait for a slot
        var t1 = throttle.Process(new Exchange());
        var t2 = throttle.Process(new Exchange());
        await Task.WhenAll(t1, t2);
        var fast = sw.ElapsedMilliseconds;

        await throttle.Process(new Exchange());
        sw.Stop();

        count.Should().Be(3);
        // Third message should have been delayed
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400); // ~500ms period
    }

    [Fact]
    public async Task Process_PreservesExchangeBody()
    {
        object? captured = null;
        var next = new DelegateProcessor(ex => captured = ex.In.Body);
        var throttle = new ThrottleProcessor(next, maxPerPeriod: 10);

        await throttle.Process(new Exchange(new Message("payload")));

        captured.Should().Be("payload");
    }

    [Fact]
    public void Constructor_NullNext_Throws()
    {
        var act = () => new ThrottleProcessor(null!, 5);
        act.Should().Throw<ArgumentNullException>().WithParameterName("next");
    }

    [Fact]
    public void Constructor_ZeroMax_Throws()
    {
        var act = () => new ThrottleProcessor(new DelegateProcessor(_ => { }), 0);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxPerPeriod");
    }

    [Fact]
    public void Constructor_NegativeMax_Throws()
    {
        var act = () => new ThrottleProcessor(new DelegateProcessor(_ => { }), -1);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxPerPeriod");
    }

    [Fact]
    public async Task Process_Cancellation_ThrowsOperationCanceled()
    {
        var next = new DelegateProcessor(_ => { });
        var throttle = new ThrottleProcessor(next, maxPerPeriod: 1, period: TimeSpan.FromSeconds(10));
        // Exhaust the single slot
        await throttle.Process(new Exchange());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = () => throttle.Process(new Exchange(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Process_ConcurrentAccess_ThreadSafe()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        var throttle = new ThrottleProcessor(next, maxPerPeriod: 50, period: TimeSpan.FromSeconds(2));

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => throttle.Process(new Exchange()))
            .ToArray();

        await Task.WhenAll(tasks);
        count.Should().Be(50);
    }
}
