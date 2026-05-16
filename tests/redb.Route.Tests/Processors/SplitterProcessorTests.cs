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
}
