using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using Xunit;

namespace redb.Route.Tests.Idempotent;

/// <summary>
/// Tests for the named IIdempotentRepositoryProvider registry (S1.1):
/// AddIdempotentRepository, named DSL overload, default provider behavior.
/// </summary>
public class NamedIdempotentRegistryTests
{
    [Fact]
    public void AddIdempotentRepository_RegistersUnderPrefixedKey()
    {
        var ctx = new RouteContext();
        var repo = new InMemoryIdempotentRepository();

        ctx.AddIdempotentRepository("orders", repo);

        var stored = ctx.GetFromRegistry<IIdempotentRepository>(
            RegistryIdempotentRepositoryProvider.KeyPrefix + "orders");
        stored.Should().BeSameAs(repo);
    }

    [Fact]
    public void DefaultProvider_Get_ReturnsRegisteredRepository()
    {
        var ctx = new RouteContext();
        var repo = new InMemoryIdempotentRepository();
        ctx.AddIdempotentRepository("foo", repo);

        var provider = ctx.GetIdempotentRepositoryProvider();
        var resolved = provider.Get("foo");

        resolved.Should().BeSameAs(repo);
    }

    [Fact]
    public void DefaultProvider_Get_MissingName_ThrowsHelpfulMessage()
    {
        var ctx = new RouteContext();
        var provider = ctx.GetIdempotentRepositoryProvider();

        var act = () => provider.Get("missing");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'missing'*")
            .WithMessage("*idempotent:missing*");
    }

    [Fact]
    public void DefaultProvider_TryGet_ReturnsFalseOnMissing()
    {
        var ctx = new RouteContext();
        var provider = ctx.GetIdempotentRepositoryProvider();

        var ok = provider.TryGet("missing", out var repo);

        ok.Should().BeFalse();
        repo.Should().BeNull();
    }

    [Fact]
    public void DefaultProvider_TryGet_ReturnsTrueOnHit()
    {
        var ctx = new RouteContext();
        var registered = new InMemoryIdempotentRepository();
        ctx.AddIdempotentRepository("hit", registered);
        var provider = ctx.GetIdempotentRepositoryProvider();

        var ok = provider.TryGet("hit", out var repo);

        ok.Should().BeTrue();
        repo.Should().BeSameAs(registered);
    }

    [Fact]
    public void GetIdempotentRepositoryProvider_RespectsCustomServiceOverride()
    {
        var ctx = new RouteContext();
        var custom = new StubProvider();
        ctx.AddService(typeof(IIdempotentRepositoryProvider), custom);

        var resolved = ctx.GetIdempotentRepositoryProvider();

        resolved.Should().BeSameAs(custom);
    }

    [Fact]
    public async Task NamedDsl_ResolvesAtCompile_AndDeduplicates()
    {
        await using var ctx = new RouteContext();
        var repo = new InMemoryIdempotentRepository();
        ctx.AddIdempotentRepository("orders", repo);

        var seen = new List<object?>();
        ctx.AddRoutes(r =>
        {
            r.From("direct://named-dedup")
                .IdempotentConsumer(e => e.In.Headers["MessageId"]?.ToString()!, "orders")
                .Process(e => seen.Add(e.In.Body));
        });

        await ctx.Start();
        var producer = ctx.GetEndpoint("direct://named-dedup").CreateProducer();
        await producer.Start();

        var m1 = new Message { Body = "a" }; m1.Headers["MessageId"] = "k1";
        var m2 = new Message { Body = "b" }; m2.Headers["MessageId"] = "k1";
        var m3 = new Message { Body = "c" }; m3.Headers["MessageId"] = "k2";
        await producer.Process(new Exchange(m1));
        await producer.Process(new Exchange(m2));
        await producer.Process(new Exchange(m3));

        seen.Should().HaveCount(2);
        seen[0].Should().Be("a");
        seen[1].Should().Be("c");
    }

    [Fact]
    public async Task NamedDsl_MissingRegistration_ThrowsAtStart()
    {
        await using var ctx = new RouteContext();

        ctx.AddRoutes(r =>
        {
            r.From("direct://missing-named")
                .IdempotentConsumer(e => "x", "not-registered")
                .Process(_ => { });
        });

        var act = async () => await ctx.Start();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'not-registered'*");
    }

    private sealed class StubProvider : IIdempotentRepositoryProvider
    {
        public IIdempotentRepository Get(string name) => throw new NotImplementedException();
        public bool TryGet(string name, out IIdempotentRepository repository)
        {
            repository = null!;
            return false;
        }
    }
}
