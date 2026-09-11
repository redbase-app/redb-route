using System.Net;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;


namespace redb.Route.Tests.Grpc;

/// <summary>
/// Admission limit on the gRPC consumer (план HTTP_CONCURRENCY_LIMITS_PLAN, волна В3,
/// решение В-3б): UNARY calls are counted, overflow is shed with an HTTP-level 429 before any
/// pipeline work; streaming and health methods are deliberately outside the limit.
/// </summary>
public class GrpcConcurrencyLimitTests : IAsyncLifetime
{
    private GrpcConsumer? _consumer;
    private int _port;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_consumer is not null) await _consumer.Stop();
    }

    private (GrpcConsumer Consumer, GrpcEndpoint Endpoint) CreateConsumer(
        Dictionary<string, string> extra, Func<IExchange, Task> onProcess)
    {
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
        };
        foreach (var (k, v) in extra) parameters[k] = v;

        var uri = new EndpointUri("grpc", $"/127.0.0.1:{_port}", $"grpc:127.0.0.1:{_port}", parameters);
        var endpoint = (GrpcEndpoint)new GrpcComponent().CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => onProcess(ci.Arg<IExchange>()));

        _consumer = new GrpcConsumer(endpoint, processor, endpoint.EndpointOptions);
        return (_consumer, endpoint);
    }

    /// <summary>Raw h2c POST to the gRPC method path — the shed happens at the HTTP layer.</summary>
    private HttpClient H2cClient() => new(new SocketsHttpHandler())
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };

    private Task<HttpResponseMessage> PostUnary(HttpClient client)
    {
        var content = new ByteArrayContent(new byte[5]); // empty gRPC frame (flag 0 + length 0)
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
        return client.PostAsync(
            $"http://127.0.0.1:{_port}{GrpcEndpointOptions.DefaultMethodPath}", content);
    }

    [Fact]
    public async Task UnaryOverLimit_IsShedWith429_AndCountedAsRejected()
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

        using var client = H2cClient();
        var first = PostUnary(client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the single permit is held

        var second = await PostUnary(client);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "сверх лимита unary отбрасывается на HTTP-уровне до пайплайна");
        second.Headers.RetryAfter.Should().NotBeNull();

        gate.SetResult();
        (await first).StatusCode.Should().Be(HttpStatusCode.OK);

        processed.Should().Be(1, "отброшенный вызов не должен коснуться пайплайна");
        endpoint.Rejected.Should().Be(1);
        endpoint.Errors.Should().Be(0, "отказ — не ошибка обработки");
    }

    [Fact]
    public async Task HealthMethod_IsNotCounted_EvenWhenTheLimitIsSaturated()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (consumer, _) = CreateConsumer(
            new Dictionary<string, string>
            {
                ["maxConcurrentRequests"] = "1",
                ["health"] = "true",
            },
            async _ => { entered.TrySetResult(); await gate.Task; });
        await consumer.Start();

        using var client = H2cClient();
        var held = PostUnary(client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // permit taken by the unary call

        var health = new ByteArrayContent(new byte[5]);
        health.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
        var probe = await client.PostAsync(
            $"http://127.0.0.1:{_port}/grpc.health.v1.Health/Check", health);
        probe.StatusCode.Should().Be(HttpStatusCode.OK,
            "health-проба вне лимита: оркестратор не должен флапать под нагрузкой");

        gate.SetResult();
        await held;
    }

    [Fact]
    public void UriAndDsl_CarryTheOptions()
    {
        var built = GrpcDsl.Listen($"127.0.0.1:{_port}")
            .MaxConcurrentRequests(4, queue: 8)
            .RejectStatusCode(503)
            .RetryAfterSeconds(0)
            .Build();

        built.Should().Contain("maxConcurrentRequests=4")
            .And.Contain("requestQueueLimit=8")
            .And.Contain("rejectStatusCode=503")
            .And.Contain("retryAfterSeconds=0");

        var endpoint = (GrpcEndpoint)new GrpcComponent().CreateEndpoint(EndpointUriParser.Parse(built));
        endpoint.EndpointOptions.MaxConcurrentRequests.Should().Be(4);
        endpoint.EndpointOptions.RequestQueueLimit.Should().Be(8);
        endpoint.EndpointOptions.RejectStatusCode.Should().Be(503);
        endpoint.EndpointOptions.RetryAfterSeconds.Should().Be(0);
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
