using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for Bean / Service Activator DSL, step creation, and method invocation.
/// </summary>
public class BeanTests
{
    // ══════════════════════════════════════════════════════════════
    // DSL — async with CancellationToken
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_AsyncWithCt_AddsBeanStep()
    {
        var def = new RouteDefinition();

        def.Bean<ITestService>(async (svc, exchange, ct) =>
        {
            exchange.In.Body = await svc.ProcessAsync(exchange.In.Body!.ToString()!, ct);
        });

        var beanDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<BeanDefinition>().Subject;
        beanDef.ServiceType.Should().Be(typeof(ITestService));
        beanDef.Method.Should().NotBeNull();
    }

    [Fact]
    public void DSL_AsyncWithCt_NullMethod_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.Bean<ITestService>((Func<ITestService, IExchange, CancellationToken, Task>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ══════════════════════════════════════════════════════════════
    // DSL — async without CancellationToken
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_AsyncNoCt_AddsBeanStep()
    {
        var def = new RouteDefinition();

        def.Bean<ITestService>(async (svc, exchange) =>
        {
            exchange.In.Body = await svc.ValidateAsync(exchange.In.Body!.ToString()!);
        });

        var beanDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<BeanDefinition>().Subject;
        beanDef.ServiceType.Should().Be(typeof(ITestService));
    }

    [Fact]
    public void DSL_AsyncNoCt_NullMethod_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.Bean<ITestService>((Func<ITestService, IExchange, Task>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ══════════════════════════════════════════════════════════════
    // DSL — synchronous
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_Sync_AddsBeanStep()
    {
        var def = new RouteDefinition();

        def.Bean<ITestService>((svc, exchange) =>
        {
            exchange.In.Headers["value"] = svc.GetValue();
        });

        var beanDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<BeanDefinition>().Subject;
        beanDef.ServiceType.Should().Be(typeof(ITestService));
    }

    [Fact]
    public void DSL_Sync_NullMethod_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.Bean<ITestService>((Action<ITestService, IExchange>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ══════════════════════════════════════════════════════════════
    // BeanStep — normalized method delegates
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BeanStep_AsyncWithCt_InvokesMethod()
    {
        var service = new TestService();
        var exchange = Exchange.Create(new Message("hello"), null);

        var def = new RouteDefinition();
        def.From("direct://test");
        def.Bean<ITestService>(async (svc, e, ct) =>
        {
            e.In.Body = await svc.ProcessAsync(e.In.Body!.ToString()!, ct);
        });

        var step = def.Steps.OfType<BeanStep>().Single();
        await step.Method(service, exchange, CancellationToken.None);

        exchange.In.Body.Should().Be("hello_processed");
    }

    [Fact]
    public async Task BeanStep_AsyncNoCt_InvokesMethod()
    {
        var service = new TestService();
        var exchange = Exchange.Create(new Message("data"), null);

        var def = new RouteDefinition();
        def.From("direct://test");
        def.Bean<ITestService>(async (svc, e) =>
        {
            e.In.Body = await svc.ValidateAsync(e.In.Body!.ToString()!);
        });

        var step = def.Steps.OfType<BeanStep>().Single();
        await step.Method(service, exchange, CancellationToken.None);

        exchange.In.Body.Should().Be("data_validated");
    }

    [Fact]
    public async Task BeanStep_Sync_InvokesMethod()
    {
        var service = new TestService();
        var exchange = Exchange.Create(new Message("test"), null);

        var def = new RouteDefinition();
        def.From("direct://test");
        def.Bean<ITestService>((svc, e) =>
        {
            e.In.Headers["value"] = svc.GetValue();
        });

        var step = def.Steps.OfType<BeanStep>().Single();
        await step.Method(service, exchange, CancellationToken.None);

        exchange.In.Headers["value"].Should().Be(42);
    }

    // ══════════════════════════════════════════════════════════════
    // BeanStep — service type correctly captured
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void BeanStep_CapturesCorrectServiceType_ForEachOverload()
    {
        var def = new RouteDefinition();

        def.Bean<ITestService>(async (svc, e, ct) => { await Task.CompletedTask; });
        def.Bean<IAnotherService>(async (svc, e) => { await Task.CompletedTask; });
        def.Bean<ITestService>((svc, e) => { });

        var beanDefs = def.Outputs.OfType<BeanDefinition>().ToList();
        beanDefs.Should().HaveCount(3);
        beanDefs[0].ServiceType.Should().Be(typeof(ITestService));
        beanDefs[1].ServiceType.Should().Be(typeof(IAnotherService));
        beanDefs[2].ServiceType.Should().Be(typeof(ITestService));
    }

    // ══════════════════════════════════════════════════════════════
    // DSL — chaining
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_Bean_ReturnsSameDefinitionForChaining()
    {
        var def = new RouteDefinition();

        var result = def.Bean<ITestService>((svc, e) => { });

        result.Should().BeSameAs(def);
    }

    // ══════════════════════════════════════════════════════════════
    // Test helpers
    // ══════════════════════════════════════════════════════════════

    public interface ITestService
    {
        Task<string> ProcessAsync(string input, CancellationToken ct);
        Task<string> ValidateAsync(string input);
        int GetValue();
    }

    public interface IAnotherService
    {
        void DoWork();
    }

    private sealed class TestService : ITestService
    {
        public Guid InstanceId { get; } = Guid.NewGuid();

        public Task<string> ProcessAsync(string input, CancellationToken ct)
            => Task.FromResult($"{input}_processed");

        public Task<string> ValidateAsync(string input)
            => Task.FromResult($"{input}_validated");

        public int GetValue() => 42;
    }
}
