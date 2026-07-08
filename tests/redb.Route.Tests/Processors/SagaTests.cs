using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for Saga processor, DSL, definition, and step creation.
/// </summary>
public class SagaTests
{
    private static IExchange CreateExchange(object? body = null)
        => Exchange.Create(new Message(body), null);

    // ══════════════════════════════════════════════════════════════
    // SagaDefinition validation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Definition_NoSteps_CreateProcessorThrows()
    {
        var def = new SagaDefinition();
        var act = () => def.CreateProcessor(new RouteContext());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Definition_NullSyncAction_Throws()
    {
        var def = new SagaDefinition();
        var act = () => def.Step((Action<IExchange>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Definition_NullAsyncAction_Throws()
    {
        var def = new SagaDefinition();
        var act = () => def.Step((Func<IExchange, CancellationToken, Task>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Definition_NullSyncCompensate_Throws()
    {
        var def = new SagaDefinition();
        var act = () => def.Step(e => { }, (Action<IExchange>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Definition_NullAsyncCompensate_Throws()
    {
        var def = new SagaDefinition();
        Func<IExchange, CancellationToken, Task> action = (e, ct) => Task.CompletedTask;
        var act = () => def.Step(action, (Func<IExchange, CancellationToken, Task>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Definition_NullSyncCompletion_Throws()
    {
        var def = new SagaDefinition();
        var act = () => def.OnCompletion((Action<IExchange>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Definition_NullAsyncCompletion_Throws()
    {
        var def = new SagaDefinition();
        var act = () => def.OnCompletion((Func<IExchange, CancellationToken, Task>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Definition_SingleStep_RecordsEntry()
    {
        var def = new SagaDefinition();
        def.Step(e => { });
        def.Entries.Should().HaveCount(1);
        def.CompletionCallback.Should().BeNull();
    }

    [Fact]
    public void Definition_MultipleSteps_RecordsInOrder()
    {
        var def = new SagaDefinition();
        def.Step(e => { }, e => { });
        def.Step(e => { });
        def.Step((e, ct) => Task.CompletedTask, (e, ct) => Task.CompletedTask);
        def.Entries.Should().HaveCount(3);
        def.Entries[0].Compensate.Should().NotBeNull();
        def.Entries[1].Compensate.Should().BeNull();
        def.Entries[2].Compensate.Should().NotBeNull();
    }

    [Fact]
    public void Definition_WithCompletion_AttachesCallback()
    {
        var def = new SagaDefinition();
        def.Step(e => { });
        def.OnCompletion(e => { });
        def.CompletionCallback.Should().NotBeNull();
    }

    // ══════════════════════════════════════════════════════════════
    // SagaProcessor — all steps succeed
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_AllStepsSucceed_ExecutesInOrder()
    {
        var log = new List<string>();
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => { log.Add("A"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("A-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => { log.Add("B"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("B-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => { log.Add("C"); return Task.CompletedTask; }, null),
        };
        var processor = new SagaProcessor(steps);
        var exchange = CreateExchange();

        await processor.Process(exchange);

        log.Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task Processor_AllSucceed_CompletionCallbackInvoked()
    {
        var completed = false;
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => Task.CompletedTask, null),
        };
        var processor = new SagaProcessor(steps, (e, ct) => { completed = true; return Task.CompletedTask; });
        var exchange = CreateExchange();

        await processor.Process(exchange);

        completed.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════
    // SagaProcessor — failure triggers compensation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_SecondStepFails_CompensatesFirstOnly()
    {
        var log = new List<string>();
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => { log.Add("A"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("A-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => throw new InvalidOperationException("boom"),
                              (e, ct) => { log.Add("B-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => { log.Add("C"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("C-comp"); return Task.CompletedTask; }),
        };
        var processor = new SagaProcessor(steps);
        var exchange = CreateExchange();

        var act = () => processor.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        // Only A executed, B failed (not counted as completed), C never ran
        // Compensation: only A-comp runs (B wasn't completed)
        log.Should().Equal("A", "A-comp");
    }

    [Fact]
    public async Task Processor_ThirdStepFails_CompensatesSecondThenFirst()
    {
        var log = new List<string>();
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => { log.Add("A"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("A-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => { log.Add("B"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("B-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => throw new InvalidOperationException("fail"),
                              (e, ct) => { log.Add("C-comp"); return Task.CompletedTask; }),
        };
        var processor = new SagaProcessor(steps);

        var act = () => processor.Process(CreateExchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        // A, B executed; C failed. Compensation runs for B then A (reverse).
        log.Should().Equal("A", "B", "B-comp", "A-comp");
    }

    [Fact]
    public async Task Processor_Failure_CompletionCallbackNotInvoked()
    {
        var completed = false;
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => throw new InvalidOperationException("fail"), null),
        };
        var processor = new SagaProcessor(steps, (e, ct) => { completed = true; return Task.CompletedTask; });

        var act = () => processor.Process(CreateExchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        completed.Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    // SagaProcessor — forward-only steps (no compensation)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_ForwardOnlyStep_SkippedDuringCompensation()
    {
        var log = new List<string>();
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => { log.Add("A"); return Task.CompletedTask; }, null),
            new SagaStepEntry((e, ct) => { log.Add("B"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("B-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => throw new InvalidOperationException("fail"),
                              (e, ct) => { log.Add("C-comp"); return Task.CompletedTask; }),
        };
        var processor = new SagaProcessor(steps);

        var act = () => processor.Process(CreateExchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        // A has no compensate → skipped; B has compensate → runs
        log.Should().Equal("A", "B", "B-comp");
    }

    // ══════════════════════════════════════════════════════════════
    // SagaProcessor — compensation failure is swallowed
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_CompensationFails_ContinuesRemainingCompensations()
    {
        var log = new List<string>();
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => { log.Add("A"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("A-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => { log.Add("B"); return Task.CompletedTask; },
                              (e, ct) => throw new InvalidOperationException("comp-fail")),
            new SagaStepEntry((e, ct) => throw new InvalidOperationException("step-fail"),
                              (e, ct) => { log.Add("C-comp"); return Task.CompletedTask; }),
        };
        var logger = Substitute.For<ILogger>();
        var processor = new SagaProcessor(steps, logger: logger);

        var act = () => processor.Process(CreateExchange());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("step-fail");

        // B-comp throws but A-comp still runs
        log.Should().Equal("A", "B", "A-comp");
    }

    // ══════════════════════════════════════════════════════════════
    // SagaProcessor — exchange body modification
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_StepsCanModifyExchangeBody()
    {
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => { e.In.Body = "step1"; return Task.CompletedTask; }, null),
            new SagaStepEntry((e, ct) => { e.In.Body = $"{e.In.Body}+step2"; return Task.CompletedTask; }, null),
        };
        var processor = new SagaProcessor(steps);
        var exchange = CreateExchange("init");

        await processor.Process(exchange);

        exchange.In.Body.Should().Be("step1+step2");
    }

    [Fact]
    public async Task Processor_CompensationCanModifyExchange()
    {
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => { e.In.Body = "step1"; return Task.CompletedTask; },
                              (e, ct) => { e.In.Body = "rolled-back"; return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => throw new InvalidOperationException("fail"), null),
        };
        var processor = new SagaProcessor(steps);
        var exchange = CreateExchange("init");

        var act = () => processor.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>();

        exchange.In.Body.Should().Be("rolled-back");
    }

    // ══════════════════════════════════════════════════════════════
    // Callback-style DSL recording
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Dsl_CallbackStyle_RecordsSagaStep()
    {
        var def = new RouteDefinition();
        def.Saga(saga => saga
            .Step(e => { }, e => { })
            .Step(e => { })
            .OnCompletion(e => { }));

        var sagaDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<SagaDefinition>().Subject;
        sagaDef.Entries.Should().HaveCount(2);
        sagaDef.CompletionCallback.Should().NotBeNull();
    }

    [Fact]
    public void Dsl_CallbackStyle_NullConfigureThrows()
    {
        var def = new RouteDefinition();
        var act = () => def.Saga((Action<ISagaDefinition>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Fluent chain DSL recording
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Dsl_FluentChain_RecordsSagaStep()
    {
        var def = new RouteDefinition();
        def.Saga()
            .SagaStep(e => { }, e => { })
            .SagaStep(e => { })
            .OnSagaCompletion(e => { })
            .EndSaga();

        var sagaDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<SagaDefinition>().Subject;
        sagaDef.Entries.Should().HaveCount(2);
        sagaDef.CompletionCallback.Should().NotBeNull();
    }

    [Fact]
    public void Dsl_FluentChain_End_ClosesScope()
    {
        var def = new RouteDefinition();
        def.Saga()
            .SagaStep(e => { }, e => { })
            .End();

        def.Outputs.Should().ContainSingle().Which.Should().BeOfType<SagaDefinition>();
    }

    [Fact]
    public void Dsl_FluentChain_AsyncSteps()
    {
        var def = new RouteDefinition();
        def.Saga()
            .SagaStep(
                (e, ct) => Task.CompletedTask,
                (e, ct) => Task.CompletedTask)
            .SagaStep((e, ct) => Task.CompletedTask)
            .EndSaga();

        var sagaDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<SagaDefinition>().Subject;
        sagaDef.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void Dsl_FluentChain_SagaStepOutsideScope_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");
        var act = () => def.SagaStep(e => { });
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Dsl_FluentChain_EndSagaOutsideScope_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");
        var act = () => def.EndSaga();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Dsl_FluentChain_OnSagaCompletionOutsideScope_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");
        var act = () => def.OnSagaCompletion(e => { });
        act.Should().Throw<InvalidOperationException>();
    }

    // ══════════════════════════════════════════════════════════════
    // First step failure — no compensations to run
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_FirstStepFails_NoCompensation()
    {
        var log = new List<string>();
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => throw new InvalidOperationException("first-fail"),
                              (e, ct) => { log.Add("A-comp"); return Task.CompletedTask; }),
            new SagaStepEntry((e, ct) => { log.Add("B"); return Task.CompletedTask; },
                              (e, ct) => { log.Add("B-comp"); return Task.CompletedTask; }),
        };
        var processor = new SagaProcessor(steps);

        var act = () => processor.Process(CreateExchange());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("first-fail");

        // First step fails → 0 completed → no compensations
        log.Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════
    // Single step saga — success and failure
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Processor_SingleStepSuccess()
    {
        var executed = false;
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => { executed = true; return Task.CompletedTask; }, null),
        };
        var processor = new SagaProcessor(steps);

        await processor.Process(CreateExchange());

        executed.Should().BeTrue();
    }

    [Fact]
    public async Task Processor_NoCompletionCallback_DoesNotThrow()
    {
        var steps = new[]
        {
            new SagaStepEntry((e, ct) => Task.CompletedTask, null),
        };
        var processor = new SagaProcessor(steps, onCompletion: null);
        var exchange = CreateExchange();

        await processor.Process(exchange);
        // No exception = success
    }
}
