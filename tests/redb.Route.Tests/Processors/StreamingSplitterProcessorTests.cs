using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using FluentAssertions;

namespace redb.Route.Tests.Processors;

public class StreamingSplitterProcessorTests
{
    [Fact]
    public async Task Process_SplitsAsyncEnumerable_IntoIndividualExchanges()
    {
        var parts = new ConcurrentBag<object?>();
        var target = new DelegateProcessor(ex => parts.Add(ex.In.Body));

        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable("a", "b", "c"),
            target);

        await splitter.Process(new Exchange(new Message("source"))).ConfigureAwait(false);

        parts.Should().HaveCount(3);
        parts.Should().Contain("a").And.Contain("b").And.Contain("c");
    }

    [Fact]
    public async Task Process_EmptyEnumerable_ZeroExchanges()
    {
        var callCount = 0;
        var target = new DelegateProcessor(_ => Interlocked.Increment(ref callCount));

        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable<object>(),
            target);

        var exchange = new Exchange(new Message("source"));
        await splitter.Process(exchange).ConfigureAwait(false);

        callCount.Should().Be(0);
        exchange.Properties["CamelSplitSize"].Should().Be(0);
    }

    [Fact]
    public async Task Process_SetsSplitMetadataHeaders()
    {
        var indices = new ConcurrentBag<int>();
        var target = new DelegateProcessor(ex =>
            indices.Add((int)ex.In.Headers["CamelSplitIndex"]!));

        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable<object>(1, 2, 3),
            target);

        await splitter.Process(new Exchange(new Message("source"))).ConfigureAwait(false);

        indices.OrderBy(x => x).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task Process_SetsSplitSize()
    {
        var target = new DelegateProcessor(_ => { });
        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable<object>("a", "b", "c"),
            target);

        var exchange = new Exchange(new Message("source"));
        await splitter.Process(exchange).ConfigureAwait(false);

        exchange.Properties["CamelSplitSize"].Should().Be(3);
    }

    [Fact]
    public async Task Process_CopiesOriginalHeaders()
    {
        var capturedHeaders = new ConcurrentBag<string>();
        var target = new DelegateProcessor(ex =>
            capturedHeaders.Add((string)ex.In.Headers["OriginalKey"]!));

        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable<object>("x"),
            target);

        var msg = new Message("source");
        msg.Headers["OriginalKey"] = "OriginalValue";
        await splitter.Process(new Exchange(msg)).ConfigureAwait(false);

        capturedHeaders.Should().ContainSingle().Which.Should().Be("OriginalValue");
    }

    [Fact]
    public async Task StopOnException_True_StopsOnFirstError()
    {
        var processedCount = 0;
        var target = new DelegateProcessor(ex =>
        {
            Interlocked.Increment(ref processedCount);
            if ((string)ex.In.Body! == "b")
                throw new InvalidOperationException("fail on b");
        });

        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable<object>("a", "b", "c"),
            target,
            stopOnException: true);

        var exchange = new Exchange(new Message("source"));
        var act = () => splitter.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("fail on b").ConfigureAwait(false);

        processedCount.Should().BeLessThanOrEqualTo(2);
        exchange.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task StopOnException_False_ContinuesToEnd()
    {
        var processedParts = new ConcurrentBag<string>();
        var target = new DelegateProcessor(ex =>
        {
            var body = (string)ex.In.Body!;
            processedParts.Add(body);
            if (body == "b")
                throw new InvalidOperationException("fail on b");
        });

        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable<object>("a", "b", "c"),
            target,
            stopOnException: false);

        var exchange = new Exchange(new Message("source"));
        await splitter.Process(exchange).ConfigureAwait(false);

        processedParts.Should().HaveCount(3);
        processedParts.Should().Contain("a").And.Contain("b").And.Contain("c");
    }

    [Fact]
    public async Task Cancellation_StopsProcessing()
    {
        using var cts = new CancellationTokenSource();
        var processedCount = 0;
        var target = new DelegateProcessor(ex =>
        {
            if (Interlocked.Increment(ref processedCount) == 2)
                cts.Cancel();
        });

        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable<object>("a", "b", "c", "d", "e"),
            target);

        var act = () => splitter.Process(new Exchange(new Message("source")), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task IsStopped_BreaksSplitLoop()
    {
        var processedCount = 0;
        var exchange = new Exchange(new Message("source"));

        var target = new DelegateProcessor(ex =>
        {
            Interlocked.Increment(ref processedCount);
            if ((string)ex.In.Body! == "b")
                exchange.Stop(); // stop the parent exchange
        });

        var splitter = new StreamingSplitterProcessor(
            _ => ToAsyncEnumerable<object>("a", "b", "c", "d", "e"),
            target);

        await splitter.Process(exchange).ConfigureAwait(false);

        // "a" processed, then "b" stops parent → loop breaks before "c"
        processedCount.Should().BeLessThanOrEqualTo(2);
    }

    private static async IAsyncEnumerable<object?> ToAsyncEnumerable<T>(
        T[] items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return item;
        }
    }

    private static IAsyncEnumerable<object?> ToAsyncEnumerable<T>(params T[] items)
        => ToAsyncEnumerable(items, CancellationToken.None);
}
