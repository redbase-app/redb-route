using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Educational end-to-end demos of the W4.4 explicit scope-form for
/// <c>Filter(predicate, body)</c> and <c>IdempotentConsumer(..., body)</c>
/// nested inside other scope-EIPs (Split / Loop / Choice / Filter).
///
/// These tests are intentionally minimal and assertion-rich: they document
/// the Camel-canonical "body is closed, tail keeps flowing" semantics that
/// the W4.4 Action-overloads introduce on top of the existing
/// tail-consuming (implicit-scope) Filter / IC.
/// </summary>
public class EipNestedScopeTests
{
    /// <summary>Filter scope inside a Split body: per-item filter, post-filter tail still runs for every part.</summary>
    [Fact]
    public async Task FilterScope_InsideSplit_BodyConditional_TailPerPart()
    {
        await using var context = new RouteContext();
        var inFilter = new List<int>();
        var afterFilter = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://split-flt")
                .Split(e => (IEnumerable<object?>)e.In.Body!, b => b
                    .Filter(e => (int)e.In.Body! % 2 == 0, fb => fb
                        .Process(e => inFilter.Add((int)e.In.Body!)))
                    .Process(e => afterFilter.Add((int)e.In.Body!)));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://split-flt").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(new object?[] { 1, 2, 3, 4, 5 })));

        inFilter.Should().Equal(2, 4);
        afterFilter.Should().Equal(1, 2, 3, 4, 5);
    }

    /// <summary>Filter scope inside a Loop body: counter-based filter, post-filter tail runs every iteration.</summary>
    [Fact]
    public async Task FilterScope_InsideLoop_BodyConditional_TailEveryIteration()
    {
        await using var context = new RouteContext();
        var inFilter = 0;
        var afterFilter = 0;

        context.AddRoutes(r =>
        {
            r.From("direct://loop-flt")
                .Loop(5, b => b
                    .Filter(_ => inFilter + afterFilter < 3, fb => fb
                        .Process(_ => inFilter++))
                    .Process(_ => afterFilter++));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://loop-flt").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("go")));

        afterFilter.Should().Be(5);     // tail runs every iteration
        inFilter.Should().BeGreaterThan(0).And.BeLessThan(5);
    }

    /// <summary>Filter scope inside a Choice When branch: branch tail still runs after filtered body.</summary>
    [Fact]
    public async Task FilterScope_InsideChoiceWhen_BodyConditional_BranchTailRuns()
    {
        await using var context = new RouteContext();
        var inFilter = new List<int>();
        var branchTail = new List<int>();
        var afterChoice = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-flt")
                .Choice(c => c
                    .When(e => (int)e.In.Body! > 0, b => b
                        .Filter(e => (int)e.In.Body! % 2 == 0, fb => fb
                            .Process(e => inFilter.Add((int)e.In.Body!)))
                        .Process(e => branchTail.Add((int)e.In.Body!))))
                .Process(e => afterChoice.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-flt").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { -1, 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        inFilter.Should().Equal(2, 4);
        branchTail.Should().Equal(1, 2, 3, 4);     // when-branch tail: positives only
        afterChoice.Should().Equal(-1, 1, 2, 3, 4); // after-choice tail: all
    }

    /// <summary>IdempotentConsumer scope inside a Split body: dedupe per element across the whole stream.</summary>
    [Fact]
    public async Task IcScope_InsideSplit_BodyOnFirstSeen_TailEveryPart()
    {
        await using var context = new RouteContext();
        var repo = new InMemoryIdempotentRepository();
        var inBody = new List<string>();
        var afterBody = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://split-ic")
                .Split(e => (IEnumerable<object?>)e.In.Body!, b => b
                    .IdempotentConsumer(e => (string)e.In.Body!, repo, ib => ib
                        .Process(e => inBody.Add((string)e.In.Body!)))
                    .Process(e => afterBody.Add((string)e.In.Body!)));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://split-ic").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(new object?[] { "a", "b", "a", "c", "b" })));

        inBody.Should().Equal("a", "b", "c");
        afterBody.Should().Equal("a", "b", "a", "c", "b");
    }

    /// <summary>Nested Filter scopes compose as logical AND for the inner body; outer tail still runs unconditionally.</summary>
    [Fact]
    public async Task FilterScope_InsideFilterScope_AndSemantics_OuterTailUnconditional()
    {
        await using var context = new RouteContext();
        var inInner = new List<int>();
        var afterOuter = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-flt")
                .Filter(e => (int)e.In.Body! > 0, ob => ob
                    .Filter(e => (int)e.In.Body! % 2 == 0, ib => ib
                        .Process(e => inInner.Add((int)e.In.Body!))))
                .Process(e => afterOuter.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-flt").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { -2, -1, 0, 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        inInner.Should().Equal(2, 4);                          // positive AND even
        afterOuter.Should().Equal(-2, -1, 0, 1, 2, 3, 4);     // outer tail: all
    }

    /// <summary>Sequential Filter then IC scopes at the same level: both bodies are independent islands, common tail runs always.</summary>
    [Fact]
    public async Task FilterScope_ThenIcScope_AtSameLevel_IndependentBodies_CommonTailAlways()
    {
        await using var context = new RouteContext();
        var repo = new InMemoryIdempotentRepository();
        var inFilter = new List<int>();
        var inIc = new List<int>();
        var tail = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-then-ic")
                .Filter(e => (int)e.In.Body! % 2 == 0, fb => fb
                    .Process(e => inFilter.Add((int)e.In.Body!)))
                .IdempotentConsumer(e => e.In.Body!.ToString()!, repo, ib => ib
                    .Process(e => inIc.Add((int)e.In.Body!)))
                .Process(e => tail.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-then-ic").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 1, 2, 3 })
            await producer.Process(new Exchange(new Message(i)));

        inFilter.Should().Equal(2, 2);                  // even only, no dedupe
        inIc.Should().Equal(1, 2, 3);                   // dedupe only, no filter
        tail.Should().Equal(1, 2, 1, 2, 3);             // tail always
    }
}
