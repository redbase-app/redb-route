using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Core.Query;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;
using redb.Route.RedbCore.Models;
using System.Linq.Expressions;

namespace redb.Route.Tests.Core;

/// <summary>
/// <see cref="RedbContextDslExtensions.AddRedbIdempotentRepository"/> with a <c>storage</c>: the entries go to the named
/// redb instance of the context, through a scope of it per operation, and a context without a host redb works.
/// </summary>
public sealed class RedbIdempotentRepositoryStorageTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private static IRedbService ScopedService(List<RedbObject<IdempotentEntryProps>> stored)
    {
        var service = Substitute.For<IRedbService>();
        var queryable = Substitute.For<IRedbQueryable<IdempotentEntryProps>>();
        queryable.Where(Arg.Any<Expression<Func<IdempotentEntryProps, bool>>>()).Returns(queryable);
        queryable.WhereRedb(Arg.Any<Expression<Func<IRedbObject, bool>>>()).Returns(queryable);
        queryable.ToListAsync().Returns(_ => Task.FromResult(stored));
        service.Query<IdempotentEntryProps>().Returns(queryable);
        return service;
    }

    [Fact]
    public async Task Named_storage_keeps_the_entries_in_that_instance_without_a_host_redb()
    {
        var scoped = ScopedService([]);
        var instance = Substitute.For<IRedbService>();
        instance.CanCreateScope.Returns(true);
        instance.CreateScope().Returns(_ => new RedbScope(scoped, null));
        _context.RegisterRedbService("local", instance);
        _context.AddRedbIdempotentRepository("dedup", storage: "local");

        await _context.Start();
        var repository = _context.GetIdempotentRepositoryProvider().Get("dedup");
        var found = await repository.Contains("order-1");

        found.Should().BeFalse();
        instance.Received().CreateScope();
        _ = scoped.Received(1).SyncSchemeAsync<IdempotentEntryProps>();
        scoped.Received(1).Query<IdempotentEntryProps>();
        instance.DidNotReceive().Query<IdempotentEntryProps>();
    }

    [Fact]
    public async Task Unknown_storage_fails_the_start_and_registers_nothing()
    {
        _context.AddRedbIdempotentRepository("dedup", storage: "ghost");

        var start = () => _context.Start();

        var thrown = await start.Should().ThrowAsync<Exception>();
        thrown.Which.ToString().Should().Contain("ghost");
        _context.GetIdempotentRepositoryProvider().TryGet("dedup", out _).Should().BeFalse();
    }
}
