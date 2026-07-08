using redb.Route.Abstractions;
using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class DefaultInflightRepositoryTests
{
    private readonly DefaultInflightRepository _repo = new();

    private static InflightExchange MakeEntry(string exchangeId = "ex-1", string routeId = "route-1")
        => new(exchangeId, routeId, DateTime.UtcNow, Environment.CurrentManagedThreadId, "kafka:orders");

    [Fact]
    public void Register_AddsEntry()
    {
        _repo.Register(MakeEntry());
        _repo.Count.Should().Be(1);
    }

    [Fact]
    public void Unregister_RemovesEntry()
    {
        _repo.Register(MakeEntry("ex-1"));
        _repo.Unregister("ex-1");
        _repo.Count.Should().Be(0);
    }

    [Fact]
    public void Unregister_UnknownId_DoesNotThrow()
    {
        var act = () => _repo.Unregister("nonexistent");
        act.Should().NotThrow();
    }

    [Fact]
    public void Browse_ReturnsAllEntries()
    {
        _repo.Register(MakeEntry("ex-1", "route-1"));
        _repo.Register(MakeEntry("ex-2", "route-2"));
        _repo.Register(MakeEntry("ex-3", "route-1"));

        _repo.Browse().Should().HaveCount(3);
    }

    [Fact]
    public void Browse_ByRouteId_ReturnsFilteredEntries()
    {
        _repo.Register(MakeEntry("ex-1", "route-1"));
        _repo.Register(MakeEntry("ex-2", "route-2"));
        _repo.Register(MakeEntry("ex-3", "route-1"));

        _repo.Browse("route-1").Should().HaveCount(2);
        _repo.Browse("route-2").Should().HaveCount(1);
        _repo.Browse("route-3").Should().BeEmpty();
    }

    [Fact]
    public void CountByRoute_ReturnsCorrectCount()
    {
        _repo.Register(MakeEntry("ex-1", "route-1"));
        _repo.Register(MakeEntry("ex-2", "route-1"));
        _repo.Register(MakeEntry("ex-3", "route-2"));

        _repo.CountByRoute("route-1").Should().Be(2);
        _repo.CountByRoute("route-2").Should().Be(1);
        _repo.CountByRoute("route-3").Should().Be(0);
    }

    [Fact]
    public void CountByRoute_AfterUnregister_Decrements()
    {
        _repo.Register(MakeEntry("ex-1", "route-1"));
        _repo.Register(MakeEntry("ex-2", "route-1"));
        _repo.Unregister("ex-1");

        _repo.CountByRoute("route-1").Should().Be(1);
    }

    [Fact]
    public void CountByRoute_NeverGoesNegative()
    {
        _repo.Register(MakeEntry("ex-1", "route-1"));
        _repo.Unregister("ex-1");
        _repo.Unregister("ex-1"); // double unregister

        _repo.CountByRoute("route-1").Should().Be(0);
    }

    [Fact]
    public void DuplicateRegister_IsIgnored()
    {
        _repo.Register(MakeEntry("ex-1", "route-1"));
        _repo.Register(MakeEntry("ex-1", "route-1")); // same id

        _repo.Count.Should().Be(1);
        _repo.CountByRoute("route-1").Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentRegisterUnregister_IsThreadSafe()
    {
        const int count = 1000;

        var registerTasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => _repo.Register(MakeEntry($"ex-{i}", "route-1"))));
        await Task.WhenAll(registerTasks);

        _repo.Count.Should().Be(count);
        _repo.CountByRoute("route-1").Should().Be(count);

        var unregisterTasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => _repo.Unregister($"ex-{i}")));
        await Task.WhenAll(unregisterTasks);

        _repo.Count.Should().Be(0);
        _repo.CountByRoute("route-1").Should().Be(0);
    }

    [Fact]
    public void Browse_ReturnsSnapshot_NotLiveReference()
    {
        _repo.Register(MakeEntry("ex-1"));
        var snapshot = _repo.Browse();
        _repo.Register(MakeEntry("ex-2"));

        snapshot.Should().HaveCount(1); // snapshot unchanged
        _repo.Browse().Should().HaveCount(2); // new browse reflects change
    }
}
