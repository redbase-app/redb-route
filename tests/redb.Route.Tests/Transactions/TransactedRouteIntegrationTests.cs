using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Transactions;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// Integration tests that verify the DSL → OldRouteCompiler → TransactedProcessor pipeline.
/// Uses real RouteContext with direct:// components to test the full compilation flow.
/// </summary>
public class TransactedRouteIntegrationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

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

    // ── DSL records TransactedStep ──

    [Fact]
    public void Transacted_SetsIsTransactedFlag()
    {
        var def = new RouteDefinition();
        def.From("direct://in").Transacted();

        def.IsTransacted.Should().BeTrue();
    }

    [Fact]
    public void Transacted_WithPolicy_RecordsPolicy()
    {
        var def = new RouteDefinition();
        def.From("direct://in").Transacted(TransactionPolicy.RequiresNew);

        var txDef = def.Outputs.OfType<TransactionDefinition>().FirstOrDefault();
        txDef.Should().NotBeNull();
        txDef!.Policy.Should().BeSameAs(TransactionPolicy.RequiresNew);
    }

    [Fact]
    public void Transacted_WithName_ParsesPolicy()
    {
        var def = new RouteDefinition();
        def.From("direct://in").Transacted("RequiresNew");

        var txDef = def.Outputs.OfType<TransactionDefinition>().FirstOrDefault();
        txDef.Should().NotBeNull();
        txDef!.Policy!.ScopeOption.Should().Be(System.Transactions.TransactionScopeOption.RequiresNew);
    }

    [Fact]
    public void Transacted_WithInvalidName_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.From("direct://in").Transacted("Bogus");

        act.Should().Throw<ArgumentException>().WithMessage("*Unknown transaction policy*");
    }

    [Fact]
    public void Transacted_WithNullPolicy_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.From("direct://in").Transacted((TransactionPolicy)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Transacted_WithNullName_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.From("direct://in").Transacted((string)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Compiler integrates TransactedProcessor ──

    [Fact]
    public async Task Transacted_Route_CommitsActionsOnSuccess()
    {
        IExchange? captured = null;
        var trackingAction = new TrackingTransactedAction();

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-in")
                .Transacted()
                .Process((ex, _) =>
                {
                    // Simulate a transport registering a deferred action
                    var dict = (ConcurrentDictionary<string, ITransactedAction>)
                        ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
                    dict["test-transport"] = trackingAction;
                    return Task.CompletedTask;
                })
                .To("direct://tx-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-out")
                .Process(ex => captured = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://tx-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "transacted-payload" });
        await producer.Process(exchange);

        captured.Should().NotBeNull();
        trackingAction.Committed.Should().BeTrue();
        trackingAction.RolledBack.Should().BeFalse();
    }

    [Fact]
    public async Task Transacted_Route_RollsBackOnFailure()
    {
        var trackingAction = new TrackingTransactedAction();

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-fail-in")
                .Transacted()
                .Process((ex, _) =>
                {
                    var dict = (ConcurrentDictionary<string, ITransactedAction>)
                        ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
                    dict["test-transport"] = trackingAction;
                    throw new InvalidOperationException("Simulated failure");
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://tx-fail-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message { Body = "will-fail" });
        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Simulated failure");
        trackingAction.RolledBack.Should().BeTrue();
        trackingAction.Committed.Should().BeFalse();
    }

    [Fact]
    public async Task Transacted_WithRequiresNewPolicy_Works()
    {
        IExchange? captured = null;
        var trackingAction = new TrackingTransactedAction();

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-rn-in")
                .Transacted(TransactionPolicy.RequiresNew)
                .Process((ex, _) =>
                {
                    var dict = (ConcurrentDictionary<string, ITransactedAction>)
                        ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
                    dict["transport"] = trackingAction;
                    return Task.CompletedTask;
                })
                .To("direct://tx-rn-out");
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-rn-out")
                .Process(ex => captured = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://tx-rn-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "rn" }));

        captured.Should().NotBeNull();
        trackingAction.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task Transacted_WithStringPolicy_Works()
    {
        var trackingAction = new TrackingTransactedAction();

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-str-in")
                .Transacted("Required")
                .Process((ex, _) =>
                {
                    var dict = (ConcurrentDictionary<string, ITransactedAction>)
                        ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
                    dict["transport"] = trackingAction;
                    return Task.CompletedTask;
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://tx-str-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "str-policy" }));

        trackingAction.Committed.Should().BeTrue();
    }

    // ── Transacted + Retry combination ──

    [Fact]
    public async Task Transacted_WithRetry_RetriesThenTransacts()
    {
        var callCount = 0;
        var trackingAction = new TrackingTransactedAction();

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-retry-in")
                .Transacted()
                .Retry(2, TimeSpan.FromMilliseconds(10))
                .Process((ex, _) =>
                {
                    var dict = (ConcurrentDictionary<string, ITransactedAction>)
                        ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
                    dict["transport"] = trackingAction;

                    callCount++;
                    if (callCount < 2)
                        throw new InvalidOperationException("Transient failure");
                    return Task.CompletedTask;
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://tx-retry-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "retry-tx" }));

        callCount.Should().Be(2);
        // After retry succeeds, transaction commits
        trackingAction.Committed.Should().BeTrue();
    }

    // ── Transacted + DeadLetterChannel combination ──

    [Fact]
    public async Task Transacted_WithDLC_SendsToDeadLetterOnFailure()
    {
        IExchange? deadLettered = null;
        var trackingAction = new TrackingTransactedAction();

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-dlc-in")
                .Transacted()
                .DeadLetterChannel("direct://tx-dlc-dead")
                .Process((ex, _) =>
                {
                    var dict = (ConcurrentDictionary<string, ITransactedAction>)
                        ex.Properties[TransactedProcessor.TransactActionPropertyKey]!;
                    dict["transport"] = trackingAction;
                    throw new InvalidOperationException("Fatal");
                });
        });

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-dlc-dead")
                .Process(ex => deadLettered = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://tx-dlc-in").CreateProducer();
        await producer.Start();

        // DLC should catch the exception, so no throw expected
        await producer.Process(new Exchange(new Message { Body = "dlc-tx" }));

        deadLettered.Should().NotBeNull();
        // Transaction rolls back because the inner processing failed
        trackingAction.RolledBack.Should().BeTrue();
    }

    // ── Exchange property key ──

    [Fact]
    public async Task Transacted_Route_SetsTransactActionProperty()
    {
        object? propertyValue = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-prop-in")
                .Transacted()
                .Process(ex =>
                {
                    ex.Properties.TryGetValue(TransactedProcessor.TransactActionPropertyKey, out propertyValue);
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://tx-prop-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = "check-prop" }));

        propertyValue.Should().NotBeNull();
        propertyValue.Should().BeOfType<ConcurrentDictionary<string, ITransactedAction>>();
    }
}
