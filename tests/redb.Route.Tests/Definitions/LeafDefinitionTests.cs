using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;
using redb.Route.Validation;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 F2 — all leaf ProcessorDefinition subclasses.
/// </summary>
public class LeafDefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Exchange MakeExchange(object? body = null)
    {
        var msg = new Message { Body = body };
        return new Exchange(msg);
    }

    // ── Process ──────────────────────────────────────────────────────────────

    [Fact]
    public void ProcessActionDefinition_CreatesDelegate()
    {
        bool called = false;
        var def = new ProcessActionDefinition(_ => { called = true; });
        var proc = def.CreateProcessor(_context);
        proc.Should().BeOfType<DelegateProcessor>();
        proc.Process(MakeExchange());
        called.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsyncDefinition_CreatesDelegate()
    {
        bool called = false;
        var def = new ProcessAsyncDefinition(async (_, _) => { called = true; await Task.CompletedTask; });
        var proc = def.CreateProcessor(_context);
        await proc.Process(MakeExchange());
        called.Should().BeTrue();
    }

    [Fact]
    public void ProcessInstanceDefinition_ReturnsSameProcessor()
    {
        var inner = new DelegateProcessor(_ => { });
        var def = new ProcessInstanceDefinition(inner);
        def.CreateProcessor(_context).Should().BeSameAs(inner);
    }

    // ── SetBody ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetBodyStaticDefinition_SetsBody()
    {
        var def = new SetBodyStaticDefinition("hello");
        var ex = MakeExchange();
        await def.CreateProcessor(_context).Process(ex);
        ex.In.Body.Should().Be("hello");
    }

    [Fact]
    public async Task SetBodyFactoryDefinition_SetsBodyFromFactory()
    {
        var def = new SetBodyFactoryDefinition(e => "from-factory");
        var ex = MakeExchange();
        await def.CreateProcessor(_context).Process(ex);
        ex.In.Body.Should().Be("from-factory");
    }

    // ── Transform ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransformDefinition_TransformsBody()
    {
        var def = new TransformDefinition(e => e.In.Body?.ToString()?.ToUpperInvariant());
        var ex = MakeExchange("hello");
        await def.CreateProcessor(_context).Process(ex);
        ex.In.Body.Should().Be("HELLO");
    }

    // ── SetHeader ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetHeaderStaticDefinition_SetsHeader()
    {
        var def = new SetHeaderStaticDefinition("X-Test", 42);
        var ex = MakeExchange();
        await def.CreateProcessor(_context).Process(ex);
        ex.In.Headers["X-Test"].Should().Be(42);
    }

    [Fact]
    public async Task RemoveHeaderDefinition_RemovesHeader()
    {
        var def = new RemoveHeaderDefinition("X-Remove");
        var ex = MakeExchange();
        ex.In.Headers["X-Remove"] = "val";
        await def.CreateProcessor(_context).Process(ex);
        ex.In.Headers.ContainsKey("X-Remove").Should().BeFalse();
    }

    [Fact]
    public async Task RemoveBodyDefinition_NullsBody()
    {
        var def = new RemoveBodyDefinition();
        var ex = MakeExchange("not-null");
        await def.CreateProcessor(_context).Process(ex);
        ex.In.Body.Should().BeNull();
    }

    // ── SetProperty ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SetPropertyStaticDefinition_SetsProperty()
    {
        var def = new SetPropertyStaticDefinition("myKey", "myValue");
        var ex = MakeExchange();
        await def.CreateProcessor(_context).Process(ex);
        ex.Properties["myKey"].Should().Be("myValue");
    }

    [Fact]
    public async Task RemovePropertyDefinition_RemovesProperty()
    {
        var def = new RemovePropertyDefinition("myKey");
        var ex = MakeExchange();
        ex.Properties["myKey"] = "v";
        await def.CreateProcessor(_context).Process(ex);
        ex.Properties.ContainsKey("myKey").Should().BeFalse();
    }

    // ── Throw / ExceptionHandled ──────────────────────────────────────────────

    [Fact]
    public void ThrowMessageDefinition_Throws()
    {
        var def = new ThrowMessageDefinition("boom");
        var act = () => def.CreateProcessor(_context).Process(MakeExchange());
        act.Should().ThrowAsync<Exception>().WithMessage("boom");
    }

    [Fact]
    public async Task ExceptionHandledDefinition_ClearsException()
    {
        var def = new ExceptionHandledDefinition();
        var ex = MakeExchange();
        ex.Exception = new Exception("test");
        await def.CreateProcessor(_context).Process(ex);
        ex.ExceptionHandled.Should().BeTrue();
        ex.Exception.Should().BeNull();
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopDefinition_StopsExchange()
    {
        var def = new StopDefinition();
        var ex = MakeExchange();
        await def.CreateProcessor(_context).Process(ex);
        ex.IsStopped.Should().BeTrue();
    }

    // ── Delay ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DelayDefinition_CreatesDelayProcessor()
    {
        var def = new DelayDefinition(TimeSpan.FromMilliseconds(100));
        def.CreateProcessor(_context).Should().BeOfType<DelayProcessor>();
    }

    // ── Sampling ──────────────────────────────────────────────────────────────

    [Fact]
    public void SampleCountDefinition_CreatesSamplingProcessor()
    {
        var def = new SampleCountDefinition(3);
        def.CreateProcessor(_context).Should().BeOfType<SamplingProcessor>();
    }

    [Fact]
    public void SamplePeriodDefinition_CreatesSamplingProcessor()
    {
        var def = new SamplePeriodDefinition(TimeSpan.FromSeconds(1));
        def.CreateProcessor(_context).Should().BeOfType<SamplingProcessor>();
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    [Fact]
    public void ValidatePredicateDefinition_CreatesValidateProcessor()
    {
        var def = new ValidatePredicateDefinition(e => true, "error", throwOnFailure: false);
        def.CreateProcessor(_context).Should().BeOfType<ValidateProcessor>();
    }

    // ── SetPattern / Respond ──────────────────────────────────────────────────

    [Fact]
    public async Task SetPatternDefinition_SetsPattern()
    {
        var def = new SetPatternDefinition(ExchangePattern.InOut);
        var ex = MakeExchange();
        await def.CreateProcessor(_context).Process(ex);
        ex.Pattern.Should().Be(ExchangePattern.InOut);
    }
}
