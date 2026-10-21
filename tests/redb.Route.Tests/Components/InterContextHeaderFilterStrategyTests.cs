using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Components;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on the calling side of <c>direct-vm:</c> and <c>vm:</c>
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md, §4.4). <c>direct-vm:</c> then runs the called context on a copy:
/// the callee sees the headers the strategy lets out, and the caller keeps its own headers and takes the reply's body,
/// the reply headers the strategy lets in, the properties and the outcome. <c>vm:</c> filters the copy it hands over.
/// </summary>
public sealed class InterContextHeaderFilterStrategyTests : IAsyncDisposable
{
    private readonly ServiceProvider _sp;

    public InterContextHeaderFilterStrategyTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SharedVmRegistry());
        _sp = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static DefaultHeaderFilterStrategy NoSecretsOutNoInternalsIn() => new()
    {
        OutFilter = ["Authorization"],
        InFilter = ["X-Internal"],
    };

    private static Exchange Sample()
    {
        var exchange = new Exchange(new Message("request"));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        return exchange;
    }

    private async Task<IExchange> Call(RouteContext caller, Exchange exchange)
    {
        var producer = caller.GetEndpoint("direct://trigger").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();
        return exchange;
    }

    [Fact]
    public async Task DirectVm_TheCalleeSeesWhatTheStrategyLetsOut_AndTheCallerKeepsItsOwnHeaders()
    {
        IExchange? seen = null;
        Dictionary<string, object?>? headersSeen = null;
        await using var callee = new RouteContext(_sp, "callee");
        await using var caller = new RouteContext(_sp, "caller");
        caller.AddToRegistry("filter", NoSecretsOutNoInternalsIn());
        callee.AddRoutes(r => r.From("direct-vm://hdr-strategy").Process(e =>
        {
            seen = e;
            headersSeen = new Dictionary<string, object?>(e.In.Headers);
            e.In.Headers.Remove("X-Corr");
            e.In.Headers["X-From-Callee"] = "set";
            e.In.Headers["X-Internal"] = "callee only";
            e.Properties["callee.property"] = 42;
            e.In.Body = "reply";
        }));
        caller.AddRoutes(r => r.From("direct://trigger").To("direct-vm://hdr-strategy?headerFilterStrategy=#filter"));
        await callee.Start();
        await caller.Start();

        var exchange = await Call(caller, Sample());

        seen.Should().NotBeSameAs(exchange, "under a strategy the called context works on a copy");
        headersSeen!.Should().ContainKey("X-Corr");
        headersSeen.Should().NotContainKey("Authorization", "the strategy keeps it from the callee");

        exchange.In.Body.Should().Be("reply");
        exchange.In.Headers["X-Corr"].Should().Be("c1", "the caller keeps its own headers, the callee removed only its copy");
        exchange.In.Headers["Authorization"].Should().Be("Bearer secret", "the caller's own header never left it");
        exchange.In.Headers["X-From-Callee"].Should().Be("set", "a reply header the strategy lets in comes back");
        exchange.In.Headers.Should().NotContainKey("X-Internal", "the strategy keeps it from the caller");
        exchange.Properties["callee.property"].Should().Be(42, "properties come back, as with the zero-copy call");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?headerFilterStrategy=#filter")]
    public async Task DirectVm_AFailingCallee_EndsTheCallTheSameWayWithOrWithoutAStrategy(string options)
    {
        await using var callee = new RouteContext(_sp, "callee");
        await using var caller = new RouteContext(_sp, "caller");
        caller.AddToRegistry("filter", NoSecretsOutNoInternalsIn());
        callee.AddRoutes(r => r.From("direct-vm://hdr-strategy-fail")
            .Process(_ => throw new InvalidOperationException("callee failed")));
        caller.AddRoutes(r => r.From("direct://trigger").To("direct-vm://hdr-strategy-fail" + options));
        await callee.Start();
        await caller.Start();

        var exchange = Sample();
        var producer = caller.GetEndpoint("direct://trigger").CreateProducer();
        await producer.Start();
        Exception? thrown = null;
        try { await producer.Process(exchange); }
        catch (Exception ex) { thrown = ex; }
        await producer.Stop();

        var failure = thrown ?? exchange.Exception;
        failure.Should().NotBeNull();
        (failure as InvalidOperationException ?? failure!.InnerException).Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("callee failed");
    }

    [Fact]
    public async Task DirectVm_AnUnknownParameterIsRefused()
    {
        await using var caller = new RouteContext(_sp, "caller");

        var act = () => caller.GetEndpoint("direct-vm://hdr-strategy?headerFilterStategy=#filter");

        act.Should().Throw<ArgumentException>().WithMessage("*headerFilterStategy*");
    }

    [Theory]
    [InlineData("direct-vm")]
    [InlineData("vm")]
    public async Task OnAConsumer_TheOptionIsRefused(string scheme)
    {
        await using var callee = new RouteContext(_sp, "callee");
        callee.AddToRegistry("filter", NoSecretsOutNoInternalsIn());

        var act = () => callee.GetEndpoint($"{scheme}://hdr-strategy-consumer?headerFilterStrategy=#filter")
            .CreateConsumer(new DelegateProcessor((IExchange _) => { }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*sets headerFilterStrategy, which applies to the * side*");
    }

    [Fact]
    public async Task Vm_TheCopyCarriesWhatTheStrategyLetsOut()
    {
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var callee = new RouteContext(_sp, "callee");
        await using var caller = new RouteContext(_sp, "caller");
        caller.AddToRegistry("filter", NoSecretsOutNoInternalsIn());
        callee.AddRoutes(r => r.From("vm://hdr-strategy").Process(e => received.TrySetResult(e)));
        caller.AddRoutes(r => r.From("direct://trigger").To("vm://hdr-strategy?headerFilterStrategy=#filter"));
        await callee.Start();
        await caller.Start();

        var exchange = await Call(caller, Sample());
        var copy = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        copy.In.Headers["X-Corr"].Should().Be("c1");
        copy.In.Headers.Should().NotContainKey("Authorization", "the strategy keeps it from the other context");
        exchange.In.Headers["Authorization"].Should().Be("Bearer secret", "the caller's own exchange is untouched");
    }
}
