using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// W4.5 variant-C demos: end-style scope form for <c>Filter()</c> /
/// <c>IdempotentConsumer()</c>. The opener pushes a block frame; the body
/// is closed either by an explicit <c>EndFilter()</c> / <c>EndIdempotentConsumer()</c>
/// / <c>End()</c>, or auto-drained at freeze (mirroring legacy tail-consuming).
/// </summary>
public class EipEndStyleScopeTests
{
    /// <summary>Filter end-style: body runs only when predicate is true; tail after EndFilter runs always.</summary>
    [Fact]
    public async Task Filter_EndStyle_BodyConditional_TailAfterEndUnconditional()
    {
        await using var context = new RouteContext();
        var inFilter = new List<int>();
        var afterEnd = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-end")
                .Filter(e => (int)e.In.Body! % 2 == 0)
                    .Process(e => inFilter.Add((int)e.In.Body!))
                .EndFilter()
                .Process(e => afterEnd.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-end").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        inFilter.Should().Equal(2, 4);
        afterEnd.Should().Equal(1, 2, 3, 4);
    }

    /// <summary>Filter end-style with generic <c>End()</c>: same semantics as <c>EndFilter()</c>.</summary>
    [Fact]
    public async Task Filter_EndStyle_GenericEnd_AlsoCloses()
    {
        await using var context = new RouteContext();
        var inFilter = new List<int>();
        var afterEnd = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-genend")
                .Filter(e => (int)e.In.Body! % 2 == 0)
                    .Process(e => inFilter.Add((int)e.In.Body!))
                .End()
                .Process(e => afterEnd.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-genend").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        inFilter.Should().Equal(2, 4);
        afterEnd.Should().Equal(1, 2, 3, 4);
    }

    /// <summary>Legacy implicit form (no EndFilter): tail still consumed by filter via auto-drain at freeze.</summary>
    [Fact]
    public async Task Filter_ImplicitForm_AutoDrain_StillTailConsuming()
    {
        await using var context = new RouteContext();
        var seen = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-implicit")
                .Filter(e => (int)e.In.Body! % 2 == 0)
                    .Process(e => seen.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-implicit").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4, 5 })
            await producer.Process(new Exchange(new Message(i)));

        seen.Should().Equal(2, 4);
    }

    /// <summary>IdempotentConsumer end-style: body runs only for first-seen keys; tail after End runs always.</summary>
    [Fact]
    public async Task IdempotentConsumer_EndStyle_BodyDeduped_TailUnconditional()
    {
        await using var context = new RouteContext();
        var inIc = new List<int>();
        var afterEnd = new List<int>();
        var repo = new InMemoryIdempotentRepository();

        context.AddRoutes(r =>
        {
            r.From("direct://ic-end")
                .IdempotentConsumer(e => e.In.Body!.ToString()!, repo)
                    .Process(e => inIc.Add((int)e.In.Body!))
                .EndIdempotentConsumer()
                .Process(e => afterEnd.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://ic-end").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 1, 2, 3 })
            await producer.Process(new Exchange(new Message(i)));

        inIc.Should().Equal(1, 2, 3);
        afterEnd.Should().Equal(1, 2, 1, 2, 3);
    }

    /// <summary>EndFilter on empty stack throws.</summary>
    [Fact]
    public void EndFilter_WithoutOpenFilter_Throws()
    {
        var def = new redb.Route.Definitions.RouteDefinition();
        def.From("direct://flt-err");
        var act = () => def.EndFilter();
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>EndIdempotentConsumer on empty stack throws.</summary>
    [Fact]
    public void EndIdempotentConsumer_WithoutOpenIc_Throws()
    {
        var def = new redb.Route.Definitions.RouteDefinition();
        def.From("direct://ic-err");
        var act = () => def.EndIdempotentConsumer();
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>Filter end-style with multiple processors in the body: all run together, all skipped together.</summary>
    [Fact]
    public async Task Filter_EndStyle_MultiStepBody_AllOrNothing()
    {
        await using var context = new RouteContext();
        var stepA = new List<int>();
        var stepB = new List<int>();
        var stepC = new List<int>();
        var afterEnd = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-multi")
                .Filter(e => (int)e.In.Body! % 2 == 0)
                    .Process(e => stepA.Add((int)e.In.Body!))
                    .Process(e => stepB.Add((int)e.In.Body! * 10))
                    .Process(e => stepC.Add((int)e.In.Body! + 100))
                .EndFilter()
                .Process(e => afterEnd.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-multi").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        stepA.Should().Equal(2, 4);
        stepB.Should().Equal(20, 40);
        stepC.Should().Equal(102, 104);
        afterEnd.Should().Equal(1, 2, 3, 4);
    }

    /// <summary>Filter end-style nested inside Filter end-style: inner runs only when BOTH predicates hold (AND).</summary>
    [Fact]
    public async Task Filter_EndStyle_NestedInsideFilter_AndSemantics()
    {
        await using var context = new RouteContext();
        var outerOnly = new List<int>();
        var innerBody = new List<int>();
        var outerTail = new List<int>();
        var afterAll = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-nested")
                .Filter(e => (int)e.In.Body! % 2 == 0)                  // even
                    .Process(e => outerOnly.Add((int)e.In.Body!))
                    .Filter(e => (int)e.In.Body! > 3)                   // even AND > 3
                        .Process(e => innerBody.Add((int)e.In.Body!))
                    .EndFilter()
                    .Process(e => outerTail.Add((int)e.In.Body!))
                .EndFilter()
                .Process(e => afterAll.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-nested").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4, 5, 6 })
            await producer.Process(new Exchange(new Message(i)));

        outerOnly.Should().Equal(2, 4, 6);          // even
        innerBody.Should().Equal(4, 6);             // even AND > 3
        outerTail.Should().Equal(2, 4, 6);          // back in outer scope: even only
        afterAll.Should().Equal(1, 2, 3, 4, 5, 6);  // unconditional
    }

    /// <summary>Two consecutive Filter end-style blocks compose as AND filters with a tail in between.</summary>
    [Fact]
    public async Task Filter_EndStyle_TwoSequential_ComposeIndependently()
    {
        await using var context = new RouteContext();
        var first = new List<int>();
        var middle = new List<int>();
        var second = new List<int>();
        var tail = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-seq")
                .Filter(e => (int)e.In.Body! % 2 == 0)
                    .Process(e => first.Add((int)e.In.Body!))
                .EndFilter()
                .Process(e => middle.Add((int)e.In.Body!))      // unconditional middle
                .Filter(e => (int)e.In.Body! > 2)
                    .Process(e => second.Add((int)e.In.Body!))
                .EndFilter()
                .Process(e => tail.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-seq").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        first.Should().Equal(2, 4);             // even
        middle.Should().Equal(1, 2, 3, 4);      // all
        second.Should().Equal(3, 4);            // > 2
        tail.Should().Equal(1, 2, 3, 4);        // all
    }

    /// <summary>IdempotentConsumer end-style with multi-step body wraps the whole body in dedupe.</summary>
    [Fact]
    public async Task IdempotentConsumer_EndStyle_MultiStepBody_DedupesAllSteps()
    {
        await using var context = new RouteContext();
        var stepA = new List<int>();
        var stepB = new List<int>();
        var afterEnd = new List<int>();
        var repo = new InMemoryIdempotentRepository();

        context.AddRoutes(r =>
        {
            r.From("direct://ic-multi")
                .IdempotentConsumer(e => e.In.Body!.ToString()!, repo)
                    .Process(e => stepA.Add((int)e.In.Body!))
                    .Process(e => stepB.Add((int)e.In.Body! * 10))
                .EndIdempotentConsumer()
                .Process(e => afterEnd.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://ic-multi").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 1, 2, 3 })
            await producer.Process(new Exchange(new Message(i)));

        stepA.Should().Equal(1, 2, 3);                  // first-seen only
        stepB.Should().Equal(10, 20, 30);               // same dedupe scope
        afterEnd.Should().Equal(1, 2, 1, 2, 3);         // tail every time
    }

    /// <summary>Filter end-style wrapping IdempotentConsumer end-style: even-only, then deduped by key.</summary>
    [Fact]
    public async Task Filter_EndStyle_Wrapping_IdempotentConsumer_EndStyle()
    {
        await using var context = new RouteContext();
        var inFilter = new List<int>();
        var inIc = new List<int>();
        var afterIc = new List<int>();
        var afterFlt = new List<int>();
        var repo = new InMemoryIdempotentRepository();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-wrap-ic")
                .Filter(e => (int)e.In.Body! % 2 == 0)
                    .Process(e => inFilter.Add((int)e.In.Body!))
                    .IdempotentConsumer(e => e.In.Body!.ToString()!, repo)
                        .Process(e => inIc.Add((int)e.In.Body!))
                    .EndIdempotentConsumer()
                    .Process(e => afterIc.Add((int)e.In.Body!))
                .EndFilter()
                .Process(e => afterFlt.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-wrap-ic").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 2, 4, 4, 5 })
            await producer.Process(new Exchange(new Message(i)));

        inFilter.Should().Equal(2, 2, 4, 4);            // every even
        inIc.Should().Equal(2, 4);                       // first-seen even keys
        afterIc.Should().Equal(2, 2, 4, 4);              // back in filter scope (every even)
        afterFlt.Should().Equal(1, 2, 3, 2, 4, 4, 5);    // unconditional
    }

    /// <summary>Three-level Filter nesting end-style: predicates compose as logical AND down the stack.</summary>
    [Fact]
    public async Task Filter_EndStyle_ThreeLevelNesting_AndComposition()
    {
        await using var context = new RouteContext();
        var l1 = new List<int>();
        var l2 = new List<int>();
        var l3 = new List<int>();
        var tail = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-3lvl")
                .Filter(e => (int)e.In.Body! > 0)                       // positive
                    .Process(e => l1.Add((int)e.In.Body!))
                    .Filter(e => (int)e.In.Body! % 2 == 0)              // pos AND even
                        .Process(e => l2.Add((int)e.In.Body!))
                        .Filter(e => (int)e.In.Body! >= 4)              // pos AND even AND >=4
                            .Process(e => l3.Add((int)e.In.Body!))
                        .EndFilter()
                    .EndFilter()
                .EndFilter()
                .Process(e => tail.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-3lvl").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { -1, 0, 1, 2, 3, 4, 5, 6 })
            await producer.Process(new Exchange(new Message(i)));

        l1.Should().Equal(1, 2, 3, 4, 5, 6);            // > 0
        l2.Should().Equal(2, 4, 6);                      // > 0 AND even
        l3.Should().Equal(4, 6);                         // > 0 AND even AND >= 4
        tail.Should().Equal(-1, 0, 1, 2, 3, 4, 5, 6);    // all
    }
}
