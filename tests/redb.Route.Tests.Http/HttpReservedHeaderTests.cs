using System.Net;
using System.Net.Http;
using NetHttpMethod = System.Net.Http.HttpMethod;
using System.Net.Sockets;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// A caller must not be able to set the transport's own headers. <c>redbHttp.RemoteAddress</c> is the
/// input to per-IP rate limiting, brute-force lockout and audit records; the consumer takes it from the
/// connection, and the copy of the request headers runs afterwards — so without a guard a header
/// literally named <c>redbHttp.RemoteAddress</c> (a valid HTTP token) would overwrite it and let a caller
/// choose its own throttle bucket.
/// </summary>
public sealed class HttpReservedHeaderTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _server = new();
    private HttpConsumer? _consumer;
    private int _port;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_consumer is not null) await _consumer.Stop();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Caller_cannot_override_the_transport_remote_address()
    {
        IExchange? seen = null;

        var component = new HttpComponent { ServerManager = _server };
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
        };
        var uri = new EndpointUri("http", $"/127.0.0.1:{_port}/hook",
            $"http:127.0.0.1:{_port}/hook", parameters);
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { seen = ci.Arg<IExchange>(); return Task.CompletedTask; });

        _consumer = (HttpConsumer)endpoint.CreateConsumer(processor);
        await _consumer.Start();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(NetHttpMethod.Post, $"http://127.0.0.1:{_port}/hook")
        {
            Content = new StringContent("payload"),
        };
        request.Headers.TryAddWithoutValidation("redbHttp.RemoteAddress", "203.0.113.7");
        request.Headers.TryAddWithoutValidation("X-Tenant", "acme");

        await client.SendAsync(request);

        seen.Should().NotBeNull();
        seen!.In.GetHeader<string>(HttpHeaders.RemoteAddress).Should().Be("127.0.0.1");

        // Ordinary caller headers are untouched — only the transport's own namespace is protected.
        seen.In.GetHeader<string>("X-Tenant").Should().Be("acme");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
