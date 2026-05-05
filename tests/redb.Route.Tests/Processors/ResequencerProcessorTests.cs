using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="ResequencerProcessor"/>.</summary>
public class ResequencerProcessorTests
{
    [Fact]
    public async Task Process_BatchComplete_FlushesInOrder()
    {
        var order = new List<long>();
        var next = new DelegateProcessor(ex => order.Add((long)ex.In.Body!));
        var reseq = new ResequencerProcessor(next,
            keySelector: ex => (long)ex.In.Body!,
            batchSize: 3);

        // Send out-of-order: 3, 1, 2
        await reseq.Process(new Exchange(new Message(3L)));
        await reseq.Process(new Exchange(new Message(1L)));
        await reseq.Process(new Exchange(new Message(2L))); // triggers flush at batchSize=3

        order.Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task Process_BelowBatchSize_DoesNotFlush()
    {
        var order = new List<long>();
        var next = new DelegateProcessor(ex => order.Add((long)ex.In.Body!));
        var reseq = new ResequencerProcessor(next,
            keySelector: ex => (long)ex.In.Body!,
            batchSize: 5);

        await reseq.Process(new Exchange(new Message(2L)));
        await reseq.Process(new Exchange(new Message(1L)));

        order.Should().BeEmpty("batch not full yet");
        (await reseq.GetBufferedCount()).Should().Be(2);
    }

    [Fact]
    public async Task Flush_DrainsPendingBuffer()
    {
        var order = new List<long>();
        var next = new DelegateProcessor(ex => order.Add((long)ex.In.Body!));
        var reseq = new ResequencerProcessor(next,
            keySelector: ex => (long)ex.In.Body!,
            batchSize: 100);

        await reseq.Process(new Exchange(new Message(5L)));
        await reseq.Process(new Exchange(new Message(2L)));
        await reseq.Process(new Exchange(new Message(8L)));

        order.Should().BeEmpty();

        await reseq.Flush();

        order.Should().Equal(2L, 5L, 8L);
        (await reseq.GetBufferedCount()).Should().Be(0);
    }

    [Fact]
    public async Task Flush_EmptyBuffer_DoesNothing()
    {
        var callCount = 0;
        var next = new DelegateProcessor(_ => callCount++);
        var reseq = new ResequencerProcessor(next,
            keySelector: ex => 0,
            batchSize: 10);

        await reseq.Flush();
        callCount.Should().Be(0);
    }

    [Fact]
    public async Task Process_MultipleBatches_EachFlushesIndependently()
    {
        var batches = new List<List<long>>();
        var currentBatch = new List<long>();
        var next = new DelegateProcessor(ex =>
        {
            currentBatch.Add((long)ex.In.Body!);
        });
        var reseq = new ResequencerProcessor(next,
            keySelector: ex => (long)ex.In.Body!,
            batchSize: 2);

        // Batch 1: 4, 1 → sorted: 1, 4
        await reseq.Process(new Exchange(new Message(4L)));
        await reseq.Process(new Exchange(new Message(1L)));
        batches.Add([.. currentBatch]);
        currentBatch.Clear();

        // Batch 2: 3, 2 → sorted: 2, 3
        await reseq.Process(new Exchange(new Message(3L)));
        await reseq.Process(new Exchange(new Message(2L)));
        batches.Add([.. currentBatch]);

        batches[0].Should().Equal(1L, 4L);
        batches[1].Should().Equal(2L, 3L);
    }

    [Fact]
    public async Task Process_DuplicateKeys_AllProcessed()
    {
        var bodies = new List<string>();
        var next = new DelegateProcessor(ex => bodies.Add((string)ex.In.Body!));
        var reseq = new ResequencerProcessor(next,
            keySelector: _ => 1L, // Same key for all
            batchSize: 3);

        await reseq.Process(new Exchange(new Message("a")));
        await reseq.Process(new Exchange(new Message("b")));
        await reseq.Process(new Exchange(new Message("c")));

        bodies.Should().HaveCount(3);
    }

    [Fact]
    public void Constructor_NullNext_Throws()
    {
        var act = () => new ResequencerProcessor(null!, _ => 0);
        act.Should().Throw<ArgumentNullException>().WithParameterName("next");
    }

    [Fact]
    public void Constructor_NullKeySelector_Throws()
    {
        var act = () => new ResequencerProcessor(new DelegateProcessor(_ => { }), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("keySelector");
    }

    [Fact]
    public void Constructor_ZeroBatchSize_Throws()
    {
        var act = () => new ResequencerProcessor(new DelegateProcessor(_ => { }), _ => 0, batchSize: 0);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("batchSize");
    }

    [Fact]
    public async Task GetBufferedCount_ReflectsCurrentState()
    {
        var next = new DelegateProcessor(_ => { });
        var reseq = new ResequencerProcessor(next, _ => 1, batchSize: 10);

        (await reseq.GetBufferedCount()).Should().Be(0);
        await reseq.Process(new Exchange());
        (await reseq.GetBufferedCount()).Should().Be(1);
        await reseq.Process(new Exchange());
        (await reseq.GetBufferedCount()).Should().Be(2);
    }

    [Fact]
    public async Task Process_StoppedExchange_StopsProcessingBatch()
    {
        var processed = new List<long>();
        var next = new DelegateProcessor(ex =>
        {
            processed.Add((long)ex.In.Body!);
            if ((long)ex.In.Body! == 2L)
                ex.Stop();
        });
        var reseq = new ResequencerProcessor(next,
            keySelector: ex => (long)ex.In.Body!,
            batchSize: 3);

        await reseq.Process(new Exchange(new Message(3L)));
        await reseq.Process(new Exchange(new Message(1L)));
        await reseq.Process(new Exchange(new Message(2L)));

        // Sorted: 1, 2, 3. Stop at 2 → should process 1, 2 but not 3
        processed.Should().Equal(1L, 2L);
    }
}
