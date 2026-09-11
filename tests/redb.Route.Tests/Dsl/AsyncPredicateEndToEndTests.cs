using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// End-to-end proof that <see cref="IPredicate.MatchesAsync"/> is the method the engine calls.
/// Until 2026-08-28 every branching definition unwrapped a predicate into its synchronous
/// <see cref="IPredicate.Matches"/> at the boundary, so an asynchronous predicate — one that
/// consults a store or a service — was silently run synchronously, and the async half of the
/// contract was dead. The predicate below answers only through the asynchronous method and
/// refuses the synchronous one, which is exactly what a real awaiting predicate would do.
/// </summary>
[Collection("ExpressionResolver")]
public class AsyncPredicateEndToEndTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A predicate that is only ever right asynchronously.</summary>
    private sealed class AsyncOnlyPredicate(Func<IExchange, bool> decide) : IPredicate
    {
        public int AsyncCalls { get; private set; }

        public bool Matches(IExchange exchange)
            => throw new InvalidOperationException("The engine must await MatchesAsync, not call Matches.");

        public async Task<bool> MatchesAsync(IExchange exchange)
        {
            await Task.Yield();
            AsyncCalls++;
            return decide(exchange);
        }
    }

    private async Task<IExchange> Send(string uri, int amount)
    {
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["amount"] = amount;
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        return exchange;
    }

    [Fact]
    public async Task Filter_AwaitsThePredicate()
    {
        var predicate = new AsyncOnlyPredicate(e => (int)e.In.Headers["amount"]! > 1000);
        var passed = new List<int>();
        _context.AddRoutes(r => r.From("direct://async-filter")
            .Filter(predicate)
            .Process(e => passed.Add((int)e.In.Headers["amount"]!)));
        await _context.Start();

        await Send("direct://async-filter", 5000);
        await Send("direct://async-filter", 10);

        passed.Should().Equal(5000);
        predicate.AsyncCalls.Should().Be(2);
    }

    [Fact]
    public async Task Choice_AwaitsThePredicate()
    {
        var predicate = new AsyncOnlyPredicate(e => (int)e.In.Headers["amount"]! > 1000);
        string? taken = null;
        _context.AddRoutes(r => r.From("direct://async-choice").Choice()
            .When(predicate).Process(_ => taken = "big").EndWhen()
            .Otherwise().Process(_ => taken = "small").EndChoice());
        await _context.Start();

        await Send("direct://async-choice", 5000);
        taken.Should().Be("big");

        await Send("direct://async-choice", 10);
        taken.Should().Be("small");

        predicate.AsyncCalls.Should().Be(2);
    }

    [Fact]
    public async Task Loop_AwaitsThePredicateBeforeEveryIteration()
    {
        var predicate = new AsyncOnlyPredicate(e => (int)e.Properties["i"]! < 3);
        var iterations = 0;
        _context.AddRoutes(r => r.From("direct://async-loop")
            .Loop(predicate)
            .Process(e =>
            {
                iterations++;
                e.Properties["i"] = (int)e.Properties["i"]! + 1;
            })
            .EndLoop());
        await _context.Start();

        var exchange = new Exchange(new Message("payload"));
        exchange.Properties["i"] = 0;
        var producer = _context.GetEndpoint("direct://async-loop").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        iterations.Should().Be(3);
        predicate.AsyncCalls.Should().Be(4, "three iterations plus the final check that ends the loop");
    }

    [Fact]
    public async Task Filter_RecordsThePredicateItWasGiven()
    {
        var predicate = new AsyncOnlyPredicate(_ => true);
        redb.Route.Definitions.FilterDefinition? filter = null;
        _context.AddRoutes(r => filter = r.From("direct://async-source").Filter(predicate));
        await _context.Start();

        filter!.SourcePredicate.Should().BeSameAs(predicate, "the stored condition is the source, not a copy beside it");
    }
}
