using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// <see cref="Exchange.ReleaseScopes"/> used to be a one-shot latch: the first call spent it for
/// the life of the exchange. Downstream code calls it manually in error handlers (a production
/// integration carries three such call sites), and any redb access on the same exchange afterwards caches a fresh
/// scope under <c>__redb_scope:*</c> — which the spent latch then prevented
/// <see cref="Exchange.DisposeAsync"/> from ever releasing. A landmine rather than a proven live
/// leak, because today's handlers end the route; these tests defuse it either way.
/// <para>
/// The latch's real job was mutual exclusion and once-only disposal, and both survive without the
/// mine: entries leave the property bag as they are released, the owned scope nulls out, and the
/// latch re-arms after each sweep — so calling again releases only what appeared since.
/// </para>
/// </summary>
public sealed class ExchangeScopeReleaseTests
{
    private sealed class TrackingScope : IServiceScope, IAsyncDisposable
    {
        public int DisposeCount;
        public IServiceProvider ServiceProvider => null!;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_scope_created_after_a_manual_release_is_still_released_on_dispose()
    {
        var exchange = new Exchange(new Message("m"));
        var beforeRelease = new TrackingScope();
        exchange.Properties["__redb_scope:audit"] = beforeRelease;

        await exchange.ReleaseScopes();
        beforeRelease.DisposeCount.Should().Be(1);

        // What an error handler's tail does: touch redb again on the same exchange, which caches
        // a fresh scope. The manual release must not have cost the exchange its cleanup.
        var afterRelease = new TrackingScope();
        exchange.Properties["__redb_scope:usage"] = afterRelease;

        await exchange.DisposeAsync();
        afterRelease.DisposeCount.Should().Be(1,
            "a spent latch here means a live connection held until the GC finds it");
    }

    [Fact]
    public async Task Releasing_twice_disposes_each_scope_once()
    {
        var exchange = new Exchange(new Message("m"));
        var scope = new TrackingScope();
        exchange.Properties["__redb_scope:audit"] = scope;

        await exchange.ReleaseScopes();
        await exchange.ReleaseScopes();

        scope.DisposeCount.Should().Be(1, "the entry leaves the property bag as it is released");
    }

    [Fact]
    public async Task The_owned_scope_is_disposed_once_across_repeated_releases()
    {
        var scope = new TrackingScope();
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        var exchange = new Exchange(new Message("m"), factory);

        await exchange.ReleaseScopes();
        await exchange.ReleaseScopes();
        await exchange.DisposeAsync();

        scope.DisposeCount.Should().Be(1, "the owned scope nulls out after its first disposal");
    }
}
