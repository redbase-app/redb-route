using System.Transactions;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// The agent inside a route transaction (a route's <c>.Transacted()</c>). Its database work and deferred
/// sends, its tools' included, belong to that transaction and roll back with it. Three things do not: an
/// external tool, whose action no rollback can take back, is not run there; the budget, whose tokens are
/// paid whatever happens to the transaction, is recorded outside it; and the shadow run stays out of it.
/// </summary>
[Trait("Category", "Governance")]
public sealed class RouteTransactionTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Input = """{"q":"x"}""";

    private static LlmConnectionFactory Factory(FakeProvider provider) => new()
    {
        Provider = "fake",
        ModelId = provider.ModelId,
        PrebuiltProvider = provider
    };

    private static AgentEngine Engine(IBudgetEnforcer? budget = null, IShadowRunner? shadow = null) => new(
        logger: null,
        producerTemplate: null,
        observer: null,
        budget: budget,
        approval: null,
        redaction: null,
        shadow: shadow,
        conversation: null,
        idempotency: null,
        approvalStore: null);

    private static AgentRequest Request(FakeProvider provider) => new()
    {
        Factory = Factory(provider),
        Exchange = new Exchange(new Message("status?")),
        UserContent = [new LlmTextBlock("status?")]
    };

    [Fact]
    public async Task ExternalTool_InsideTransactedRoute_IsNotRun()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("send_mail", Input, "tu_1")
            .EnqueueText("done");
        var spy = new ToolInvocationSpy();
        var tool = new EchoToolRoute("send_mail", "Send an e-mail.", Schema, """{"sent":true}""",
            sideEffect: ToolSideEffect.External);
        var routeWasTransacted = false;

        await using var host = LiveLlmHost.Build(spy)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .Transacted()
            .Process(_ => routeWasTransacted = Transaction.Current is not null)
            .To(LlmDsl.Factory("demo").Tools("send_mail").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Mail the report.");

        routeWasTransacted.Should().BeTrue("the agent step must run inside the route's transaction, or this test proves nothing");
        tool.CapturedInputs.Should().BeEmpty(
            "an irreversible action does not run inside a transaction that can still roll back");
        spy.ForTool("send_mail").Should().ContainSingle(
            i => i.Skipped && i.SkipReason == ToolSkipReasons.ExternalInTransaction,
            "the audit channel records why the tool did not run");

        var result = provider.SentMessages.Should().HaveCount(2).And.Subject.ElementAt(1)
            .SelectMany(m => m.Content).OfType<LlmToolResultBlock>().Should().ContainSingle().Subject;
        result.IsError.Should().BeTrue();
        result.OutputJson.Should().Contain(ToolResultErrors.ExternalInTransaction,
            "the model is told the tool could not run, so it can answer without it");
    }

    [Fact]
    public async Task ExternalTool_WithoutTransaction_Runs()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("send_mail", Input, "tu_1")
            .EnqueueText("done");
        var tool = new EchoToolRoute("send_mail", "Send an e-mail.", Schema, """{"sent":true}""",
            sideEffect: ToolSideEffect.External);

        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("send_mail").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Mail the report.");

        tool.CapturedInputs.Should().HaveCount(1, "outside a transaction nothing can roll back under the action");
    }

    [Fact]
    public async Task MutatingTool_InsideTransactedRoute_Runs()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("write", Input, "tu_1")
            .EnqueueText("done");
        var tool = new EchoToolRoute("write", "Mutate something.", Schema, """{"answer":"ok"}""",
            sideEffect: ToolSideEffect.Mutating);

        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .Transacted()
            .To(LlmDsl.Factory("demo").Tools("write").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Mutate.");

        tool.CapturedInputs.Should().HaveCount(1,
            "a mutating tool's writes roll back with the transaction, so it belongs inside it");
    }

    [Fact]
    public async Task Budget_InsideTransaction_IsRecordedOutsideIt()
    {
        var sawTransactionAtProvider = false;
        var provider = new FakeProvider
        {
            OnCall = (_, _) =>
            {
                sawTransactionAtProvider = Transaction.Current is not null;
                return Task.CompletedTask;
            }
        }.EnqueueText("done");
        var budget = new SpyBudgetEnforcer();

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await Engine(budget: budget).RunAsync(Request(provider));
            scope.Complete();
        }

        sawTransactionAtProvider.Should().BeTrue("the run itself is inside the transaction, or this test proves nothing");
        budget.PreChecks.Should().Be(1);
        budget.PostChecks.Should().Be(1);
        budget.SawAmbientTransaction.Should().HaveCount(2).And.OnlyContain(seen => !seen,
            "tokens are paid when the provider answers; a rollback must not un-count them");
    }

    /// <summary>Reports whether the shadow run saw an ambient transaction.</summary>
    private sealed class ShadowSpy : IShadowRunner
    {
        public TaskCompletionSource<bool> SawTransaction { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Enabled => true;

        public Task RunAsync(ILlmProvider primary, LlmRequest request, LlmResponse primaryResponse, CancellationToken ct = default)
        {
            SawTransaction.TrySetResult(Transaction.Current is not null);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ShadowRun_DoesNotInheritTheTransaction()
    {
        var provider = new FakeProvider().EnqueueText("done");
        var shadow = new ShadowSpy();

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await Engine(shadow: shadow).RunAsync(Request(provider));
            scope.Complete();
        }

        (await shadow.SawTransaction.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse(
            "the shadow runs beside the primary run: it must not write into its transaction");
    }
}
