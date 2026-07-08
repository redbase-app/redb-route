using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="SplitterProcessor"/>.</summary>
public class SplitterProcessorTests
{
    /// <summary>Splits array body into individual exchanges.</summary>
    [Fact]
    public async Task Process_SplitsArray_IntoIndividualExchanges()
    {
        var parts = new List<object?>();
        var target = new DelegateProcessor(ex => parts.Add(ex.In.Body));

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target);

        var data = new object[] { "a", "b", "c" };
        await splitter.Process(new Exchange(new Message(data)));

        parts.Should().Equal("a", "b", "c");
    }

    /// <summary>Split metadata headers are set correctly.</summary>
    [Fact]
    public async Task Process_SetsSplitMetadataHeaders()
    {
        var indices = new List<int>();
        var sizes = new List<int>();
        var completes = new List<bool>();

        var target = new DelegateProcessor(ex =>
        {
            indices.Add((int)ex.In.Headers["CamelSplitIndex"]!);
            sizes.Add((int)ex.In.Headers["CamelSplitSize"]!);
            completes.Add((bool)ex.In.Headers["CamelSplitComplete"]!);
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target);

        await splitter.Process(new Exchange(new Message(new object[] { 1, 2, 3 })));

        indices.Should().Equal(0, 1, 2);
        sizes.Should().OnlyContain(s => s == 3);
        completes.Should().Equal(false, false, true);
    }

    /// <summary>Original headers are copied to split exchanges.</summary>
    [Fact]
    public async Task Process_CopiesHeaders()
    {
        string? capturedHeader = null;
        var target = new DelegateProcessor(ex =>
        {
            capturedHeader = ex.In.GetHeader<string>("source");
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target);

        var msg = new Message(new object[] { "x" });
        msg.Headers["source"] = "test-source";
        await splitter.Process(new Exchange(msg));

        capturedHeader.Should().Be("test-source");
    }

    /// <summary>Properties are copied to split exchanges.</summary>
    [Fact]
    public async Task Process_CopiesProperties()
    {
        object? capturedProp = null;
        var target = new DelegateProcessor(ex =>
        {
            capturedProp = ex.Properties["routeId"];
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target);

        var exchange = new Exchange(new Message(new object[] { "x" }));
        exchange.Properties["routeId"] = "route-1";
        await splitter.Process(exchange);

        capturedProp.Should().Be("route-1");
    }

    /// <summary>Exchange.Stop() breaks splitting early.</summary>
    [Fact]
    public async Task Process_ExchangeStopped_BreaksSplit()
    {
        var count = 0;
        var exchange = new Exchange(new Message(new object[] { 1, 2, 3, 4, 5 }));

        var target = new DelegateProcessor(_ =>
        {
            count++;
            if (count >= 2) exchange.Stop();
        });

        var splitter = new SplitterProcessor(
            ex => ((IEnumerable<object?>)ex.In.Body!),
            target);

        await splitter.Process(exchange);

        count.Should().Be(2);
    }

    /// <summary>Empty split produces no exchanges.</summary>
    [Fact]
    public async Task Process_EmptySplit_NoProcessing()
    {
        var count = 0;
        var target = new DelegateProcessor(_ => count++);
        var splitter = new SplitterProcessor(_ => Enumerable.Empty<object?>(), target);

        await splitter.Process(new Exchange());

        count.Should().Be(0);
    }

    // ── Parallel Error Handling ──

    /// <summary>Single parallel split failure: exchange.Exception is that single exception.</summary>
    [Fact]
    public async Task ProcessParallel_SingleFailure_SetsExchangeException()
    {
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor((ex, _) =>
            {
                if ((string)ex.In.Body! == "bad")
                    throw new InvalidOperationException("split-fail");
                return Task.CompletedTask;
            }),
            parallelProcessing: true,
            stopOnException: false);

        var exchange = new Exchange(new Message(new object[] { "good", "bad" }));
        await splitter.Process(exchange);

        exchange.Exception.Should().BeOfType<InvalidOperationException>();
    }

    /// <summary>Multiple parallel split failures produce AggregateException.</summary>
    [Fact]
    public async Task ProcessParallel_MultipleFailures_SetsAggregateException()
    {
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor((ex, _) => throw new InvalidOperationException($"fail-{ex.In.Body}")),
            parallelProcessing: true,
            stopOnException: false);

        var exchange = new Exchange(new Message(new object[] { "a", "b", "c" }));
        await splitter.Process(exchange);

        exchange.Exception.Should().BeOfType<AggregateException>();
        var agg = (AggregateException)exchange.Exception!;
        agg.InnerExceptions.Should().HaveCount(3);
    }

    /// <summary>stopOnException=true rethrows in parallel mode.</summary>
    [Fact]
    public async Task ProcessParallel_StopOnException_Rethrows()
    {
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor((_, _) => throw new InvalidOperationException("stop")),
            parallelProcessing: true,
            stopOnException: true);

        var exchange = new Exchange(new Message(new object[] { "a" }));
        var act = async () => await splitter.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stop");
    }

    /// <summary>Sequential stopOnException stops processing remaining parts.</summary>
    [Fact]
    public async Task ProcessSequential_StopOnException_SkipsRemaining()
    {
        var processed = new List<string>();
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor((ex, _) =>
            {
                var body = (string)ex.In.Body!;
                processed.Add(body);
                if (body == "b") throw new InvalidOperationException("seq-stop");
                return Task.CompletedTask;
            }),
            parallelProcessing: false,
            stopOnException: true);

        var exchange = new Exchange(new Message(new object[] { "a", "b", "c" }));
        var act = async () => await splitter.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("seq-stop");
        processed.Should().Equal("a", "b"); // "c" was skipped
    }

    // ── Aggregation Strategy: Camel-compatible null-old contract ──

    /// <summary>T6.1 Sequential aggregation: first part goes through strategy with oldExchange=null.</summary>
    [Fact]
    public async Task Sequential_AggregationStrategy_FirstPartGoesThroughStrategy_WithNullOld()
    {
        var calls = new List<(IExchange? Old, object? NewBody)>();
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor(_ => { }),
            parallelProcessing: false,
            aggregationStrategy: (old, cur) =>
            {
                calls.Add((old, cur.In.Body));
                if (old == null) return cur;
                return old;
            });

        await splitter.Process(new Exchange(new Message(new object[] { 10, 20, 30 })));

        calls.Should().HaveCount(3);
        calls[0].Old.Should().BeNull();
        calls[0].NewBody.Should().Be(10);
        calls[1].Old.Should().NotBeNull();
        calls[2].Old.Should().NotBeNull();
    }

    /// <summary>T6.2 Parallel aggregation: first part goes through strategy with oldExchange=null.</summary>
    [Fact]
    public async Task Parallel_AggregationStrategy_FirstPartGoesThroughStrategy_WithNullOld()
    {
        var calls = new List<(IExchange? Old, object? NewBody)>();
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor(_ => { }),
            parallelProcessing: true,
            aggregationStrategy: (old, cur) =>
            {
                calls.Add((old, cur.In.Body));
                if (old == null) return cur;
                return old;
            });

        await splitter.Process(new Exchange(new Message(new object[] { 10, 20, 30 })));

        // Deterministic post-pass over split exchanges in input order.
        calls.Should().HaveCount(3);
        calls[0].Old.Should().BeNull();
        calls[0].NewBody.Should().Be(10);
        calls[1].NewBody.Should().Be(20);
        calls[2].NewBody.Should().Be(30);
    }

    /// <summary>T6.3 Parallel + stopOnException: failure skips final aggregation (no partial result on exchange).</summary>
    [Fact]
    public async Task ProcessParallel_StopOnException_SkipsFinalAggregation()
    {
        var aggCalls = 0;
        var originalBody = new object[] { 1, 2, 3 };
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor((ex, _) =>
            {
                if ((int)ex.In.Body! == 2) throw new InvalidOperationException("boom");
                return Task.CompletedTask;
            }),
            parallelProcessing: true,
            aggregationStrategy: (old, cur) =>
            {
                Interlocked.Increment(ref aggCalls);
                return cur;
            },
            stopOnException: true);

        var exchange = new Exchange(new Message(originalBody));
        var act = async () => await splitter.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>();
        aggCalls.Should().Be(0); // ApplyAggregation skipped entirely
        exchange.In.Body.Should().BeSameAs(originalBody);
    }

    /// <summary>T6.4 Sequential + stopOnException: failure leaves exchange body untouched (no partial aggregation visible).</summary>
    [Fact]
    public async Task ProcessSequential_StopOnException_DoesNotPublishPartialAggregation()
    {
        var originalBody = new object[] { 1, 2, 3 };
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor((ex, _) =>
            {
                if ((int)ex.In.Body! == 2) throw new InvalidOperationException("boom");
                return Task.CompletedTask;
            }),
            parallelProcessing: false,
            aggregationStrategy: (old, cur) =>
            {
                if (old == null) return cur;
                return old;
            },
            stopOnException: true);

        var exchange = new Exchange(new Message(originalBody));
        var act = async () => await splitter.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>();
        exchange.In.Body.Should().BeSameAs(originalBody); // ApplyAggregation skipped
    }

    /// <summary>T6.5 parallelAggregate=true: aggregation runs in-task under lock; counter sees all parts.</summary>
    [Fact]
    public async Task ProcessParallel_WithParallelAggregate_AggregatesAllPartsThreadSafely()
    {
        var aggCalls = 0;
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor(_ => { }),
            parallelProcessing: true,
            maxDegreeOfParallelism: 4,
            aggregationStrategy: (old, cur) =>
            {
                aggCalls++; // safe: under processor's lock when parallelAggregate=true
                return old ?? cur;
            },
            stopOnException: false,
            parallelAggregate: true);

        var parts = Enumerable.Range(1, 20).Cast<object?>().ToArray();
        await splitter.Process(new Exchange(new Message(parts)));

        aggCalls.Should().Be(20);
    }

    /// <summary>T6.6 aggregateOnException=true: failed split parts still feed into the strategy.</summary>
    [Fact]
    public async Task ProcessSequential_AggregateOnException_FeedsFailedPartsToStrategy()
    {
        var seenBodies = new List<object?>();
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor((ex, _) =>
            {
                if ((int)ex.In.Body! == 1) throw new InvalidOperationException("boom");
                return Task.CompletedTask;
            }),
            parallelProcessing: false,
            aggregationStrategy: (old, cur) =>
            {
                seenBodies.Add(cur.In.Body);
                return old ?? cur;
            },
            stopOnException: false,
            aggregateOnException: true);

        await splitter.Process(new Exchange(new Message(new object[] { 0, 1, 2 })));

        seenBodies.Should().Equal(0, 1, 2); // failed part (1) included
    }
}
