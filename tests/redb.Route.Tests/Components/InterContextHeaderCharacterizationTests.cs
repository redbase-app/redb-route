using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Components;

/// <summary>
/// What crosses between two contexts today: pinned before the header filter strategy reaches <c>direct-vm:</c> and
/// <c>vm:</c> (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md, §4.4). <c>direct-vm:</c> hands the called context the
/// caller's own exchange: every header goes, and every header the called route sets or removes is the caller's when
/// the call returns. <c>vm:</c> hands it a copy: every header goes, and nothing comes back.
/// </summary>
public sealed class InterContextHeaderCharacterizationTests : IAsyncLifetime
{
    private readonly ServiceProvider _sp;

    public InterContextHeaderCharacterizationTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SharedVmRegistry());
        _sp = services.BuildServiceProvider();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Exchange Sample()
    {
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        return exchange;
    }

    [Fact]
    public async Task DirectVm_TheCalledContextWorksOnTheCallersExchange()
    {
        IExchange? seen = null;
        Dictionary<string, object?>? headersSeen = null;
        await using var callee = new RouteContext(_sp, "callee");
        await using var caller = new RouteContext(_sp, "caller");
        callee.AddRoutes(r => r.From("direct-vm://hdr-char").Process(e =>
        {
            seen = e;
            headersSeen = new Dictionary<string, object?>(e.In.Headers);
            e.In.Headers.Remove("X-Corr");
            e.In.Headers["X-From-Callee"] = "set";
        }));
        caller.AddRoutes(r => r.From("direct://hdr-char-trigger").To("direct-vm://hdr-char"));
        await callee.Start();
        await caller.Start();

        var exchange = Sample();
        var producer = caller.GetEndpoint("direct://hdr-char-trigger").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        seen.Should().BeSameAs(exchange, "direct-vm is zero-copy: the called context gets the caller's exchange");
        headersSeen!.Keys.Should().Contain(["X-Corr", "Authorization", "redbHttp.Method"], "every header goes");
        exchange.In.Headers.Should().NotContainKey("X-Corr", "a header the called route removed is gone for the caller");
        exchange.In.Headers["X-From-Callee"].Should().Be("set", "a header the called route set is the caller's");
    }

    [Fact]
    public async Task Vm_TheCalledContextWorksOnACopy()
    {
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var callee = new RouteContext(_sp, "callee");
        await using var caller = new RouteContext(_sp, "caller");
        callee.AddRoutes(r => r.From("vm://hdr-char").Process(e =>
        {
            e.In.Headers["X-From-Callee"] = "set";
            received.TrySetResult(e);
        }));
        caller.AddRoutes(r => r.From("direct://hdr-char-trigger").To("vm://hdr-char"));
        await callee.Start();
        await caller.Start();

        var exchange = Sample();
        var producer = caller.GetEndpoint("direct://hdr-char-trigger").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();
        var copy = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        copy.Should().NotBeSameAs(exchange, "vm hands over a copy");
        copy.In.Headers.Keys.Should().Contain(["X-Corr", "Authorization", "redbHttp.Method"], "every header goes");
        exchange.In.Headers.Should().NotContainKey("X-From-Callee", "nothing comes back from an asynchronous call");
    }
}
