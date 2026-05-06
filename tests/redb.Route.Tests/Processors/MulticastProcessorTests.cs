using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="MulticastProcessor"/>.</summary>
public class MulticastProcessorTests
{
    /// <summary>Parallel processing sends clones to all targets.</summary>
    [Fact]
    public async Task Process_Parallel_AllTargetsReceiveClones()
    {
        var bodies = new System.Collections.Concurrent.ConcurrentBag<object?>();
        var multicast = new MulticastProcessor(parallelProcessing: true)
            .AddTarget(new DelegateProcessor(ex => bodies.Add(ex.In.Body)))
            .AddTarget(new DelegateProcessor(ex => bodies.Add(ex.In.Body)));

        await multicast.Process(new Exchange(new Message("data")));

        bodies.Should().HaveCount(2);
        bodies.Should().OnlyContain(b => (string)b! == "data");
    }

    /// <summary>Sequential processing preserves order.</summary>
    [Fact]
    public async Task Process_Sequential_PreservesOrder()
    {
        var order = new List<int>();
        var multicast = new MulticastProcessor(parallelProcessing: false)
            .AddTarget(new DelegateProcessor(_ => order.Add(1)))
            .AddTarget(new DelegateProcessor(_ => order.Add(2)));

        await multicast.Process(new Exchange());

        order.Should().Equal(1, 2);
    }

    /// <summary>Each target gets an independent clone — mutations don't leak.</summary>
    [Fact]
    public async Task Process_Clones_AreIndependent()
    {
        IExchange? clone1 = null;
        IExchange? clone2 = null;

        var multicast = new MulticastProcessor(parallelProcessing: false)
            .AddTarget(new DelegateProcessor(ex => { clone1 = ex; ex.In.Body = "modified-1"; }))
            .AddTarget(new DelegateProcessor(ex => { clone2 = ex; }));

        var original = new Exchange(new Message("original"));
        await multicast.Process(original);

        clone1!.In.Body.Should().Be("modified-1");
        clone2!.In.Body.Should().Be("original"); // Not affected by clone1
        original.In.Body.Should().Be("original"); // Original unchanged
    }

    /// <summary>Aggregation strategy is called pair-wise for each clone result.</summary>
    [Fact]
    public async Task Process_WithAggregation_MergesResults()
    {
        var multicast = new MulticastProcessor(
            parallelProcessing: false,
            aggregationStrategy: (aggregated, current) =>
            {
                aggregated.In.Body = (int)aggregated.In.Body! + (int)current.In.Body!;
                return aggregated;
            })
            .AddTarget(new DelegateProcessor(ex => ex.In.Body = 10))
            .AddTarget(new DelegateProcessor(ex => ex.In.Body = 20));

        var exchange = new Exchange(new Message(0));
        await multicast.Process(exchange);

        exchange.In.Body.Should().Be(30);
    }

    /// <summary>Empty multicast does nothing.</summary>
    [Fact]
    public async Task Process_NoTargets_DoesNothing()
    {
        var multicast = new MulticastProcessor();
        await multicast.Process(new Exchange());
        // No exception = success
    }

    /// <summary>Targets property returns all added processors.</summary>
    [Fact]
    public void Targets_ReturnsAll()
    {
        var multicast = new MulticastProcessor()
            .AddTarget(new DelegateProcessor(_ => { }))
            .AddTarget(new DelegateProcessor(_ => { }));

        multicast.Targets.Should().HaveCount(2);
    }

    // ── Parallel Error Handling ──

    /// <summary>Single parallel failure: exchange.Exception is the single exception.</summary>
    [Fact]
    public async Task ProcessParallel_SingleFailure_SetsExchangeException()
    {
        var multicast = new MulticastProcessor(parallelProcessing: true, stopOnException: false)
            .AddTarget(new DelegateProcessor(_ => { }))
            .AddTarget(new DelegateProcessor((_, _) => throw new InvalidOperationException("boom")));

        var exchange = new Exchange(new Message("data"));
        await multicast.Process(exchange);

        exchange.Exception.Should().BeOfType<InvalidOperationException>();
        exchange.Exception!.Message.Should().Be("boom");
    }

    /// <summary>Multiple parallel failures: exchange.Exception is AggregateException.</summary>
    [Fact]
    public async Task ProcessParallel_MultipleFailures_SetsAggregateException()
    {
        var multicast = new MulticastProcessor(parallelProcessing: true, stopOnException: false)
            .AddTarget(new DelegateProcessor((_, _) => throw new InvalidOperationException("err1")))
            .AddTarget(new DelegateProcessor((_, _) => throw new ArgumentException("err2")))
            .AddTarget(new DelegateProcessor(_ => { }));

        var exchange = new Exchange(new Message("data"));
        await multicast.Process(exchange);

        exchange.Exception.Should().BeOfType<AggregateException>();
        var agg = (AggregateException)exchange.Exception!;
        agg.InnerExceptions.Should().HaveCount(2);
        agg.InnerExceptions.Should().Contain(e => e is InvalidOperationException);
        agg.InnerExceptions.Should().Contain(e => e is ArgumentException);
    }

    /// <summary>stopOnException=true rethrows the first exception.</summary>
    [Fact]
    public async Task ProcessParallel_StopOnException_Rethrows()
    {
        var multicast = new MulticastProcessor(parallelProcessing: true, stopOnException: true)
            .AddTarget(new DelegateProcessor((_, _) => throw new InvalidOperationException("stop")))
            .AddTarget(new DelegateProcessor(_ => { }));

        var exchange = new Exchange(new Message("data"));
        var act = async () => await multicast.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stop");
    }

    // ── Sequential Error Handling ──

    /// <summary>Sequential failure with stopOnException sets exchange.Exception and rethrows.</summary>
    [Fact]
    public async Task ProcessSequential_StopOnException_SetsExceptionAndThrows()
    {
        var count = 0;
        var multicast = new MulticastProcessor(parallelProcessing: false, stopOnException: true)
            .AddTarget(new DelegateProcessor(_ => count++))
            .AddTarget(new DelegateProcessor((_, _) => throw new InvalidOperationException("seq-fail")))
            .AddTarget(new DelegateProcessor(_ => count++));

        var exchange = new Exchange(new Message("data"));
        var act = async () => await multicast.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("seq-fail");
        count.Should().Be(1); // Third target was skipped
    }

    /// <summary>Sequential without stopOnException continues and stores exception.</summary>
    [Fact]
    public async Task ProcessSequential_NoStop_ContinuesAfterFailure()
    {
        var count = 0;
        var multicast = new MulticastProcessor(parallelProcessing: false, stopOnException: false)
            .AddTarget(new DelegateProcessor(_ => count++))
            .AddTarget(new DelegateProcessor((_, _) => throw new InvalidOperationException("e1")))
            .AddTarget(new DelegateProcessor(_ => count++));

        var exchange = new Exchange(new Message("data"));
        await multicast.Process(exchange);

        count.Should().Be(2); // All non-failing targets executed
    }
}
