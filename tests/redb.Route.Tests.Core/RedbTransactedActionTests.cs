using System.Collections.Concurrent;
using NSubstitute;
using redb.Core;
using redb.Core.Data;
using redb.Route.Abstractions;
using redb.Route.RedbCore.Extensions;
using redb.Route.RedbCore.Transactions;
using redb.Route.Transactions;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for <see cref="RedbTransactedAction"/> and the
/// <c>BeginRedbTransaction</c> DSL extensions (S3.1 R-17).
/// </summary>
public sealed class RedbTransactedActionTests
{
    // ── RedbTransactedAction adapter ────────────────────────────────

    [Fact]
    public void Constructor_NullTx_Throws()
    {
        var act = () => new RedbTransactedAction(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("tx");
    }

    [Fact]
    public async Task Commit_CommitsAndDisposesUnderlyingTx()
    {
        var tx = Substitute.For<IRedbTransaction>();
        tx.IsActive.Returns(true);
        var action = new RedbTransactedAction(tx);

        await action.Commit();

        await tx.Received(1).CommitAsync();
        await tx.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Rollback_RollsBackAndDisposesUnderlyingTx()
    {
        var tx = Substitute.For<IRedbTransaction>();
        tx.IsActive.Returns(true);
        var action = new RedbTransactedAction(tx);

        await action.Rollback();

        await tx.Received(1).RollbackAsync();
        await tx.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Commit_InactiveTx_OnlyDisposes()
    {
        var tx = Substitute.For<IRedbTransaction>();
        tx.IsActive.Returns(false);
        var action = new RedbTransactedAction(tx);

        await action.Commit();

        await tx.DidNotReceive().CommitAsync();
        await tx.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DoubleCommit_SecondCallIsNoOp()
    {
        var tx = Substitute.For<IRedbTransaction>();
        tx.IsActive.Returns(true);
        var action = new RedbTransactedAction(tx);

        await action.Commit();
        await action.Commit();

        await tx.Received(1).CommitAsync();
        await tx.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Rollback_AfterCommit_IsNoOp()
    {
        var tx = Substitute.For<IRedbTransaction>();
        tx.IsActive.Returns(true);
        var action = new RedbTransactedAction(tx);

        await action.Commit();
        await action.Rollback();

        await tx.Received(1).CommitAsync();
        await tx.DidNotReceive().RollbackAsync();
        await tx.Received(1).DisposeAsync();
    }

    // ── BeginRedbTransaction DSL ─────────────────────────────────────

    [Fact]
    public void BeginRedbTransaction_NullRoute_Throws()
    {
        IRouteDefinition? route = null;
        var act = () => route!.BeginRedbTransaction();
        act.Should().Throw<ArgumentNullException>().WithParameterName("route");
    }

    [Fact]
    public void BeginRedbTransaction_Named_NullName_Throws()
    {
        var route = Substitute.For<IRouteDefinition>();
        var act = () => route.BeginRedbTransaction(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BeginRedbTransaction_Named_EmptyName_Throws()
    {
        var route = Substitute.For<IRouteDefinition>();
        var act = () => route.BeginRedbTransaction(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task BeginRedbTransaction_Default_OpensTxAndEnrollsInTransactActions()
    {
        var (route, context, tx) = SetupRouteWithRedb(name: null);
        var holder = new ProcessorHolder();
        WireProcessor(route, holder);
        var exchange = CreateExchange();

        route.BeginRedbTransaction();
        holder.Captured.Should().NotBeNull();
        await holder.Captured!(exchange, CancellationToken.None);

        await context.GetRedbService().Context.Received(1).BeginTransactionAsync();

        var actions = GetActionsBag(exchange);
        actions.Should().ContainKey("redb:");
        actions["redb:"].Should().BeOfType<RedbTransactedAction>()
            .Which.Transaction.Should().BeSameAs(tx);
    }

    [Fact]
    public async Task BeginRedbTransaction_Named_OpensTxUnderNamedKey()
    {
        var (route, _, tx) = SetupRouteWithRedb(name: "orders-db");
        var holder = new ProcessorHolder();
        WireProcessor(route, holder);
        var exchange = CreateExchange();

        route.BeginRedbTransaction("orders-db");
        await holder.Captured!(exchange, CancellationToken.None);

        var actions = GetActionsBag(exchange);
        actions.Should().ContainKey("redb:orders-db");
        actions["redb:orders-db"].Should().BeOfType<RedbTransactedAction>()
            .Which.Transaction.Should().BeSameAs(tx);
    }

    [Fact]
    public async Task BeginRedbTransaction_Idempotent_DoesNotOpenSecondTxForSameKey()
    {
        var (route, context, _) = SetupRouteWithRedb(name: null);
        var holder = new ProcessorHolder();
        WireProcessor(route, holder);
        var exchange = CreateExchange();

        route.BeginRedbTransaction();
        await holder.Captured!(exchange, CancellationToken.None);
        await holder.Captured!(exchange, CancellationToken.None); // second invocation

        await context.GetRedbService().Context.Received(1).BeginTransactionAsync();
        GetActionsBag(exchange).Should().HaveCount(1);
    }

    [Fact]
    public async Task BeginRedbTransaction_NullContext_Throws()
    {
        var route = Substitute.For<IRouteDefinition>();
        route.GetContext().Returns((IRouteContext?)null);
        var holder = new ProcessorHolder();
        WireProcessor(route, holder);

        route.BeginRedbTransaction();
        var act = () => holder.Captured!(CreateExchange(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*RouteContext is not available*");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (IRouteDefinition route, IRouteContext context, IRedbTransaction tx) SetupRouteWithRedb(string? name)
    {
        var redb = Substitute.For<IRedbService>();
        var ctx = Substitute.For<IRedbContext>();
        var tx = Substitute.For<IRedbTransaction>();
        tx.IsActive.Returns(true);
        ctx.BeginTransactionAsync().Returns(Task.FromResult(tx));
        redb.Context.Returns(ctx);

        var context = Substitute.For<IRouteContext>();
        if (string.IsNullOrEmpty(name))
        {
            context.GetService<IRedbService>().Returns(redb);
        }
        else
        {
            // Singleton fallback path (no factory).
            context.GetFromRegistry<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(Arg.Any<string>())
                .Returns((Microsoft.Extensions.DependencyInjection.IServiceScopeFactory?)null);
            context.GetFromRegistry<IRedbService>("redb:" + name).Returns(redb);
        }

        var route = Substitute.For<IRouteDefinition>();
        route.GetContext().Returns(context);

        return (route, context, tx);
    }

    private sealed class ProcessorHolder
    {
        public Func<IExchange, CancellationToken, Task>? Captured;
    }

    private static void WireProcessor(IRouteDefinition route, ProcessorHolder holder)
    {
        route.Process(Arg.Any<Func<IExchange, CancellationToken, Task>>())
            .Returns(ci => { holder.Captured = ci.Arg<Func<IExchange, CancellationToken, Task>>(); return route; });
    }

    private static IExchange CreateExchange()
    {
        var ex = Substitute.For<IExchange>();
        ex.Properties.Returns(new Dictionary<string, object?>());
        return ex;
    }

    private static ConcurrentDictionary<string, ITransactedAction> GetActionsBag(IExchange exchange)
    {
        exchange.Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out var raw)
            .Should().BeTrue("TRANSACT_ACTION dictionary should be initialized by BeginRedbTransaction");
        return raw.Should().BeOfType<ConcurrentDictionary<string, ITransactedAction>>().Which;
    }
}
