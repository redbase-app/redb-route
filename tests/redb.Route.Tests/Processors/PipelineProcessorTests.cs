using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="PipelineProcessor"/>.</summary>
public class PipelineProcessorTests
{
    /// <summary>Processors execute in order.</summary>
    [Fact]
    public async Task Process_ExecutesInOrder()
    {
        var order = new List<int>();

        var pipeline = new PipelineProcessor();
        pipeline.Add(new DelegateProcessor(_ => order.Add(1)));
        pipeline.Add(new DelegateProcessor(_ => order.Add(2)));
        pipeline.Add(new DelegateProcessor(_ => order.Add(3)));

        await pipeline.Process(new Exchange());

        order.Should().Equal(1, 2, 3);
    }

    /// <summary>Empty pipeline completes without error.</summary>
    [Fact]
    public async Task Process_EmptyPipeline_Succeeds()
    {
        var pipeline = new PipelineProcessor();
        var exchange = new Exchange();
        await pipeline.Process(exchange);

        exchange.IsStopped.Should().BeFalse();
    }

    /// <summary>Stops when exchange.Stop() is called.</summary>
    [Fact]
    public async Task Process_StoppedExchange_BreaksEarly()
    {
        var order = new List<int>();

        var pipeline = new PipelineProcessor();
        pipeline.Add(new DelegateProcessor(ex => { order.Add(1); ex.Stop(); }));
        pipeline.Add(new DelegateProcessor(_ => order.Add(2)));

        await pipeline.Process(new Exchange());

        order.Should().Equal(1);
    }

    /// <summary>Respects CancellationToken — throws on cancellation.</summary>
    [Fact]
    public async Task Process_CancellationRequested_Throws()
    {
        var pipeline = new PipelineProcessor();
        pipeline.Add(new DelegateProcessor(_ => { }));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => pipeline.Process(new Exchange(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>AddRange adds multiple processors at once.</summary>
    [Fact]
    public async Task AddRange_AddsMultiple()
    {
        var order = new List<int>();
        var pipeline = new PipelineProcessor();
        pipeline.AddRange([
            new DelegateProcessor(_ => order.Add(1)),
            new DelegateProcessor(_ => order.Add(2))
        ]);

        await pipeline.Process(new Exchange());

        order.Should().Equal(1, 2);
        pipeline.Processors.Should().HaveCount(2);
    }

    /// <summary>Exception in a processor propagates.</summary>
    [Fact]
    public async Task Process_ExceptionPropagates()
    {
        var pipeline = new PipelineProcessor();
        pipeline.Add(new DelegateProcessor(_ => throw new InvalidOperationException("boom")));
        pipeline.Add(new DelegateProcessor(_ => { }));

        var act = () => pipeline.Process(new Exchange());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    /// <summary>Out→In merge copies ContentType.</summary>
    [Fact]
    public async Task Process_OutToInMerge_CopiesContentType()
    {
        var pipeline = new PipelineProcessor();
        pipeline.Add(new DelegateProcessor(ex =>
        {
            var outMsg = new Message("response") { ContentType = "application/xml" };
            outMsg.Headers["extra"] = "val";
            ex.Out = outMsg;
        }));
        // Second processor sees merged In
        pipeline.Add(new DelegateProcessor(ex =>
        {
            ex.In.Body.Should().Be("response");
            ex.In.ContentType.Should().Be("application/xml");
            ex.In.Headers["extra"].Should().Be("val");
            ex.HasOut.Should().BeFalse();
        }));

        var exchange = new Exchange(new Message("original") { ContentType = "text/plain" });
        await pipeline.Process(exchange);

        exchange.In.ContentType.Should().Be("application/xml");
    }
}
