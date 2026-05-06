using System.Collections.Concurrent;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Transactions;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for features added to close DSL gaps:
/// 1. Loop copy mode
/// 2. RollbackAll DSL step
/// 3. ExceptionHandled DSL step
/// 4. RedeliveryPolicy class + DSL
/// 5. OnException(params Type[])
/// </summary>
public class GapFeatureTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ══════════════════════════════════════════
    // 1. Loop with copy: true — DSL integration
    // ══════════════════════════════════════════

    [Fact]
    public async Task Loop_CopyTrue_IsolatesIterations_Lambda()
    {
        var bodies = new List<int>();

        _context.AddRoutes(r =>
        {
            r.From("direct://loop-copy-in")
                .Loop(3, cfg =>
                {
                    cfg.Process(ex =>
                    {
                        var val = (int)(ex.In.Body ?? 0);
                        bodies.Add(val);
                        ex.In.Body = val + 100; // should NOT carry over
                    });
                }, copy: true)
                .To("direct://loop-copy-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://loop-copy-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://loop-copy-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(1)));

        // Each iteration sees original body = 1
        bodies.Should().AllBeEquivalentTo(1);
        // Last iteration result (1+100=101) is merged back
        received.Should().NotBeNull();
        ((int)received!.In.Body!).Should().Be(101);
    }

    [Fact]
    public async Task Loop_CopyFalse_MutationsAccumulate_Lambda()
    {
        var bodies = new List<int>();

        _context.AddRoutes(r =>
        {
            r.From("direct://loop-nocopy-in")
                .Loop(3, cfg =>
                {
                    cfg.Process(ex =>
                    {
                        var val = (int)(ex.In.Body ?? 0);
                        bodies.Add(val);
                        ex.In.Body = val + 1;
                    });
                })
                .To("direct://loop-nocopy-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://loop-nocopy-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://loop-nocopy-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(0)));

        bodies.Should().BeEquivalentTo(new[] { 0, 1, 2 });
        received.Should().NotBeNull();
        ((int)received!.In.Body!).Should().Be(3);
    }

    [Fact]
    public async Task Loop_CopyTrue_Fluent()
    {
        var bodies = new List<int>();

        _context.AddRoutes(r =>
        {
            r.From("direct://loop-copy-fluent-in")
                .Loop(3, copy: true)
                    .Process(ex =>
                    {
                        var val = (int)(ex.In.Body ?? 0);
                        bodies.Add(val);
                        ex.In.Body = val + 100;
                    })
                .End()
                .To("direct://loop-copy-fluent-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://loop-copy-fluent-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://loop-copy-fluent-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(5)));

        // Each iteration sees original body = 5
        bodies.Should().AllBeEquivalentTo(5);
        received.Should().NotBeNull();
        ((int)received!.In.Body!).Should().Be(105);
    }

    [Fact]
    public async Task Loop_CopyTrue_HeadersIsolated()
    {
        var headerValues = new List<string?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://loop-header-in")
                .Loop(3, cfg =>
                {
                    cfg.Process(ex =>
                    {
                        headerValues.Add(ex.In.Headers.TryGetValue("Counter", out var v) ? v?.ToString() : null);
                        ex.In.Headers["Counter"] = "modified";
                    });
                }, copy: true);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://loop-header-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("body")));

        // None of the iterations should see the "modified" header from a previous one
        headerValues.Should().AllSatisfy(v => v.Should().BeNull());
    }

    [Fact]
    public void Loop_CopyTrue_RecordedInStep_Lambda()
    {
        var def = new RouteDefinition();
        def.From("direct://x")
            .Loop(5, _ => { }, copy: true);

        def.Steps.OfType<LoopCountStep>().First().Copy.Should().BeTrue();
    }

    [Fact]
    public void Loop_CopyFalse_RecordedInStep_Lambda()
    {
        var def = new RouteDefinition();
        def.From("direct://x")
            .Loop(5, _ => { });

        def.Steps.OfType<LoopCountStep>().First().Copy.Should().BeFalse();
    }

    // ══════════════════════════════════════════
    // 2. RollbackAll DSL step
    // ══════════════════════════════════════════

    private sealed class TrackingAction : ITransactedAction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }

        public Task Commit(CancellationToken ct = default) { Committed = true; return Task.CompletedTask; }
        public Task Rollback(CancellationToken ct = default) { RolledBack = true; return Task.CompletedTask; }
    }

    [Fact]
    public async Task RollbackAll_RollsBackAllTransactedActions()
    {
        var action1 = new TrackingAction();
        var action2 = new TrackingAction();

        _context.AddRoutes(r =>
        {
            r.From("direct://rb-in")
                .Process(ex =>
                {
                    // Simulate transacted actions registered by TransactedProcessor
                    var dict = new ConcurrentDictionary<string, ITransactedAction>(StringComparer.OrdinalIgnoreCase);
                    dict["action1"] = action1;
                    dict["action2"] = action2;
                    ex.Properties[TransactedProcessor.TransactActionPropertyKey] = dict;
                })
                .RollbackAll()
                .To("direct://rb-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://rb-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://rb-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        action1.RolledBack.Should().BeTrue();
        action2.RolledBack.Should().BeTrue();
        action1.Committed.Should().BeFalse();
        action2.Committed.Should().BeFalse();

        received.Should().NotBeNull();
        received!.Properties.Should().ContainKey("RollbackOnly");
        received.Properties["RollbackOnly"].Should().Be(true);
    }

    [Fact]
    public async Task RollbackAll_NoActions_SetsRollbackOnlyWithoutError()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://rb-empty-in")
                .RollbackAll()
                .To("direct://rb-empty-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://rb-empty-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://rb-empty-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("data")));

        received.Should().NotBeNull();
        received!.Properties.Should().ContainKey("RollbackOnly");
    }

    [Fact]
    public void RollbackAll_RecordedInSteps()
    {
        var def = new RouteDefinition();
        def.From("direct://x").RollbackAll();

        def.Steps.OfType<RollbackAllStep>().Should().HaveCount(1);
    }

    // ══════════════════════════════════════════
    // 3. ExceptionHandled DSL step
    // ══════════════════════════════════════════

    [Fact]
    public async Task ExceptionHandled_ClearsExceptionAndSetsFlag()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://eh-in")
                .Process(ex =>
                {
                    ex.Exception = new InvalidOperationException("test error");
                })
                .ExceptionHandled()
                .To("direct://eh-out");
        });

        IExchange? received = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://eh-out")
                .Process(ex => received = ex);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://eh-in").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        await producer.Process(exchange);

        received.Should().NotBeNull();
        received!.Exception.Should().BeNull();
        received.ExceptionHandled.Should().BeTrue();
    }

    [Fact]
    public void ExceptionHandled_RecordedInSteps()
    {
        var def = new RouteDefinition();
        def.From("direct://x").ExceptionHandled();

        def.Steps.OfType<ExceptionHandledStep>().Should().HaveCount(1);
    }

    [Fact]
    public async Task ExceptionHandled_UsableOutsideOnExceptionScope()
    {
        // Key test: ExceptionHandled() is NOT restricted to OnException blocks
        var processed = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://eh-outside-in")
                .Process(ex => ex.Exception = new Exception("bad"))
                .ExceptionHandled()
                .Process(_ => processed = true);
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://eh-outside-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("go")));

        processed.Should().BeTrue();
    }

    // ══════════════════════════════════════════
    // 4. RedeliveryPolicy class + DSL
    // ══════════════════════════════════════════

    [Fact]
    public void RedeliveryPolicy_DefaultValues()
    {
        var policy = new RedeliveryPolicy();

        policy.MaximumRedeliveries.Should().Be(0);
        policy.RedeliveryDelay.Should().Be(TimeSpan.FromSeconds(1));
        policy.BackOffMultiplier.Should().Be(1.0);
        policy.UseExponentialBackOff.Should().BeFalse();
    }

    [Fact]
    public void RedeliveryPolicy_InitProperties()
    {
        var policy = new RedeliveryPolicy
        {
            MaximumRedeliveries = 5,
            RedeliveryDelay = TimeSpan.FromMilliseconds(200),
            BackOffMultiplier = 2.5,
            UseExponentialBackOff = true
        };

        policy.MaximumRedeliveries.Should().Be(5);
        policy.RedeliveryDelay.Should().Be(TimeSpan.FromMilliseconds(200));
        policy.BackOffMultiplier.Should().Be(2.5);
        policy.UseExponentialBackOff.Should().BeTrue();
    }

    [Fact]
    public void RedeliveryPolicy_AppliedInOnExceptionScope()
    {
        var policy = new RedeliveryPolicy
        {
            MaximumRedeliveries = 3,
            RedeliveryDelay = TimeSpan.FromMilliseconds(50),
            BackOffMultiplier = 2.0,
            UseExponentialBackOff = true
        };

        var def = new RouteDefinition();
        def.From("direct://x")
            .OnException<InvalidOperationException>()
                .RedeliveryPolicy(policy)
                .Handled()
            .End();

        var handlers = def.Steps.OfType<OnExceptionStep>().SelectMany(s => s.Handlers).ToList();
        handlers.Should().HaveCount(1);
        handlers[0].MaxRedeliveries.Should().Be(3);
        handlers[0].RedeliveryDelay.Should().Be(TimeSpan.FromMilliseconds(50));
        handlers[0].BackoffMultiplier.Should().Be(2.0);
        handlers[0].UseExponentialBackoff.Should().BeTrue();
    }

    [Fact]
    public void RedeliveryPolicy_OutsideOnException_Throws()
    {
        var policy = new RedeliveryPolicy { MaximumRedeliveries = 1 };
        var def = new RouteDefinition();
        def.From("direct://x");

        var act = () => def.RedeliveryPolicy(policy);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OnException*");
    }

    [Fact]
    public void RedeliveryPolicy_NullPolicy_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://x");

        var act = () => def.OnException<Exception>().RedeliveryPolicy(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task RedeliveryPolicy_IntegrationWithEngine()
    {
        var callCount = 0;
        var policy = new RedeliveryPolicy
        {
            MaximumRedeliveries = 2,
            RedeliveryDelay = TimeSpan.FromMilliseconds(10)
        };

        _context.AddRoutes(r =>
        {
            r.From("direct://rp-engine-in")
                .OnException<InvalidOperationException>()
                    .RedeliveryPolicy(policy)
                    .Handled()
                .End()
                .Process(_ =>
                {
                    callCount++;
                    throw new InvalidOperationException("retry me");
                });
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://rp-engine-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("go")));

        // 1 original + 2 redeliveries
        callCount.Should().Be(3);
    }

    // ══════════════════════════════════════════
    // 5. OnException(params Type[])
    // ══════════════════════════════════════════

    [Fact]
    public void OnException_ParamsTypes_RecordsMultipleHandlers()
    {
        var def = new RouteDefinition();
        def.From("direct://x")
            .OnException(typeof(InvalidOperationException), typeof(TimeoutException))
                .Handled()
                .MaximumRedeliveries(2)
            .End();

        var handlers = def.Steps.OfType<OnExceptionStep>().SelectMany(s => s.Handlers).ToList();
        handlers.Should().HaveCount(2);
        handlers[0].ExceptionType.Should().Be(typeof(InvalidOperationException));
        handlers[1].ExceptionType.Should().Be(typeof(TimeoutException));

        // Both share same config
        handlers[0].MaxRedeliveries.Should().Be(2);
        handlers[1].MaxRedeliveries.Should().Be(2);
        handlers[0].Handled.Should().BeTrue();
        handlers[1].Handled.Should().BeTrue();
    }

    [Fact]
    public void OnException_ParamsTypes_EmptyArray_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://x");

        var act = () => def.OnException(Array.Empty<Type>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OnException_ParamsTypes_NonExceptionType_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://x");

        var act = () => def.OnException(typeof(string));
        act.Should().Throw<ArgumentException>()
            .WithMessage("*String*not an Exception*");
    }

    [Fact]
    public async Task OnException_ParamsTypes_CatchesBothTypes()
    {
        var handledTypes = new List<string>();

        _context.AddRoutes(r =>
        {
            r.From("direct://multi-ex-in")
                .OnException(typeof(InvalidOperationException), typeof(ArgumentException))
                    .Handled()
                    .Process(ex => handledTypes.Add(ex.Exception!.GetType().Name))
                .End()
                .Process(_ => throw new InvalidOperationException("first"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://multi-ex-in").CreateProducer();
        await producer.Start();

        // Send message that throws InvalidOperationException
        await producer.Process(new Exchange(new Message("go")));
        handledTypes.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public async Task OnException_ParamsTypes_SecondTypeCaughtToo()
    {
        var handled = false;

        _context.AddRoutes(r =>
        {
            r.From("direct://multi-ex2-in")
                .OnException(typeof(InvalidOperationException), typeof(ArgumentException))
                    .Handled()
                    .Process(_ => handled = true)
                .End()
                .Process(_ => throw new ArgumentException("second type"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://multi-ex2-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("go")));

        handled.Should().BeTrue();
    }

    [Fact]
    public void OnException_ParamsTypes_RedeliveryPolicyAppliedToAll()
    {
        var policy = new RedeliveryPolicy
        {
            MaximumRedeliveries = 4,
            RedeliveryDelay = TimeSpan.FromMilliseconds(100),
            BackOffMultiplier = 3.0,
            UseExponentialBackOff = true
        };

        var def = new RouteDefinition();
        def.From("direct://x")
            .OnException(typeof(InvalidOperationException), typeof(TimeoutException))
                .RedeliveryPolicy(policy)
                .Handled()
            .End();

        var handlers = def.Steps.OfType<OnExceptionStep>().SelectMany(s => s.Handlers).ToList();
        handlers.Should().HaveCount(2);
        foreach (var h in handlers)
        {
            h.MaxRedeliveries.Should().Be(4);
            h.RedeliveryDelay.Should().Be(TimeSpan.FromMilliseconds(100));
            h.BackoffMultiplier.Should().Be(3.0);
            h.UseExponentialBackoff.Should().BeTrue();
        }
    }

    // ── Builder-level OnException(params Type[]) ──

    [Fact]
    public async Task OnException_ParamsTypes_BuilderLevel()
    {
        var handled = false;

        _context.AddRoutes(r =>
        {
            r.OnException(typeof(InvalidOperationException), typeof(TimeoutException))
                .Handled()
                .Process(_ => handled = true);

            r.From("direct://builder-multi-in")
                .Process(_ => throw new TimeoutException("timeout"));
        });

        await _context.Start();

        var producer = _context.GetEndpoint("direct://builder-multi-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("go")));

        handled.Should().BeTrue();
    }

    [Fact]
    public void OnException_ParamsTypes_BuilderLevel_EmptyThrows()
    {
        var builder = new TestParamsBuilder(b =>
        {
            b.CallOnException(Array.Empty<Type>());
        });

        var act = () => ((IRouteBuilder)builder).Configure(null!);
        act.Should().Throw<ArgumentException>();
    }

    private sealed class TestParamsBuilder : RouteBuilder
    {
        private readonly Action<TestParamsBuilder>? _configure;
        internal TestParamsBuilder(Action<TestParamsBuilder>? configure = null) => _configure = configure;
        protected override void Configure() => _configure?.Invoke(this);
        public IRouteDefinition CallOnException(params Type[] types) => OnException(types);
    }
}
