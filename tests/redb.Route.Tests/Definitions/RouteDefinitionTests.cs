using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;
using redb.Route.Transactions;
using redb.Route.Validation;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 F3 — RouteDefinition skeleton (pipeline compilation).
/// </summary>
public class RouteDefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Exchange MakeExchange(object? body = null)
        => new Exchange(new Message { Body = body });

    // ── CreateProcessor pipeline ──────────────────────────────────────────────

    [Fact]
    public void CreateProcessor_EmptyOutputs_ReturnsDelegate()
    {
        var def = new RouteDefinition();
        def.CreateProcessor(_context).Should().BeOfType<DelegateProcessor>();
    }

    [Fact]
    public void CreateProcessor_SingleOutput_ReturnsThatProcessor()
    {
        var def = new RouteDefinition().To("direct:target");
        def.CreateProcessor(_context).Should().BeOfType<ToProcessor>();
    }

    [Fact]
    public void CreateProcessor_MultipleOutputs_ReturnsPipeline()
    {
        var def = new RouteDefinition()
            .To("direct:a")
            .To("direct:b");
        def.CreateProcessor(_context).Should().BeOfType<PipelineProcessor>();
    }

    // ── Fluent identity ───────────────────────────────────────────────────────

    [Fact]
    public void RouteId_Stored()
    {
        var def = new RouteDefinition().RouteId("my-route");
        def.GetRouteId().Should().Be("my-route");
    }

    [Fact]
    public void From_Stored()
    {
        var def = new RouteDefinition().From("kafka://orders");
        def.GetFromUri().Should().Be("kafka://orders");
    }

    [Fact]
    public void AutoStart_DefaultTrue()
    {
        new RouteDefinition().GetAutoStart().Should().BeTrue();
    }

    [Fact]
    public void AutoStart_CanBeSetFalse()
    {
        new RouteDefinition().AutoStart(false).GetAutoStart().Should().BeFalse();
    }

    // ── Leaf methods add to Outputs ───────────────────────────────────────────

    [Fact]
    public void To_AddsToDefinition()
    {
        var def = new RouteDefinition();
        def.To("direct:x");
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<ToDefinition>();
    }

    [Fact]
    public void SetBody_AddsSetBodyStaticDefinition()
    {
        var def = new RouteDefinition();
        def.SetBody("hello");
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<SetBodyStaticDefinition>();
    }

    [Fact]
    public void Chaining_AddsInOrder()
    {
        var def = new RouteDefinition()
            .SetBody("a")
            .SetHeader("x", 1)
            .To("direct:out");
        def.Outputs.Should().HaveCount(3);
    }

    // ── End-to-end pipeline execution ────────────────────────────────────────

    [Fact]
    public async Task Pipeline_ExecutesOutputsInOrder()
    {
        var log = new List<string>();
        var def = new RouteDefinition()
            .Process(_ => { log.Add("step1"); })
            .Process(_ => { log.Add("step2"); });

        var proc = def.CreateProcessor(_context);
        await proc.Process(MakeExchange());
        log.Should().Equal("step1", "step2");
    }

    [Fact]
    public async Task SetBody_ThenTransform_ProducesExpectedBody()
    {
        var def = new RouteDefinition()
            .SetBody("hello")
            .Transform(e => e.In.Body?.ToString()?.ToUpperInvariant());

        var ex = MakeExchange();
        await def.CreateProcessor(_context).Process(ex);
        ex.In.Body.Should().Be("HELLO");
    }

    [Fact]
    public async Task Stop_HaltsRemainingOutputs()
    {
        bool reached = false;
        var def = new RouteDefinition()
            .Stop()
            .Process(_ => { reached = true; });

        var ex = MakeExchange();
        await def.CreateProcessor(_context).Process(ex);
        reached.Should().BeFalse();
        ex.IsStopped.Should().BeTrue();
    }

    // ── AddOutput sets Parent on child ────────────────────────────────────────

    [Fact]
    public void AddOutput_SetsParentOnChild()
    {
        var def = new RouteDefinition();
        def.To("direct:x");
        def.Outputs[0].Parent.Should().BeSameAs(def);
    }

    [Fact]
    public async Task StreamCaching_WrapsStreamBodyForReread()
    {
        using var ms = new MemoryStream("hello"u8.ToArray());
        string? firstRead = null;
        string? secondRead = null;

        _context.AddRoutes(r =>
        {
            r.From("direct://stream-cache-test")
                .StreamCaching()
                .Process(async (e, ct) =>
                {
                    var s = (Stream)e.In.Body!;
                    using var sr = new StreamReader(s, leaveOpen: true);
                    firstRead = await sr.ReadToEndAsync(ct);
                })
                .Process(async (e, ct) =>
                {
                    // Without StreamCaching this read would return empty string
                    var s = (Stream)e.In.Body!;
                    s.Position = 0;
                    using var sr = new StreamReader(s, leaveOpen: true);
                    secondRead = await sr.ReadToEndAsync(ct);
                });
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://stream-cache-test").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message { Body = ms }));

        firstRead.Should().Be("hello");
        secondRead.Should().Be("hello");
    }

    // ── Loop ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loop_CountBased_RepeatsExactTimes()
    {
        int count = 0;
        var def = new RouteDefinition()
            .Loop(3)
                .Process(_ => count++)
            .EndLoop();

        await def.CreateProcessor(_context).Process(MakeExchange());
        count.Should().Be(3);
    }

    [Fact]
    public async Task Loop_ZeroCount_ExecutesNothing()
    {
        int count = 0;
        var def = new RouteDefinition()
            .Loop(0)
                .Process(_ => count++)
            .EndLoop();

        await def.CreateProcessor(_context).Process(MakeExchange());
        count.Should().Be(0);
    }

    [Fact]
    public async Task Loop_DynamicCount_ResolvesFromExchange()
    {
        int count = 0;
        var def = new RouteDefinition()
            .Loop(e => (int)e.In.Body!)
                .Process(_ => count++)
            .EndLoop();

        var ex = MakeExchange(5);
        await def.CreateProcessor(_context).Process(ex);
        count.Should().Be(5);
    }

    [Fact]
    public async Task Loop_WhileCondition_StopsWhenFalse()
    {
        int count = 0;
        var def = new RouteDefinition()
            .Loop(e => count < 4)
                .Process(_ => count++)
            .EndLoop();

        await def.CreateProcessor(_context).Process(MakeExchange());
        count.Should().Be(4);
    }

    [Fact]
    public void Loop_EndLoop_ReturnsParent()
    {
        var root = new RouteDefinition();
        var loopDef = root.Loop(1);
        loopDef.EndLoop().Should().BeSameAs(root);
    }

    // ── WireTap overloads ────────────────────────────────────────────────────

    [Fact]
    public void WireTap_WithOnPrepare_AddsWireTapDefinition()
    {
        var def = new RouteDefinition();
        def.WireTap("direct:tap", _ => { });
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<WireTapDefinition>();
    }

    [Fact]
    public void WireTap_WithNewBodyFactory_AddsWireTapDefinition()
    {
        var def = new RouteDefinition();
        def.WireTap("direct:tap", e => e.In.Body);
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<WireTapDefinition>();
    }

    [Fact]
    public void WireTap_WithOnPrepareAndBodyFactory_AddsWireTapDefinition()
    {
        var def = new RouteDefinition();
        def.WireTap("direct:tap", _ => { }, e => e.In.Body);
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<WireTapDefinition>();
    }

    // ── Transactions ───────────────────────────────────────────────────────────

    [Fact]
    public void BeginTransaction_AddsBeginTransactionDefinition()
    {
        var def = new RouteDefinition();
        def.BeginTransaction();
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<BeginTransactionDefinition>();
    }

    [Fact]
    public void BeginTransaction_WithPolicy_AddsBeginTransactionDefinition()
    {
        var def = new RouteDefinition();
        def.BeginTransaction(redb.Route.Transactions.TransactionPolicy.RequiresNew);
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<BeginTransactionDefinition>();
    }

    [Fact]
    public void CommitTransaction_AddsCommitTransactionDefinition()
    {
        var def = new RouteDefinition();
        def.CommitTransaction();
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<CommitTransactionDefinition>();
    }

    [Fact]
    public void RollbackTransaction_AddsRollbackTransactionDefinition()
    {
        var def = new RouteDefinition();
        def.RollbackTransaction();
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<RollbackTransactionDefinition>();
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_WithPredicate_AddsValidatePredicateDefinition()
    {
        var def = new RouteDefinition();
        def.Validate(_ => true, "must be true");
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<ValidatePredicateDefinition>();
    }

    [Fact]
    public void ValidateJsonSchema_WithString_AddsValidateJsonSchemaStringDefinition()
    {
        var def = new RouteDefinition();
        def.ValidateJsonSchema("""{ "type": "object" }""");
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<ValidateJsonSchemaStringDefinition>();
    }

    [Fact]
    public void ValidateXsd_WithString_AddsValidateXsdStringDefinition()
    {
        var def = new RouteDefinition();
        def.ValidateXsd("<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'/>");
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<ValidateXsdStringDefinition>();
    }

    [Fact]
    public void ValidateXsd_WithNamespace_AddsValidateXsdNamespaceDefinition()
    {
        var def = new RouteDefinition();
        def.ValidateXsd("urn:test", "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'/>");
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<ValidateXsdNamespaceDefinition>();
    }

    // ── Serialization ──────────────────────────────────────────────────────────

    [Fact]
    public void Marshal_WithType_AddsMarshalDefinition()
    {
        var def = new RouteDefinition();
        def.Marshal(typeof(DummySerializer));
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<MarshalDefinition>();
    }

    [Fact]
    public void Marshal_Generic_AddsMarshalDefinition()
    {
        var def = new RouteDefinition();
        def.Marshal<DummySerializer>();
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<MarshalDefinition>();
    }

    [Fact]
    public void Unmarshal_WithTypes_AddsUnmarshalDefinition()
    {
        var def = new RouteDefinition();
        def.Unmarshal(typeof(DummySerializer), typeof(string));
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<UnmarshalDefinition>();
    }

    [Fact]
    public void Unmarshal_Generic_AddsUnmarshalDefinition()
    {
        var def = new RouteDefinition();
        def.Unmarshal<DummySerializer, string>();
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<UnmarshalDefinition>();
    }

    // ── Telemetry ──────────────────────────────────────────────────────────────

    [Fact]
    public void Traced_Scope_ReturnsTracedDefinition()
    {
        var def = new RouteDefinition();
        var scope = def.Traced("my-op");
        scope.Should().BeOfType<TracedDefinition>();
        scope.OperationName.Should().Be("my-op");
        def.Outputs.Should().HaveCount(1).And.Contain(scope);
    }

    [Fact]
    public void Traced_EndTraced_ReturnsParent()
    {
        var def = new RouteDefinition();
        var parent = def.Traced("my-op").Process(_ => { }).EndTraced();
        parent.Should().BeSameAs(def);
    }

    [Fact]
    public void Traced_End_ReturnsParent()
    {
        var def = new RouteDefinition();
        var parent = def.Traced("my-op").End();
        parent.Should().BeSameAs(def);
    }

    [Fact]
    public void Traced_Inline_AddsProcessInstanceDefinition()
    {
        var def = new RouteDefinition();
        def.Traced("inline-op", _ => { });
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<ProcessInstanceDefinition>();
    }

    [Fact]
    public void Traced_Scope_CompilesInstrumentedProcessor()
    {
        var def = new RouteDefinition();
        def.Traced("compile-op").Process(_ => { }).EndTraced();

        using var ctx = new RouteContext();
        var proc = def.Outputs[0].CreateProcessor(ctx);
        proc.Should().BeOfType<redb.Route.Telemetry.InstrumentedProcessor>();
    }

    [Fact]
    public void Metered_Scope_ReturnsMeteredDefinition()
    {
        var def = new RouteDefinition();
        var scope = def.Metered("sql-step");
        scope.Should().BeOfType<MeteredDefinition>();
        scope.StepName.Should().Be("sql-step");
        def.Outputs.Should().HaveCount(1).And.Contain(scope);
    }

    [Fact]
    public void Metered_EndMetered_ReturnsParent()
    {
        var def = new RouteDefinition();
        var parent = def.Metered("sql-step").Process(_ => { }).EndMetered();
        parent.Should().BeSameAs(def);
    }

    [Fact]
    public void Metered_End_ReturnsParent()
    {
        var def = new RouteDefinition();
        var parent = def.Metered("sql-step").End();
        parent.Should().BeSameAs(def);
    }

    [Fact]
    public void Metered_Inline_AddsProcessInstanceDefinition()
    {
        var def = new RouteDefinition();
        def.Metered("inline-step", _ => { });
        def.Outputs.Should().HaveCount(1);
        def.Outputs[0].Should().BeOfType<ProcessInstanceDefinition>();
    }

    [Fact]
    public void Metered_Scope_CompilesMeteredStepProcessor()
    {
        var def = new RouteDefinition();
        def.Metered("sql-step").Process(_ => { }).EndMetered();

        using var ctx = new RouteContext();
        var proc = def.Outputs[0].CreateProcessor(ctx);
        proc.Should().BeOfType<redb.Route.Telemetry.MeteredStepProcessor>();
    }

    // ── TransactionDefinition ─────────────────────────────────────────────────

    [Fact]
    public void Transaction_Scope_ReturnsTransactionDefinition()
    {
        var def = new RouteDefinition();
        var scope = def.Transaction();
        scope.Should().BeOfType<TransactionDefinition>();
    }

    [Fact]
    public void Transaction_EndTransaction_ReturnsParent()
    {
        var def = new RouteDefinition();
        var parent = def.Transaction().Process(_ => { }).EndTransaction();
        parent.Should().BeSameAs(def);
    }

    [Fact]
    public void Transaction_End_ReturnsParent()
    {
        var def = new RouteDefinition();
        var parent = def.Transaction().End();
        parent.Should().BeSameAs(def);
    }

    [Fact]
    public void Transaction_Scope_CompilesTransactedProcessor()
    {
        var def = new RouteDefinition();
        def.Transaction().Process(_ => { }).EndTransaction();

        using var ctx = new RouteContext();
        var proc = def.Outputs[0].CreateProcessor(ctx);
        proc.Should().BeOfType<redb.Route.Transactions.TransactedProcessor>();
    }

    [Fact]
    public void Transaction_WithPolicy_IsRegistered()
    {
        var def = new RouteDefinition();
        var policy = new redb.Route.Transactions.TransactionPolicy();
        var scope = def.Transaction(policy);
        scope.Should().BeOfType<TransactionDefinition>();
        def.Outputs.Should().HaveCount(1);
    }
}

// Minimal serializer stub for Serialization DSL tests.
file sealed class DummySerializer : redb.Route.Abstractions.IMessageSerializer
{
    public string ContentType => "application/octet-stream";
    public byte[] Serialize<T>(T value) => [];
    public T? Deserialize<T>(byte[] data) => default;
    public object? Deserialize(byte[] data, Type type) => null;
}
