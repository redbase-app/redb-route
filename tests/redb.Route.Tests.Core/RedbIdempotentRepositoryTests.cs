using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Core.Query;
using redb.Route.Abstractions;
using redb.Route.RedbCore.Models;
using redb.Route.RedbCore.Repositories;
using System.Linq.Expressions;

namespace redb.Route.Tests.Core;

/// <summary>
/// Unit tests for <see cref="RedbIdempotentRepository"/>.
/// Uses NSubstitute to mock <see cref="IRedbService"/> behind <see cref="IServiceScopeFactory"/>.
/// </summary>
public sealed class RedbIdempotentRepositoryTests
{
    private readonly IRedbService _redb = Substitute.For<IRedbService>();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RedbIdempotentOptions _defaultOptions = new()
    {
        ProcessorName = "test-route"
    };

    public RedbIdempotentRepositoryTests()
    {
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IRedbService)).Returns(_redb);
        scope.ServiceProvider.Returns(sp);
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(scope);
    }

    private IServiceScopeFactory CreateScopeFactory(IRedbService redb)
    {
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IRedbService)).Returns(redb);
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    private RedbIdempotentRepository CreateSut(RedbIdempotentOptions? options = null)
        => new(_scopeFactory, options ?? _defaultOptions);

    private void SetupQuery(List<RedbObject<IdempotentEntryProps>> results)
    {
        var queryable = Substitute.For<IRedbQueryable<IdempotentEntryProps>>();
        queryable.Where(Arg.Any<Expression<Func<IdempotentEntryProps, bool>>>()).Returns(queryable);
        queryable.WhereRedb(Arg.Any<Expression<Func<IRedbObject, bool>>>()).Returns(queryable);
        queryable.ToListAsync().Returns(Task.FromResult(results));
        _redb.Query<IdempotentEntryProps>().Returns(queryable);
    }

    // ── Constructor ─────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullScopeFactory_Throws()
    {
        var act = () => new RedbIdempotentRepository(null!, _defaultOptions);
        act.Should().Throw<ArgumentNullException>().WithParameterName("scopeFactory");
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var act = () => new RedbIdempotentRepository(_scopeFactory, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    // ── Add ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_NullKey_Throws()
    {
        var sut = CreateSut();
        var act = () => sut.Add(null!);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("key");
    }

    [Fact]
    public async Task Add_NewKey_SyncsScheme_SavesEntry_ReturnsTrue()
    {
        SetupQuery([]);
        _redb.SaveAsync(Arg.Any<IRedbObject<IdempotentEntryProps>>()).Returns(1L);

        var sut = CreateSut();
        var result = await sut.Add("msg-001");

        result.Should().BeTrue();
        await _redb.Received(1).SyncSchemeAsync<IdempotentEntryProps>();
        await _redb.Received(1).SaveAsync(Arg.Is<RedbObject<IdempotentEntryProps>>(o =>
            o.Props.ProcessorName == "test-route" &&
            o.Props.MessageKey == "msg-001" &&
            o.Props.Confirmed == false));
    }

    [Fact]
    public async Task Add_DuplicateKey_ReturnsFalse()
    {
        var existing = new RedbObject<IdempotentEntryProps>
        {
            id = 42,
            Props = new IdempotentEntryProps
            {
                ProcessorName = "test-route",
                MessageKey = "msg-001",
                CreatedAt = DateTimeOffset.UtcNow,
                Confirmed = false
            }
        };
        SetupQuery([existing]);

        var sut = CreateSut();
        var result = await sut.Add("msg-001");

        result.Should().BeFalse();
        await _redb.DidNotReceive().SaveAsync(Arg.Any<IRedbObject<IdempotentEntryProps>>());
    }

    [Fact]
    public async Task Add_SyncSchemeCalledOnlyOnce()
    {
        SetupQuery([]);
        _redb.SaveAsync(Arg.Any<IRedbObject<IdempotentEntryProps>>()).Returns(1L);

        var sut = CreateSut();
        await sut.Add("msg-001");
        await sut.Add("msg-002");

        await _redb.Received(1).SyncSchemeAsync<IdempotentEntryProps>();
    }

    // ── Add with TTL cleanup ────────────────────────────────────────

    [Fact]
    public async Task Add_WithTtl_CleansExpired()
    {
        var options = new RedbIdempotentOptions
        {
            ProcessorName = "test-route",
            Ttl = TimeSpan.FromMinutes(30)
        };

        // Setup two separate query chains: one for cleanup, one for add-check
        var cleanupQuery = Substitute.For<IRedbQueryable<IdempotentEntryProps>>();
        cleanupQuery.Where(Arg.Any<Expression<Func<IdempotentEntryProps, bool>>>()).Returns(cleanupQuery);
        cleanupQuery.WhereRedb(Arg.Any<Expression<Func<IRedbObject, bool>>>()).Returns(cleanupQuery);
        cleanupQuery.ToListAsync().Returns(Task.FromResult(new List<RedbObject<IdempotentEntryProps>>()));

        _redb.Query<IdempotentEntryProps>().Returns(cleanupQuery);
        _redb.SaveAsync(Arg.Any<IRedbObject<IdempotentEntryProps>>()).Returns(1L);

        var sut = CreateSut(options);
        await sut.Add("msg-001");

        // Query called at least twice: once for cleanup, once for duplicate check
        _redb.Received(2).Query<IdempotentEntryProps>();
    }

    // ── Confirm ─────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_NullKey_Throws()
    {
        var sut = CreateSut();
        var act = () => sut.Confirm(null!);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("key");
    }

    [Fact]
    public async Task Confirm_ExistingEntry_SetsConfirmedTrue()
    {
        var entry = new RedbObject<IdempotentEntryProps>
        {
            id = 42,
            Props = new IdempotentEntryProps
            {
                ProcessorName = "test-route",
                MessageKey = "msg-001",
                CreatedAt = DateTimeOffset.UtcNow,
                Confirmed = false
            }
        };
        SetupQuery([entry]);
        _redb.SaveAsync(Arg.Any<IRedbObject>()).Returns(42L);

        var sut = CreateSut();
        await sut.Confirm("msg-001");

        entry.Props.Confirmed.Should().BeTrue();
        await _redb.Received(1).SaveAsync(Arg.Is<RedbObject<IdempotentEntryProps>>(o =>
            o.Props.Confirmed == true));
    }

    [Fact]
    public async Task Confirm_NoEntry_DoesNothing()
    {
        SetupQuery([]);

        var sut = CreateSut();
        await sut.Confirm("msg-missing");

        await _redb.DidNotReceive().SaveAsync(Arg.Any<IRedbObject>());
    }

    // ── Remove ──────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_NullKey_Throws()
    {
        var sut = CreateSut();
        var act = () => sut.Remove(null!);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("key");
    }

    [Fact]
    public async Task Remove_ExistingEntry_DeletesIt()
    {
        var entry = new RedbObject<IdempotentEntryProps>
        {
            id = 42,
            Props = new IdempotentEntryProps
            {
                ProcessorName = "test-route",
                MessageKey = "msg-001",
                CreatedAt = DateTimeOffset.UtcNow
            }
        };
        SetupQuery([entry]);
        _redb.DeleteAsync(Arg.Any<IRedbObject>()).Returns(true);

        var sut = CreateSut();
        await sut.Remove("msg-001");

        await _redb.Received(1).DeleteAsync(Arg.Is<RedbObject<IdempotentEntryProps>>(o => o.id == 42));
    }

    [Fact]
    public async Task Remove_NoEntry_DoesNothing()
    {
        SetupQuery([]);

        var sut = CreateSut();
        await sut.Remove("msg-missing");

        await _redb.DidNotReceive().DeleteAsync(Arg.Any<IRedbObject>());
    }

    // ── Contains ────────────────────────────────────────────────────

    [Fact]
    public async Task Contains_NullKey_Throws()
    {
        var sut = CreateSut();
        var act = () => sut.Contains(null!);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("key");
    }

    [Fact]
    public async Task Contains_ExistingEntry_ReturnsTrue()
    {
        var entry = new RedbObject<IdempotentEntryProps>
        {
            id = 42,
            Props = new IdempotentEntryProps
            {
                ProcessorName = "test-route",
                MessageKey = "msg-001",
                CreatedAt = DateTimeOffset.UtcNow
            }
        };
        SetupQuery([entry]);

        var sut = CreateSut();
        var result = await sut.Contains("msg-001");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_MissingEntry_ReturnsFalse()
    {
        SetupQuery([]);

        var sut = CreateSut();
        var result = await sut.Contains("msg-missing");

        result.Should().BeFalse();
    }

    // ── Clear ───────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_EntriesExist_DeletesAll()
    {
        var entries = new List<RedbObject<IdempotentEntryProps>>
        {
            new() { id = 1, Props = new() { ProcessorName = "test-route", MessageKey = "a" } },
            new() { id = 2, Props = new() { ProcessorName = "test-route", MessageKey = "b" } },
            new() { id = 3, Props = new() { ProcessorName = "test-route", MessageKey = "c" } }
        };
        SetupQuery(entries);
        _redb.DeleteAsync(Arg.Any<IEnumerable<IRedbObject>>()).Returns(3);

        var sut = CreateSut();
        await sut.Clear();

        await _redb.Received(1).DeleteAsync(Arg.Any<IEnumerable<IRedbObject>>());
    }

    [Fact]
    public async Task Clear_NoEntries_SkipsDelete()
    {
        SetupQuery([]);

        var sut = CreateSut();
        await sut.Clear();

        await _redb.DidNotReceive().DeleteAsync(Arg.Any<IEnumerable<IRedbObject>>());
    }

    // ── Processor isolation ─────────────────────────────────────────

    [Fact]
    public async Task Add_DifferentProcessors_AreIsolated()
    {
        // Processor A has "msg-001"
        var aQuery = Substitute.For<IRedbQueryable<IdempotentEntryProps>>();
        aQuery.Where(Arg.Any<Expression<Func<IdempotentEntryProps, bool>>>()).Returns(aQuery);
        aQuery.WhereRedb(Arg.Any<Expression<Func<IRedbObject, bool>>>()).Returns(aQuery);
        aQuery.ToListAsync().Returns(Task.FromResult(new List<RedbObject<IdempotentEntryProps>>
        {
            new()
            {
                id = 1,
                Props = new() { ProcessorName = "route-A", MessageKey = "msg-001" }
            }
        }));

        // Processor B does not have "msg-001"
        var bRedb = Substitute.For<IRedbService>();
        var bQuery = Substitute.For<IRedbQueryable<IdempotentEntryProps>>();
        bQuery.Where(Arg.Any<Expression<Func<IdempotentEntryProps, bool>>>()).Returns(bQuery);
        bQuery.WhereRedb(Arg.Any<Expression<Func<IRedbObject, bool>>>()).Returns(bQuery);
        bQuery.ToListAsync().Returns(Task.FromResult(new List<RedbObject<IdempotentEntryProps>>()));
        bRedb.Query<IdempotentEntryProps>().Returns(bQuery);
        bRedb.SaveAsync(Arg.Any<IRedbObject<IdempotentEntryProps>>()).Returns(2L);

        _redb.Query<IdempotentEntryProps>().Returns(aQuery);

        var sutA = new RedbIdempotentRepository(_scopeFactory, new RedbIdempotentOptions { ProcessorName = "route-A" });
        var sutB = new RedbIdempotentRepository(CreateScopeFactory(bRedb), new RedbIdempotentOptions { ProcessorName = "route-B" });

        var resultA = await sutA.Add("msg-001"); // already exists
        var resultB = await sutB.Add("msg-001"); // new for route-B

        resultA.Should().BeFalse();
        resultB.Should().BeTrue();
    }

    // ── IdempotentEntryProps model ──────────────────────────────────

    [Fact]
    public void IdempotentEntryProps_Defaults()
    {
        var props = new IdempotentEntryProps();

        props.ProcessorName.Should().BeEmpty();
        props.MessageKey.Should().BeEmpty();
        props.CreatedAt.Should().Be(default);
        props.Confirmed.Should().BeFalse();
    }

    // ── Interface compliance ────────────────────────────────────────

    [Fact]
    public void Implements_IIdempotentRepository()
    {
        var sut = CreateSut();
        sut.Should().BeAssignableTo<IIdempotentRepository>();
    }
}
