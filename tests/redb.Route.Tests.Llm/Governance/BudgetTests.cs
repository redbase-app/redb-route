using Microsoft.Extensions.DependencyInjection;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Extensions;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// Budgets (wave 4). Before this wave <c>AgentRequest.Budget</c> was filled nowhere, so every route
/// ran unbounded, and the cost dimension was a structural zero. These tests pin the three things that
/// make the feature real: the ceiling reaches the engine, a run that crosses it stops with a reason,
/// and a cost ceiling nobody can price fails fast instead of being silently ignored.
/// </summary>
[Trait("Category", "Governance")]
public sealed class BudgetTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Input = """{"q":"x"}""";
    private const string Reply = """{"answer":"42"}""";

    [Fact]
    public async Task RouteWithTokenBudget_StopsWithBudgetReason()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1", tokensIn: 10, tokensOut: 1)
            .EnqueueText("first answer", tokensIn: 500, tokensOut: 1)
            .EnqueueText("never asked for", tokensIn: 500, tokensOut: 1);

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply);
        var budgetSpy = new SpyBudgetEnforcer();

        await using var host = LiveLlmHost.Build(budget: budgetSpy)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).Budget(inputTokens: 100).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.");

        tool.CapturedInputs.Should().HaveCount(1, "the first iteration stayed inside the ceiling");
        provider.CallCount.Should().Be(2, "the run stopped after the iteration that crossed the ceiling");
        host.Mock("mock:done").ReceivedExchanges[0].In.Headers[LlmHeaders.ToolIterations]
            .Should().Be(2, "two iterations completed; the third provider call never happened");
        budgetSpy.StopReasons.Should().Contain(r => r != null && r.Contains("input-token budget exceeded"));
    }

    [Fact]
    public async Task RouteWithCostBudget_WithoutCostCalculator_FailsFast()
    {
        var provider = new FakeProvider().EnqueueText("never reached");

        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Budget(costUsd: 0.5m).AsUri())
            .To("mock:done"));

        var thrown = await Record.ExceptionAsync(() => host.SendAsync("direct:agent", "hello"));

        thrown.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("ICostCalculator",
                "the message must name what is missing, not just that something is wrong");
        provider.CallCount.Should().Be(0, "an unpriceable cost ceiling must be refused before any spend");
    }

    [Fact]
    public async Task RouteWithCostBudget_WithCalculator_StopsWithBudgetReason()
    {
        var provider = new FakeProvider()
            .EnqueueText("first answer", tokensIn: 1000, tokensOut: 1000)
            .EnqueueText("never asked for", tokensIn: 1000, tokensOut: 1000);
        var budgetSpy = new SpyBudgetEnforcer();

        await using var host = LiveLlmHost.Build(budget: budgetSpy, costCalculator: new FlatCostCalculator(0.01m))
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Budget(costUsd: 0.005m).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "hello");

        provider.CallCount.Should().Be(1, "the priced iteration crossed the ceiling, so the loop stopped");
        budgetSpy.StopReasons.Should().Contain(r => r != null && r.Contains("cost budget exceeded"));
    }

    [Fact]
    public void BudgetBuilder_EmitsOnlyTheDimensionsItWasGiven()
    {
        var outputOnly = LlmDsl.Factory("demo").Budget(outputTokens: 50).AsUri();

        outputOnly.Should().Contain("budgetOutputTokens=50");
        outputOnly.Should().NotContain("budgetInputTokens",
            "an unset dimension must stay absent — zero and \"not set\" must not become one state in the URI");
        outputOnly.Should().NotContain("budgetCostUsd");

        LlmDsl.Factory("demo").AsUri().Should().NotContain("budget",
            "a route that asked for no budget carries none");
    }

    [Fact]
    public void CustomBudgetEnforcer_SurvivesAddRedbLlmStorage()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRouteContext>(new RouteContext());
        services.AddRedbRouteLlm();

        var mine = new SpyBudgetEnforcer();
        services.AddSingleton<IBudgetEnforcer>(mine);

        services.AddRedbLlmStorage();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IBudgetEnforcer>().Should().BeSameAs(mine,
            "AddRedbLlmStorage must never take away a governance implementation the host registered");
    }

    [Fact]
    public void ShippedBudgetEnforcer_IsReplacedByStoreEnforcer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRouteContext>(new RouteContext());
        services.AddRedbRouteLlm();
        services.AddRedbLlmStorage();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IBudgetEnforcer>().Should().BeOfType<StoreBudgetEnforcer>(
            "with a store present the shipped per-run default is replaced — otherwise usage is never accumulated");
    }

    /// <summary>
    /// A zero ceiling means "no ceiling": the enforcer only enforces positive limits, and the option docs
    /// now say so. Pinned here so the documentation cannot drift back into promising a stop sign that the
    /// code does not implement.
    /// </summary>
    [Fact]
    public async Task ZeroBudget_MeansNoCeiling()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1", tokensIn: 500, tokensOut: 1)
            .EnqueueText("done", tokensIn: 500, tokensOut: 1);

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply);

        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).Budget(inputTokens: 0).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.");

        provider.CallCount.Should().Be(2,
            "a zero ceiling does not stop the run — 500 tokens per iteration and the loop still reaches the model's own stop reason");
    }
}