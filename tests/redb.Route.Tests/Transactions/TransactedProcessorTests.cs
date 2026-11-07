using System.Collections.Concurrent;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// Tests for <see cref="TransactedProcessor"/>.
/// </summary>
public class TransactedProcessorTests
{
    // ── Helpers ──

    private static Exchange CreateExchange(object? body = null) =>
        new(new Message { Body = body ?? "test" });

    private sealed class TrackingTransactedAction : ITransactedAction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }

        public Task Commit(CancellationToken ct = default)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public Task Rollback(CancellationToken ct = default)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingRollbackAction : ITransactedAction
    {
        public Task Commit(CancellationToken ct = default) => Task.CompletedTask;

        public Task Rollback(CancellationToken ct = default) =>
            throw new InvalidOperationException("Rollback failed deliberately.");
    }

    private const string TransportStepsKey = "test:transport-steps";

    /// <summary>
    /// Arranges a tracking action that a transport step registers once the block runs, the only place a deferred
    /// action can be registered (<see cref="TransactedActions.Register"/> refuses it outside a block).
    /// </summary>
    private static TrackingTransactedAction RegisterAction(IExchange exchange, string key = "test-action")
    {
        var action = new TrackingTransactedAction();
        RegisterInBlock(exchange, key, action);
        return action;
    }

    private static void RegisterInBlock(IExchange exchange, string key, ITransactedAction action)
    {
        if (!exchange.Properties.TryGetValue(TransportStepsKey, out var raw) ||
            raw is not List<(string Key, ITransactedAction Action)> steps)
        {
            steps = [];
            exchange.Properties[TransportStepsKey] = steps;
        }
        steps.Add((key, action));
    }

    /// <summary>The block's body: the transport steps register their actions first, then the rest of the route runs.</summary>
    private static IProcessor WithTransportSteps(IProcessor inner) => new DelegateProcessor(async (ex, ct) =>
    {
        if (ex.Properties.TryGetValue(TransportStepsKey, out var raw) && raw is List<(string Key, ITransactedAction Action)> steps)
        {
            foreach (var (key, action) in steps)
                TransactedActions.Register(ex, key, action, "test-transport");
        }
        await inner.Process(ex, ct);
    });

    // ── Constructor ──

    [Fact]
    public void Ctor_ThrowsOnNullInner()
    {
        var act = () => new TransactedProcessor(null!, TransactionPolicy.Default);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_ThrowsOnNullPolicy()
    {
        var inner = Substitute.For<IProcessor>();

        var act = () => new TransactedProcessor(WithTransportSteps(inner), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Success path ──

    [Fact]
    public async Task Process_CallsInnerProcessor()
    {
        var inner = Substitute.For<IProcessor>();
        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();

        await processor.Process(exchange);

        await inner.Received(1).Process(exchange, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_CommitsActionsOnSuccess()
    {
        var inner = Substitute.For<IProcessor>();
        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();
        var action = RegisterAction(exchange);

        await processor.Process(exchange);

        action.Committed.Should().BeTrue();
        action.RolledBack.Should().BeFalse();
    }

    [Fact]
    public async Task Process_CommitsMultipleActionsOnSuccess()
    {
        var inner = Substitute.For<IProcessor>();
        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();
        var action1 = RegisterAction(exchange, "kafka-topic-1");
        var action2 = RegisterAction(exchange, "redis-stream-1");

        await processor.Process(exchange);

        action1.Committed.Should().BeTrue();
        action2.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task Process_OpensTransactActionsDictionaryForTheBlockOnly()
    {
        object? inside = null;
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<IExchange>().Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out inside);
                return Task.CompletedTask;
            });
        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();

        // No TRANSACT_ACTION set: the processor creates it for the block and removes it when the block ends,
        // so a transacted step after the block cannot defer a send nobody would commit.
        await processor.Process(exchange);

        inside.Should().BeAssignableTo<ConcurrentDictionary<string, ITransactedAction>>();
        exchange.Properties.Should().NotContainKey(TransactedProcessor.TransactActionPropertyKey);
    }

    // ── Failure path ──

    [Fact]
    public async Task Process_RollsBackActionsOnException()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(x => throw new InvalidOperationException("Processing failed."));

        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();
        var action = RegisterAction(exchange);

        var act = () => processor.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>();
        action.RolledBack.Should().BeTrue();
        action.Committed.Should().BeFalse();
    }

    [Fact]
    public async Task Process_RollsBackMultipleActionsOnException()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(x => throw new ApplicationException("Boom"));

        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();
        var action1 = RegisterAction(exchange, "action-a");
        var action2 = RegisterAction(exchange, "action-b");

        var act = () => processor.Process(exchange);

        await act.Should().ThrowAsync<ApplicationException>();
        action1.RolledBack.Should().BeTrue();
        action2.RolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task Process_RollsBackOnCancellation()
    {
        var cts = new CancellationTokenSource();
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(x => throw new OperationCanceledException());

        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();
        var action = RegisterAction(exchange);

        var act = () => processor.Process(exchange, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        action.RolledBack.Should().BeTrue();
        action.Committed.Should().BeFalse();
    }

    // ── Rollback failure suppression ──

    [Fact]
    public async Task Process_SuppressesRollbackFailure_PropagatesOriginalException()
    {
        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(x => throw new InvalidOperationException("Original"));

        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();

        // Register a failing rollback action
        RegisterInBlock(exchange, "failing-rollback", new FailingRollbackAction());

        var act = () => processor.Process(exchange);

        // The original exception should propagate, not the rollback failure
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Original");
    }

    // ── Transport-registered actions during processing ──

    [Fact]
    public async Task Process_CommitsActionsRegisteredDuringProcessing()
    {
        // Simulate a transport that registers an action during processing
        var trackingAction = new TrackingTransactedAction();

        var inner = Substitute.For<IProcessor>();
        inner.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var dict = (ConcurrentDictionary<string, ITransactedAction>)
                    ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
                dict["kafka://orders"] = trackingAction;
                return Task.CompletedTask;
            });

        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();

        await processor.Process(exchange);

        trackingAction.Committed.Should().BeTrue();
    }

    // ── No actions registered ──

    [Fact]
    public async Task Process_SucceedsWithNoRegisteredActions()
    {
        var inner = Substitute.For<IProcessor>();
        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Default);
        var exchange = CreateExchange();

        // No actions registered — should still succeed
        await processor.Process(exchange);

        await inner.Received(1).Process(exchange, Arg.Any<CancellationToken>());
    }

    // ── Policy variations ──

    [Fact]
    public async Task Process_WorksWithRequiresNewPolicy()
    {
        var inner = Substitute.For<IProcessor>();
        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.RequiresNew);
        var exchange = CreateExchange();
        var action = RegisterAction(exchange);

        await processor.Process(exchange);

        action.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task Process_WorksWithSuppressPolicy()
    {
        var inner = Substitute.For<IProcessor>();
        var processor = new TransactedProcessor(WithTransportSteps(inner), TransactionPolicy.Suppress);
        var exchange = CreateExchange();
        var action = RegisterAction(exchange);

        await processor.Process(exchange);

        action.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task Process_WorksWithCustomPolicy()
    {
        var policy = new TransactionPolicy
        {
            ScopeOption = System.Transactions.TransactionScopeOption.RequiresNew,
            Timeout = TimeSpan.FromMinutes(1),
            IsolationLevel = System.Transactions.IsolationLevel.Serializable
        };

        var inner = Substitute.For<IProcessor>();
        var processor = new TransactedProcessor(WithTransportSteps(inner), policy);
        var exchange = CreateExchange();
        var action = RegisterAction(exchange);

        await processor.Process(exchange);

        action.Committed.Should().BeTrue();
    }

    // ── PropertyKey constant ──

    [Fact]
    public void TransactActionPropertyKey_IsCorrect()
    {
        TransactedProcessor.TransactActionPropertyKey.Should().Be("TRANSACT_ACTION");
    }
}
