using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// An HTTP consumer and producer keep working after <c>Stop()</c> and <c>Start()</c> of the same route context: the stop
/// gives the port back, the start takes it again, and the producer's client of the cached endpoint still sends.
/// </summary>
[Collection("HttpServer")]
public sealed class HttpRestartTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _servers = new();
    private readonly RouteContext _ctx = new();
    private readonly HttpClient _client = new();
    private readonly int _port = global::redb.Route.Tests.Shared.TestPorts.Next();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _ctx.DisposeAsync();
        await _servers.DisposeAsync();
    }

    [Fact]
    public async Task Consumer_and_producer_work_after_the_context_is_stopped_and_started_again()
    {
        var port = _port;
        var calls = 0;
        _ctx.AddComponent(new HttpComponent { ServerManager = _servers });
        _ctx.AddRoutes(r => r.From($"http://127.0.0.1:{port}/ping?inOut=true").Process(e =>
        {
            Interlocked.Increment(ref calls);
            e.In.Body = "pong";
        }));
        _ctx.AddRoutes(r => r.From("direct://call").To($"http://127.0.0.1:{port}/ping"));

        async Task CheckAsync(int round)
        {
            (await _client.GetStringAsync($"http://127.0.0.1:{port}/ping")).Should().Be("pong",
                "HTTP-приёмник после рестарта отвечает");

            var producer = _ctx.GetEndpoint("direct://call").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message("x"));
            await producer.Process(exchange);
            exchange.Exception.Should().BeNull("HTTP-отправка после рестарта доходит");
            Volatile.Read(ref calls).Should().Be(round * 2);
        }

        await _ctx.Start();
        await CheckAsync(1);

        await _ctx.Stop();
        var stopped = () => _client.GetAsync($"http://127.0.0.1:{port}/ping");
        await stopped.Should().ThrowAsync<HttpRequestException>("остановленный контекст отпускает порт");

        await _ctx.Start();
        await CheckAsync(2);
    }
}
