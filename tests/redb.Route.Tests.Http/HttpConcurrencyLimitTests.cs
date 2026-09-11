using System.Net;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using HttpDsl = redb.Route.Http.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// E2e for the per-endpoint admission limit (план HTTP_CONCURRENCY_LIMITS_PLAN, волна В2):
/// a consumer with <c>maxConcurrentRequests</c> runs at most that many pipelines, sheds the
/// overflow with 429 + Retry-After BEFORE the pipeline, and counts each shed request in the
/// endpoint's <c>Rejected</c> — MessagesIn/Errors stay untouched.
/// </summary>
[Collection("HttpServer")]
public class HttpConcurrencyLimitTests : IAsyncLifetime
{
    private HttpConsumer? _consumer;
    private int _port;
    private SharedHttpServerManager? _serverManager;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _serverManager = new SharedHttpServerManager();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_consumer is not null) await _consumer.Stop();
        if (_serverManager is not null) await _serverManager.DisposeAsync();
    }

    private (HttpConsumer Consumer, HttpEndpoint Endpoint) CreateConsumer(
        Dictionary<string, string> extra, Func<IExchange, Task> onProcess)
    {
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
        };
        foreach (var (k, v) in extra) parameters[k] = v;

        var path = $"/127.0.0.1:{_port}/limited";
        var uri = new EndpointUri("http", path, $"http:{path}", parameters);
        var endpoint = (HttpEndpoint)new HttpComponent().CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => onProcess(ci.Arg<IExchange>()));

        _consumer = new HttpConsumer(endpoint, processor, endpoint.EndpointOptions, _serverManager!);
        return (_consumer, endpoint);
    }

    [Fact]
    public async Task OverLimit_IsShedWith429_AndCountedAsRejected()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = 0;

        var (consumer, endpoint) = CreateConsumer(
            new Dictionary<string, string> { ["maxConcurrentRequests"] = "1" },
            async _ =>
            {
                Interlocked.Increment(ref processed);
                entered.TrySetResult();
                await gate.Task;
            });
        await consumer.Start();

        using var client = new HttpClient();
        var first = client.PostAsync($"http://127.0.0.1:{_port}/limited", new StringContent("a"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the single permit is held

        var second = await client.PostAsync($"http://127.0.0.1:{_port}/limited", new StringContent("b"));
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "сверх лимита — немедленный отказ до пайплайна");
        second.Headers.RetryAfter.Should().NotBeNull();
        second.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));

        gate.SetResult();
        (await first).IsSuccessStatusCode.Should().BeTrue();

        processed.Should().Be(1, "отброшенный запрос не должен коснуться пайплайна");
        endpoint.Rejected.Should().Be(1, "каждый отказ считается ровно один раз");
        endpoint.Errors.Should().Be(0, "отказ — не ошибка обработки");
    }

    [Fact]
    public async Task WithQueue_TheOverflowWaits_AndCapHolds()
    {
        var current = 0;
        var max = 0;

        var (consumer, endpoint) = CreateConsumer(
            new Dictionary<string, string>
            {
                ["maxConcurrentRequests"] = "2",
                ["requestQueueLimit"] = "100",
            },
            async _ =>
            {
                var now = Interlocked.Increment(ref current);
                int seen;
                do { seen = Volatile.Read(ref max); }
                while (now > seen && Interlocked.CompareExchange(ref max, now, seen) != seen);
                await Task.Delay(250);
                Interlocked.Decrement(ref current);
            });
        await consumer.Start();

        using var client = new HttpClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.PostAsync($"http://127.0.0.1:{_port}/limited", new StringContent("x"))));

        responses.Should().OnlyContain(r => r.IsSuccessStatusCode,
            "с достаточной очередью никто не отбрасывается");
        Volatile.Read(ref max).Should().BeLessThanOrEqualTo(2,
            "без лимита Kestrel исполнил бы все 8 разом");
        endpoint.Rejected.Should().Be(0);
    }

    [Fact]
    public void UriAndDsl_CarryTheOptions()
    {
        var built = HttpDsl.Listen("/api")
            .Port(8080)
            .MaxConcurrentRequests(4, queue: 16)
            .RejectStatusCode(503)
            .RetryAfterSeconds(0)
            .Build();
        built.Should().Contain("maxConcurrentRequests=4")
            .And.Contain("requestQueueLimit=16")
            .And.Contain("rejectStatusCode=503")
            .And.Contain("retryAfterSeconds=0");

        var endpoint = (HttpEndpoint)new HttpComponent().CreateEndpoint(EndpointUriParser.Parse(built));
        endpoint.EndpointOptions.MaxConcurrentRequests.Should().Be(4);
        endpoint.EndpointOptions.RequestQueueLimit.Should().Be(16);
        endpoint.EndpointOptions.RejectStatusCode.Should().Be(503);
        endpoint.EndpointOptions.RetryAfterSeconds.Should().Be(0);
    }

    [Fact]
    public void QueueWithoutLimit_FailsLoud()
    {
        var uri = EndpointUriParser.Parse($"http:/127.0.0.1:{_port}/x?requestQueueLimit=5");
        var act = () => new HttpComponent().CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*requestQueueLimit*maxConcurrentRequests*");
    }

    private static int GetFreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
