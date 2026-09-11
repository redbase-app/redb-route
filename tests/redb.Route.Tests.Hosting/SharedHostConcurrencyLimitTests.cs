using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using redb.Route.Http;
using Xunit;

namespace redb.Route.Tests.Hosting;

/// <summary>
/// Per-registration admission limits (план HTTP_CONCURRENCY_LIMITS_PLAN, волна В1). Kestrel
/// executes as many handlers as requests arrive; a registration with a limit runs at most
/// maxConcurrentRequests of them, queues up to requestQueueLimit, and sheds the rest with
/// 429 + Retry-After BEFORE any pipeline work — strictly per registration, so a saturated
/// route cannot eat a neighbor's budget on the shared listener.
/// </summary>
public sealed class SharedHostConcurrencyLimitTests : IAsyncLifetime
{
    private readonly List<SharedHttpServerManager> _managers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var m in _managers) await m.DisposeAsync();
    }

    private SharedHttpServerManager Manager()
    {
        var manager = new SharedHttpServerManager(new HttpHostingOptions());
        _managers.Add(manager);
        return manager;
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Handler that tracks the maximum number of concurrent executions.</summary>
    private sealed class ConcurrencyProbe
    {
        private int _current;
        private int _max;
        public int Max => Volatile.Read(ref _max);

        public async Task Handle(HttpContext ctx)
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            do { seen = Volatile.Read(ref _max); }
            while (now > seen && Interlocked.CompareExchange(ref _max, now, seen) != seen);

            await Task.Delay(300);
            Interlocked.Decrement(ref _current);
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok");
        }
    }

    [Fact]
    public async Task Limit_CapsConcurrentExecutions()
    {
        var port = GetFreePort();
        var manager = Manager();
        var probe = new ConcurrencyProbe();
        manager.RegisterRoute("127.0.0.1", port, "/limited", null, probe.Handle,
            concurrencyLimit: new ConcurrencyLimitOptions { MaxConcurrentRequests = 2, RequestQueueLimit = 100 });
        await manager.EnsureStarted("127.0.0.1", port);

        using var client = new HttpClient();
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => client.GetAsync($"http://127.0.0.1:{port}/limited"))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK,
            "с достаточной очередью никто не отбрасывается — все дожидаются пермита");
        probe.Max.Should().BeLessThanOrEqualTo(2,
            "без лимита Kestrel исполнил бы все 8 разом — потолок обязан держать 2");
    }

    [Fact]
    public async Task OverLimitAndQueue_IsShedWith429AndRetryAfter()
    {
        var port = GetFreePort();
        var manager = Manager();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejections = 0;

        manager.RegisterRoute("127.0.0.1", port, "/shed", null,
            async ctx => { entered.TrySetResult(); await gate.Task; ctx.Response.StatusCode = 200; },
            concurrencyLimit: new ConcurrencyLimitOptions
            {
                MaxConcurrentRequests = 1,
                RequestQueueLimit = 0,
                OnRejected = () => Interlocked.Increment(ref rejections),
            });
        await manager.EnsureStarted("127.0.0.1", port);

        using var client = new HttpClient();
        var first = client.GetAsync($"http://127.0.0.1:{port}/shed");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // permit is now held

        var second = await client.GetAsync($"http://127.0.0.1:{port}/shed");
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "сверх лимита при пустой очереди — немедленный отказ, а не ожидание");
        second.Headers.RetryAfter.Should().NotBeNull();
        second.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
        rejections.Should().Be(1, "OnRejected — ровно один вызов на один отказ");

        gate.SetResult();
        (await first).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueuedRequest_RunsWhenAPermitFrees()
    {
        var port = GetFreePort();
        var manager = Manager();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.RegisterRoute("127.0.0.1", port, "/queued", null,
            async ctx => { entered.TrySetResult(); await gate.Task; ctx.Response.StatusCode = 200; await ctx.Response.WriteAsync("ok"); },
            concurrencyLimit: new ConcurrencyLimitOptions { MaxConcurrentRequests = 1, RequestQueueLimit = 1 });
        await manager.EnsureStarted("127.0.0.1", port);

        using var client = new HttpClient();
        var first = client.GetAsync($"http://127.0.0.1:{port}/queued");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = client.GetAsync($"http://127.0.0.1:{port}/queued");
        await Task.Delay(200);
        second.IsCompleted.Should().BeFalse("второй запрос обязан ЖДАТЬ в очереди, а не отброситься");

        gate.SetResult();
        (await first).StatusCode.Should().Be(HttpStatusCode.OK);
        (await second).StatusCode.Should().Be(HttpStatusCode.OK, "очередь дождалась пермита");
    }

    [Fact]
    public async Task NeighborWithoutLimit_IsUnaffected()
    {
        var port = GetFreePort();
        var manager = Manager();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.RegisterRoute("127.0.0.1", port, "/saturated", null,
            async ctx => { entered.TrySetResult(); await gate.Task; ctx.Response.StatusCode = 200; },
            concurrencyLimit: new ConcurrencyLimitOptions { MaxConcurrentRequests = 1 });
        manager.RegisterRoute("127.0.0.1", port, "/free", null,
            async ctx => { ctx.Response.StatusCode = 200; await ctx.Response.WriteAsync("free"); });
        await manager.EnsureStarted("127.0.0.1", port);

        using var client = new HttpClient();
        var held = client.GetAsync($"http://127.0.0.1:{port}/saturated");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var neighbor = await client.GetAsync($"http://127.0.0.1:{port}/free");
        neighbor.StatusCode.Should().Be(HttpStatusCode.OK,
            "лимит строго per-registration: насыщенный сосед не трогает чужой бюджет");

        gate.SetResult();
        await held;
    }

    [Fact]
    public async Task RejectStatusCode_AndRetryAfter_AreConfigurable()
    {
        var port = GetFreePort();
        var manager = Manager();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.RegisterRoute("127.0.0.1", port, "/custom", null,
            async ctx => { entered.TrySetResult(); await gate.Task; ctx.Response.StatusCode = 200; },
            concurrencyLimit: new ConcurrencyLimitOptions
            {
                MaxConcurrentRequests = 1,
                RejectStatusCode = 503,
                RetryAfterSeconds = 0,
            });
        await manager.EnsureStarted("127.0.0.1", port);

        using var client = new HttpClient();
        var first = client.GetAsync($"http://127.0.0.1:{port}/custom");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await client.GetAsync($"http://127.0.0.1:{port}/custom");
        second.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        second.Headers.RetryAfter.Should().BeNull("retryAfterSeconds=0 — заголовок не шлётся");

        gate.SetResult();
        await first;
    }

    [Fact]
    public void InvalidOptions_FailLoud()
    {
        var manager = Manager();
        var port = GetFreePort();

        var zeroLimit = () => manager.RegisterRoute("127.0.0.1", port, "/x", null, _ => Task.CompletedTask,
            concurrencyLimit: new ConcurrencyLimitOptions { MaxConcurrentRequests = 0 });
        zeroLimit.Should().Throw<ArgumentOutOfRangeException>();

        var badStatus = () => manager.RegisterRoute("127.0.0.1", port, "/x", null, _ => Task.CompletedTask,
            concurrencyLimit: new ConcurrencyLimitOptions { MaxConcurrentRequests = 1, RejectStatusCode = 200 });
        badStatus.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task HostConnectionLimits_AreAppliedWithoutBreakingService()
    {
        // The backstop is Kestrel's own ceiling; a functional smoke proves the wiring does not
        // regress normal service (a hard connection-starvation assert would be timing-flaky).
        var port = GetFreePort();
        var options = new HttpHostingOptions();
        options.Limits.MaxConcurrentConnections = 100;
        options.Limits.MaxConcurrentUpgradedConnections = 10;
        var manager = new SharedHttpServerManager(options);
        _managers.Add(manager);

        manager.RegisterRoute("127.0.0.1", port, "/smoke", null,
            async ctx => { ctx.Response.StatusCode = 200; await ctx.Response.WriteAsync("ok"); });
        await manager.EnsureStarted("127.0.0.1", port);

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{port}/smoke");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
