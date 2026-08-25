using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Tests for the <c>.ClaimCheck(...)</c> DSL: the Claim Check EIP reached from a route,
/// including repository resolution (explicit instance, named registry entry, context default).
/// </summary>
public class ClaimCheckDslTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds a route ending in a collector, runs one exchange through it.</summary>
    private async Task<IExchange> RunAsync(Action<IRouteDefinition> configure, object body)
    {
        IExchange? seen = null;

        _context.AddRoutes(r =>
        {
            var def = r.From("direct://claim-in");
            configure(def);
            def.Process(e => seen = e);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://claim-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = body }));

        seen.Should().NotBeNull("the route must reach its final step");
        return seen!;
    }

    // ── Set / Get ───────────────────────────────────────────────────

    [Fact]
    public async Task Set_ReplacesBodyWithClaimKey_AndStoresThePayload()
    {
        var repository = new InMemoryClaimCheckRepository();

        var exchange = await RunAsync(
            def => def.ClaimCheck(repository, ClaimCheckOperation.Set, "order-42"),
            "a very large payload");

        exchange.In.Body.Should().Be("order-42", "the body travels on as the claim key");
        exchange.In.Headers[ClaimCheckHeaders.Key].Should().Be("order-42");
        (await repository.Retrieve("order-42")).Should().NotBeNull();
    }

    [Fact]
    public async Task SetThenGet_RestoresTheOriginalBody()
    {
        var repository = new InMemoryClaimCheckRepository();

        var exchange = await RunAsync(
            def => def
                .ClaimCheck(repository, ClaimCheckOperation.Set, "order-42")
                .ClaimCheck(repository, ClaimCheckOperation.Get, "order-42"),
            "a very large payload");

        exchange.In.Body.Should().Be("a very large payload");
    }

    [Fact]
    public async Task GetAndRemove_DropsThePayloadFromTheStore()
    {
        var repository = new InMemoryClaimCheckRepository();

        await RunAsync(
            def => def
                .ClaimCheck(repository, ClaimCheckOperation.Set, "order-42")
                .ClaimCheck(repository, ClaimCheckOperation.GetAndRemove, "order-42"),
            "payload");

        (await repository.Retrieve("order-42")).Should().BeNull();
    }

    // ── Push / Pop ──────────────────────────────────────────────────

    [Fact]
    public async Task PushThenPop_RoundTripsTheBodyWithoutAKey()
    {
        var repository = new InMemoryClaimCheckRepository();

        var exchange = await RunAsync(
            def => def
                .ClaimCheck(repository, ClaimCheckOperation.Push)
                .Process(e => e.In.Body = "replaced while the real body was checked in")
                .ClaimCheck(repository, ClaimCheckOperation.Pop),
            "original payload");

        exchange.In.Body.Should().Be("original payload");
    }

    [Fact]
    public async Task PushPop_NestsLikeAStack()
    {
        var repository = new InMemoryClaimCheckRepository();

        var exchange = await RunAsync(
            def => def
                .ClaimCheck(repository, ClaimCheckOperation.Push)   // outer body checked in
                .Process(e => e.In.Body = "middle")
                .ClaimCheck(repository, ClaimCheckOperation.Push)   // "middle" checked in
                .Process(e => e.In.Body = "inner-work")
                .ClaimCheck(repository, ClaimCheckOperation.Pop)    // back to "middle"
                .ClaimCheck(repository, ClaimCheckOperation.Pop),   // back to the original
            "outer");

        exchange.In.Body.Should().Be("outer");
    }

    // ── Repository resolution ───────────────────────────────────────

    [Fact]
    public async Task WithoutARepository_TheContextDefaultIsSharedAcrossSteps()
    {
        var exchange = await RunAsync(
            def => def
                .ClaimCheck(ClaimCheckOperation.Push)
                .Process(e => e.In.Body = "scratch")
                .ClaimCheck(ClaimCheckOperation.Pop),
            "original payload");

        exchange.In.Body.Should().Be("original payload",
            "steps that name no repository must land in the same context-wide store");
    }

    [Fact]
    public async Task NamedRepository_IsResolvedFromTheRegistry()
    {
        var named = new InMemoryClaimCheckRepository();
        _context.AddClaimCheckRepository("big-payloads", named);

        var exchange = await RunAsync(
            def => def
                .ClaimCheck(ClaimCheckOperation.Set, "k", repositoryName: "big-payloads")
                .ClaimCheck(ClaimCheckOperation.Get, "k", repositoryName: "big-payloads"),
            "payload");

        exchange.In.Body.Should().Be("payload");
        (await named.Retrieve("k")).Should().NotBeNull("the named repository is the one that was used");
    }

    [Fact]
    public async Task UnknownRepositoryName_FailsWithANamingError()
    {
        _context.AddRoutes(r => r
            .From("direct://claim-missing")
            .ClaimCheck(ClaimCheckOperation.Set, "k", repositoryName: "not-registered"));

        var act = () => _context.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not-registered*");
    }

    [Fact]
    public async Task ExplicitDefaultRepository_WinsOverTheImplicitOne()
    {
        var mine = new InMemoryClaimCheckRepository();
        _context.SetDefaultClaimCheckRepository(mine);

        await RunAsync(def => def.ClaimCheck(ClaimCheckOperation.Set, "k"), "payload");

        (await mine.Retrieve("k")).Should().NotBeNull();
    }
}
