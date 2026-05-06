using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for Saga using full route compilation and pipeline execution.
/// </summary>
[Trait("Category", "Integration")]
public class SagaIntegrationTests
{
    private readonly RouteContext _context = new();

    private static IExchange CreateExchange(object? body = null)
        => Exchange.Create(new Message(body), null);

    // ══════════════════════════════════════════════════════════════
    // Callback style — compiled pipeline: all steps succeed
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Callback_AllSucceed_RunsAllSteps()
    {
        var log = new List<string>();
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga(saga => saga
            .Step(e => log.Add("A"), e => log.Add("A-comp"))
            .Step(e => log.Add("B"), e => log.Add("B-comp"))
            .Step(e => log.Add("C")));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);
        var exchange = CreateExchange();

        await pipeline.Process(exchange);

        log.Should().Equal("A", "B", "C");
    }

    // ══════════════════════════════════════════════════════════════
    // Callback style — compiled pipeline: failure triggers compensation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Callback_Failure_CompensatesInReverse()
    {
        var log = new List<string>();
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga(saga => saga
            .Step(e => log.Add("A"), e => log.Add("A-comp"))
            .Step(e => log.Add("B"), e => log.Add("B-comp"))
            .Step(e => throw new InvalidOperationException("boom"), e => log.Add("C-comp")));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);
        var exchange = CreateExchange();

        var act = () => pipeline.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>();

        log.Should().Equal("A", "B", "B-comp", "A-comp");
    }

    // ══════════════════════════════════════════════════════════════
    // Callback style — with completion callback
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Callback_WithCompletion_InvokedOnSuccess()
    {
        var completed = false;
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga(saga => saga
            .Step(e => { })
            .OnCompletion(e => completed = true));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        await pipeline.Process(CreateExchange());

        completed.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════
    // Fluent chain — compiled pipeline: all steps succeed
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FluentChain_AllSucceed_RunsAllSteps()
    {
        var log = new List<string>();
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga()
            .SagaStep(e => log.Add("X"), e => log.Add("X-comp"))
            .SagaStep(e => log.Add("Y"))
            .EndSaga();

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        await pipeline.Process(CreateExchange());

        log.Should().Equal("X", "Y");
    }

    // ══════════════════════════════════════════════════════════════
    // Fluent chain — failure triggers compensation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FluentChain_Failure_CompensatesInReverse()
    {
        var log = new List<string>();
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga()
            .SagaStep(e => log.Add("A"), e => log.Add("A-comp"))
            .SagaStep(e => throw new InvalidOperationException("fail"), e => log.Add("B-comp"))
            .EndSaga();

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var act = () => pipeline.Process(CreateExchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        log.Should().Equal("A", "A-comp");
    }

    // ══════════════════════════════════════════════════════════════
    // Fluent chain with End() — closes scope
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FluentChain_End_ClosesScope()
    {
        var log = new List<string>();
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga()
            .SagaStep(e => log.Add("done"))
            .End();
        // Steps after saga
        def.Process(e => log.Add("after-saga"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        await pipeline.Process(CreateExchange());

        log.Should().Equal("done", "after-saga");
    }

    // ══════════════════════════════════════════════════════════════
    // Saga + downstream steps: saga failure does not run downstream
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Callback_SagaFailure_DownstreamNotExecuted()
    {
        var downstreamRan = false;
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga(saga => saga
            .Step(e => throw new InvalidOperationException("fail")));
        def.Process(e => downstreamRan = true);

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var act = () => pipeline.Process(CreateExchange());
        await act.Should().ThrowAsync<InvalidOperationException>();

        downstreamRan.Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    // Async steps — compiled pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Callback_AsyncSteps_AllSucceed()
    {
        var log = new List<string>();
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga(saga => saga
            .Step(async (e, ct) => { await Task.Yield(); log.Add("async-A"); },
                  async (e, ct) => { await Task.Yield(); log.Add("async-A-comp"); })
            .Step(async (e, ct) => { await Task.Yield(); log.Add("async-B"); }));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        await pipeline.Process(CreateExchange());

        log.Should().Equal("async-A", "async-B");
    }

    // ══════════════════════════════════════════════════════════════
    // Exchange body preserved across saga steps
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Callback_StepsModifyExchangeBody()
    {
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga(saga => saga
            .Step(e => e.In.Body = "first")
            .Step(e => e.In.Body = $"{e.In.Body}+second"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);
        var exchange = CreateExchange("init");

        await pipeline.Process(exchange);

        exchange.In.Body.Should().Be("first+second");
    }

    // ══════════════════════════════════════════════════════════════
    // Compensation restores exchange state
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Callback_CompensationRestoresBody()
    {
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga(saga => saga
            .Step(e => e.In.Body = "modified",
                  e => e.In.Body = "restored")
            .Step(e => throw new InvalidOperationException("fail")));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);
        var exchange = CreateExchange("original");

        var act = () => pipeline.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>();

        exchange.In.Body.Should().Be("restored");
    }

    // ══════════════════════════════════════════════════════════════
    // Fluent chain — async with completion
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FluentChain_AsyncWithCompletion()
    {
        var log = new List<string>();
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Saga()
            .SagaStep(
                async (e, ct) => { await Task.Yield(); log.Add("step"); },
                async (e, ct) => { await Task.Yield(); log.Add("comp"); })
            .OnSagaCompletion(async (e, ct) => { await Task.Yield(); log.Add("completed"); })
            .EndSaga();

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        await pipeline.Process(CreateExchange());

        log.Should().Equal("step", "completed");
    }

    // ══════════════════════════════════════════════════════════════
    // Saga preceded and followed by other steps
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Pipeline_SagaBetweenOtherSteps()
    {
        var log = new List<string>();
        var def = new RouteDefinition();
        def.From("direct://saga-test");
        def.Process(e => log.Add("before"));
        def.Saga(saga => saga
            .Step(e => log.Add("saga-step")));
        def.Process(e => log.Add("after"));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        await pipeline.Process(CreateExchange());

        log.Should().Equal("before", "saga-step", "after");
    }
}
