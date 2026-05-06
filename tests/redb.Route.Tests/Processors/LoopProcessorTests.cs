using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="LoopProcessor"/>.</summary>
public class LoopProcessorTests
{
    /// <summary>Count-based loop executes N times.</summary>
    [Fact]
    public async Task Process_CountBased_ExecutesNTimes()
    {
        var count = 0;
        var body = new DelegateProcessor(_ => count++);
        var loop = new LoopProcessor(body, 5);

        await loop.Process(new Exchange());

        count.Should().Be(5);
    }

    /// <summary>Count zero executes zero times.</summary>
    [Fact]
    public async Task Process_CountZero_NoExecution()
    {
        var count = 0;
        var body = new DelegateProcessor(_ => count++);
        var loop = new LoopProcessor(body, 0);

        await loop.Process(new Exchange());

        count.Should().Be(0);
    }

    /// <summary>Predicate-based loop stops when false.</summary>
    [Fact]
    public async Task Process_PredicateBased_StopsWhenFalse()
    {
        var iterations = 0;
        var body = new DelegateProcessor(ex =>
        {
            iterations++;
            var current = (int)(ex.In.Body ?? 0);
            ex.In.Body = current + 1;
        });

        var loop = new LoopProcessor(body, ex => (int)(ex.In.Body ?? 0) < 3);

        var exchange = new Exchange(new Message(0));
        await loop.Process(exchange);

        iterations.Should().Be(3);
        exchange.In.Body.Should().Be(3);
    }

    /// <summary>Exchange.Stop() breaks the loop.</summary>
    [Fact]
    public async Task Process_ExchangeStopped_BreaksLoop()
    {
        var count = 0;
        var body = new DelegateProcessor(ex =>
        {
            count++;
            if (count >= 2) ex.Stop();
        });

        var loop = new LoopProcessor(body, 100);
        await loop.Process(new Exchange());

        count.Should().Be(2);
    }

    /// <summary>CancellationToken is respected.</summary>
    [Fact]
    public async Task Process_CancellationRespected()
    {
        using var cts = new CancellationTokenSource();
        var count = 0;
        var body = new DelegateProcessor((ex, ct) =>
        {
            count++;
            if (count >= 3) cts.Cancel();
            return Task.CompletedTask;
        });

        var loop = new LoopProcessor(body, 1000);

        var act = () => loop.Process(new Exchange(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        count.Should().BeGreaterThanOrEqualTo(3);
    }

    /// <summary>Negative maxIterations throws.</summary>
    [Fact]
    public void Constructor_NegativeCount_Throws()
    {
        var act = () => new LoopProcessor(new DelegateProcessor(_ => { }), -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Copy Mode ──

    /// <summary>Copy mode clones exchange each iteration — mutations don't accumulate.</summary>
    [Fact]
    public async Task Process_CopyMode_CountBased_IsolatesIterations()
    {
        var bodies = new List<int>();
        var body = new DelegateProcessor(ex =>
        {
            var val = (int)(ex.In.Body ?? 0);
            bodies.Add(val);
            ex.In.Body = val + 100; // mutate — should NOT carry to next iteration
        });

        var loop = new LoopProcessor(body, 3, copy: true);
        var exchange = new Exchange(new Message(1));
        await loop.Process(exchange);

        // Each iteration should see original body = 1
        bodies.Should().AllBeEquivalentTo(1);
        // Final exchange body = last iteration result (1 + 100)
        ((int)exchange.In.Body!).Should().Be(101);
    }

    /// <summary>Copy mode with predicate loop.</summary>
    [Fact]
    public async Task Process_CopyMode_PredicateBased_IsolatesIterations()
    {
        var iteration = 0;
        var body = new DelegateProcessor(ex =>
        {
            iteration++;
            ex.In.Body = iteration; // mutate
        });

        // Predicate checks original exchange body (always 0 < 3), 
        // but iteration counter grows externally
        var loop = new LoopProcessor(body, ex => iteration < 3, copy: true);
        var exchange = new Exchange(new Message(0));
        await loop.Process(exchange);

        iteration.Should().Be(3);
        // Last iteration wrote 3 to copy → merged back
        ((int)exchange.In.Body!).Should().Be(3);
    }

    /// <summary>Non-copy mode accumulates mutations across iterations.</summary>
    [Fact]
    public async Task Process_NoCopy_MutationsAccumulate()
    {
        var bodies = new List<int>();
        var body = new DelegateProcessor(ex =>
        {
            var val = (int)(ex.In.Body ?? 0);
            bodies.Add(val);
            ex.In.Body = val + 1;
        });

        var loop = new LoopProcessor(body, 3, copy: false);
        var exchange = new Exchange(new Message(0));
        await loop.Process(exchange);

        bodies.Should().BeEquivalentTo(new[] { 0, 1, 2 });
        ((int)exchange.In.Body!).Should().Be(3);
    }
}
