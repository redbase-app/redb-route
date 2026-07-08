using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using HttpMethod = redb.Route.Http.HttpMethod;

namespace redb.Route.Tests.Http;

/// <summary>
/// Tests for HttpProducer using a real embedded HTTP test server.
/// Each test starts a lightweight Kestrel server, sends real HTTP requests, and verifies the results.
/// </summary>
[Collection("HttpServer")]
public class HttpProducerTests : IAsyncLifetime
{
    private WebApplication? _server;
    private int _port;
    private string _baseUrl = null!;

    // Captures from test server
    private string? _lastRequestMethod;
    private string? _lastRequestPath;
    private string? _lastRequestBody;
    private string? _lastRequestContentType;
    private Dictionary<string, string> _lastRequestHeaders = new(StringComparer.OrdinalIgnoreCase);

    // Configurable response from test server
    private int _responseStatusCode = 200;
    private string _responseBody = """{"result":"ok"}""";
    private string _responseContentType = "application/json";
    private Dictionary<string, string> _responseHeaders = new();

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        _baseUrl = $"http://localhost:{_port}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, _port));
        builder.Logging.ClearProviders();

        _server = builder.Build();
        _server.Map("/{**catch}", HandleTestRequest);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
        }
    }

    private async Task HandleTestRequest(Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        _lastRequestMethod = ctx.Request.Method;
        _lastRequestPath = ctx.Request.Path + ctx.Request.QueryString;
        _lastRequestContentType = ctx.Request.ContentType;

        _lastRequestHeaders.Clear();
        foreach (var h in ctx.Request.Headers)
        {
            _lastRequestHeaders[h.Key] = h.Value.ToString();
        }

        if (ctx.Request.ContentLength > 0 || ctx.Request.ContentType is not null)
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            _lastRequestBody = await reader.ReadToEndAsync();
        }
        else
        {
            _lastRequestBody = null;
        }

        ctx.Response.StatusCode = _responseStatusCode;
        ctx.Response.ContentType = _responseContentType;
        foreach (var (key, value) in _responseHeaders)
        {
            ctx.Response.Headers.Append(key, value);
        }
        await ctx.Response.WriteAsync(_responseBody);
    }

    private HttpEndpoint CreateEndpoint(Dictionary<string, string>? parameters = null)
    {
        var component = new HttpComponent();
        var pars = parameters ?? new Dictionary<string, string>();
        var path = $"/localhost:{_port}";
        var uri = new EndpointUri("http", path, $"http:{path}", pars);
        return (HttpEndpoint)component.CreateEndpoint(uri);
    }

    private HttpEndpoint CreateEndpointWithPath(string route, Dictionary<string, string>? parameters = null)
    {
        var component = new HttpComponent();
        var pars = parameters ?? new Dictionary<string, string>();
        var path = $"/localhost:{_port}{route}";
        var uri = new EndpointUri("http", path, $"http:{path}", pars);
        return (HttpEndpoint)component.CreateEndpoint(uri);
    }

    private HttpEndpoint CreateEndpointWithFullPath(string fullPath, Dictionary<string, string>? parameters = null)
    {
        var component = new HttpComponent();
        var pars = parameters ?? new Dictionary<string, string>();
        var uri = new EndpointUri("http", fullPath, $"http:{fullPath}", pars);
        return (HttpEndpoint)component.CreateEndpoint(uri);
    }

    private static Exchange CreateExchange(object? body = null)
    {
        var message = new Message(body);
        return new Exchange(message);
    }

    // ── Basic GET ──

    [Fact]
    public async Task Producer_GET_SendsRequest_ReceivesResponse()
    {
        var endpoint = CreateEndpointWithPath("/api/test");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("GET");
        _lastRequestPath.Should().Be("/api/test");
        exchange.Out.Should().NotBeNull();
        exchange.Out!.Headers[HttpHeaders.StatusCode].Should().Be(200);

        await producer.Stop();
    }

    // ── POST with body ──

    [Fact]
    public async Task Producer_POST_SendsBodyAsString()
    {
        var endpoint = CreateEndpointWithPath("/api/data", new Dictionary<string, string>
        {
            ["method"] = "POST"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange("""{"name":"test"}""");
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("POST");
        _lastRequestBody.Should().Be("""{"name":"test"}""");
        _lastRequestContentType.Should().Contain("application/json");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_POST_SendsBodyAsBytes()
    {
        var endpoint = CreateEndpointWithPath("/api/binary", new Dictionary<string, string>
        {
            ["method"] = "POST",
            ["contentType"] = "application/octet-stream"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var bytes = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var exchange = CreateExchange(bytes);
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("POST");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_POST_SendsBodyAsStream()
    {
        var endpoint = CreateEndpointWithPath("/api/stream", new Dictionary<string, string>
        {
            ["method"] = "POST"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("stream-data"));
        var exchange = CreateExchange(stream);
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("POST");
        _lastRequestBody.Should().Be("stream-data");

        await producer.Stop();
    }

    // ── PUT and DELETE ──

    [Fact]
    public async Task Producer_PUT_SendsRequest()
    {
        var endpoint = CreateEndpointWithPath("/api/update", new Dictionary<string, string>
        {
            ["method"] = "PUT"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange("""{"update":true}""");
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("PUT");
        _lastRequestBody.Should().Be("""{"update":true}""");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_DELETE_SendsRequest()
    {
        var endpoint = CreateEndpointWithPath("/api/resource/42", new Dictionary<string, string>
        {
            ["method"] = "DELETE"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("DELETE");
        _lastRequestPath.Should().Be("/api/resource/42");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_PATCH_SendsRequest()
    {
        var endpoint = CreateEndpointWithPath("/api/patch", new Dictionary<string, string>
        {
            ["method"] = "PATCH"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange("""{"field":"value"}""");
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("PATCH");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_HEAD_SendsRequest()
    {
        var endpoint = CreateEndpointWithPath("/api/status", new Dictionary<string, string>
        {
            ["method"] = "HEAD"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("HEAD");

        await producer.Stop();
    }

    // ── Method override from exchange header ──

    [Fact]
    public async Task Producer_MethodOverride_FromHeader()
    {
        var endpoint = CreateEndpointWithPath("/api/test"); // default GET
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange("""{"data":1}""");
        exchange.In.Headers[HttpHeaders.Method] = "POST";
        await producer.Process(exchange);

        _lastRequestMethod.Should().Be("POST");

        await producer.Stop();
    }

    // ── Query parameters from header ──

    [Fact]
    public async Task Producer_QueryParameters_FromHeader()
    {
        var endpoint = CreateEndpointWithPath("/api/search");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        exchange.In.Headers[HttpHeaders.Query] = "q=test&page=1";
        await producer.Process(exchange);

        _lastRequestPath.Should().Be("/api/search?q=test&page=1");

        await producer.Stop();
    }

    // ── Response handling ──

    [Fact]
    public async Task Producer_ResponseBody_MappedToOutMessage()
    {
        _responseBody = """{"result":"success","count":42}""";
        _responseContentType = "application/json";

        var endpoint = CreateEndpointWithPath("/api/test");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var body = Encoding.UTF8.GetString((byte[])exchange.Out!.Body!);
        body.Should().Be("""{"result":"success","count":42}""");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_ResponseHeaders_MappedToOutHeaders()
    {
        _responseHeaders["X-Custom-Header"] = "test-value";

        var endpoint = CreateEndpointWithPath("/api/test");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        exchange.Out!.Headers[HttpHeaders.StatusCode].Should().Be(200);
        exchange.Out!.Headers.Should().ContainKey(HttpHeaders.StatusText);
        exchange.Out!.Headers.Should().ContainKey("X-Custom-Header");
        exchange.Out!.Headers["X-Custom-Header"].Should().Be("test-value");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_CopyResponseHeaders_False_SkipsHeaders()
    {
        _responseHeaders["X-Custom-Header"] = "test-value";

        var endpoint = CreateEndpointWithPath("/api/test", new Dictionary<string, string>
        {
            ["copyResponseHeaders"] = "false"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        // StatusCode is always set, but custom headers are not copied
        exchange.Out!.Headers[HttpHeaders.StatusCode].Should().Be(200);
        exchange.Out!.Headers.Should().NotContainKey("X-Custom-Header");

        await producer.Stop();
    }

    // ── Error handling ──

    [Fact]
    public async Task Producer_ThrowOnError_True_ThrowsOn4xx()
    {
        _responseStatusCode = 404;
        _responseBody = "Not Found";

        var endpoint = CreateEndpointWithPath("/api/missing");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<HttpRequestException>();

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_ThrowOnError_True_ThrowsOn5xx()
    {
        _responseStatusCode = 500;
        _responseBody = "Internal Server Error";

        var endpoint = CreateEndpointWithPath("/api/fail");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<HttpRequestException>();

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_ThrowOnError_False_DoesNotThrow()
    {
        _responseStatusCode = 500;
        _responseBody = "Error";

        var endpoint = CreateEndpointWithPath("/api/fail", new Dictionary<string, string>
        {
            ["throwOnError"] = "false"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange); // should not throw

        exchange.Out!.Headers[HttpHeaders.StatusCode].Should().Be(500);

        await producer.Stop();
    }

    // ── Authentication ──

    [Fact]
    public async Task Producer_BasicAuth_SetsAuthorizationHeader()
    {
        var endpoint = CreateEndpointWithPath("/api/secure", new Dictionary<string, string>
        {
            ["authScheme"] = "Basic",
            ["username"] = "admin",
            ["password"] = "secret"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        _lastRequestHeaders.Should().ContainKey("Authorization");
        var authHeader = _lastRequestHeaders["Authorization"];
        authHeader.Should().StartWith("Basic ");
        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(authHeader["Basic ".Length..]));
        decoded.Should().Be("admin:secret");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_BearerAuth_SetsAuthorizationHeader()
    {
        var endpoint = CreateEndpointWithPath("/api/secure", new Dictionary<string, string>
        {
            ["authScheme"] = "Bearer",
            ["authToken"] = "my-secret-token-123"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        _lastRequestHeaders.Should().ContainKey("Authorization");
        _lastRequestHeaders["Authorization"].Should().Be("Bearer my-secret-token-123");

        await producer.Stop();
    }

    // ── Header bridging ──

    [Fact]
    public async Task Producer_BridgeHeaders_True_SendsExchangeHeaders()
    {
        var endpoint = CreateEndpointWithPath("/api/test");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        exchange.In.Headers["X-Request-Id"] = "req-123";
        exchange.In.Headers["X-Correlation-Id"] = "corr-456";
        await producer.Process(exchange);

        _lastRequestHeaders.Should().ContainKey("X-Request-Id");
        _lastRequestHeaders["X-Request-Id"].Should().Be("req-123");
        _lastRequestHeaders.Should().ContainKey("X-Correlation-Id");
        _lastRequestHeaders["X-Correlation-Id"].Should().Be("corr-456");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_BridgeHeaders_False_DoesNotSendExchangeHeaders()
    {
        var endpoint = CreateEndpointWithPath("/api/test", new Dictionary<string, string>
        {
            ["bridgeHeaders"] = "false"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        exchange.In.Headers["X-Request-Id"] = "req-123";
        await producer.Process(exchange);

        _lastRequestHeaders.Should().NotContainKey("X-Request-Id");

        await producer.Stop();
    }

    // ── ContentType override from exchange header ──

    [Fact]
    public async Task Producer_ContentType_OverriddenFromExchangeHeader()
    {
        var endpoint = CreateEndpointWithPath("/api/data", new Dictionary<string, string>
        {
            ["method"] = "POST"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange("<xml>data</xml>");
        exchange.In.Headers[HttpHeaders.ContentType] = "text/xml";
        await producer.Process(exchange);

        _lastRequestContentType.Should().Contain("text/xml");

        await producer.Stop();
    }

    // ── Lifecycle ──

    [Fact]
    public async Task Producer_Process_BeforeStart_Throws()
    {
        var endpoint = CreateEndpointWithPath("/api/test");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);

        var exchange = CreateExchange();
        var act = () => producer.Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not been started*");
    }

    [Fact]
    public async Task Producer_StartStop_Lifecycle()
    {
        var endpoint = CreateEndpointWithPath("/api/test");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);

        await producer.Start();
        await producer.Process(CreateExchange()); // should work
        await producer.Stop();
    }

    // ── Timeout ──

    [Fact]
    public async Task Producer_Timeout_Zero_InfiniteTimeout()
    {
        var endpoint = CreateEndpointWithPath("/api/test", new Dictionary<string, string>
        {
            ["timeout"] = "0"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        // Should not throw — infinite timeout
        await producer.Process(CreateExchange());

        await producer.Stop();
    }

    // ── Null body for GET ──

    [Fact]
    public async Task Producer_GET_NullBody_NoRequestBody()
    {
        var endpoint = CreateEndpointWithPath("/api/test");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange(null);
        await producer.Process(exchange);

        _lastRequestBody.Should().BeNull();

        await producer.Stop();
    }

    // ── Named Parameters {name} ──

    [Fact]
    public async Task Producer_NamedParam_ConstantValue_ReplacedInPath()
    {
        var endpoint = CreateEndpointWithFullPath(
            $"/localhost:{_port}/api/users/{{userId}}/orders",
            new Dictionary<string, string>
            {
                ["method"] = "GET",
                ["param.userId"] = "42"
            });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        _lastRequestPath.Should().Be("/api/users/42/orders");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_NamedParam_ExpressionValue_ResolvedFromHeader()
    {
        var endpoint = CreateEndpointWithFullPath(
            $"/localhost:{_port}/api/users/{{userId}}/profile",
            new Dictionary<string, string>
            {
                ["method"] = "GET",
                ["param.userId"] = "${header.userId}"
            });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        exchange.In.Headers["userId"] = "99";
        await producer.Process(exchange);

        _lastRequestPath.Should().Be("/api/users/99/profile");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_NamedParam_MultipleParams_AllReplaced()
    {
        var endpoint = CreateEndpointWithFullPath(
            $"/localhost:{_port}/api/{{version}}/users/{{userId}}",
            new Dictionary<string, string>
            {
                ["method"] = "GET",
                ["param.version"] = "v2",
                ["param.userId"] = "${header.uid}"
            });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        exchange.In.Headers["uid"] = "777";
        await producer.Process(exchange);

        _lastRequestPath.Should().Be("/api/v2/users/777");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_NamedParam_InQueryString_ReplacedInUrl()
    {
        var endpoint = CreateEndpointWithFullPath(
            $"/localhost:{_port}/api/search?q={{query}}&limit={{limit}}",
            new Dictionary<string, string>
            {
                ["method"] = "GET",
                ["param.query"] = "${header.q}",
                ["param.limit"] = "10"
            });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        exchange.In.Headers["q"] = "test";
        await producer.Process(exchange);

        _lastRequestPath.Should().Be("/api/search?q=test&limit=10");

        await producer.Stop();
    }

    // ── StreamResponse ──

    [Fact]
    public async Task Producer_StreamResponse_BodyIsStream()
    {
        _responseBody = """{"data":"streamed"}""";
        _responseContentType = "application/json";

        var endpoint = CreateEndpointWithPath("/api/test", new Dictionary<string, string>
        {
            ["streamResponse"] = "true"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().BeAssignableTo<Stream>();

        // Read the stream to verify data
        var stream = (Stream)exchange.Out!.Body!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        content.Should().Be("""{"data":"streamed"}""");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_StreamResponse_False_BodyIsByteArray()
    {
        _responseBody = "bytes-response";

        var endpoint = CreateEndpointWithPath("/api/test");
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        exchange.Out!.Body.Should().BeOfType<byte[]>();

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_StreamResponse_HeadersStillMapped()
    {
        _responseHeaders["X-Custom"] = "val";

        var endpoint = CreateEndpointWithPath("/api/test", new Dictionary<string, string>
        {
            ["streamResponse"] = "true"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        exchange.Out!.Headers[HttpHeaders.StatusCode].Should().Be(200);
        exchange.Out!.Headers.Should().ContainKey("X-Custom");

        await producer.Stop();
    }

    [Fact]
    public async Task Producer_StreamResponse_BinaryData_Preserved()
    {
        // Use a test server that returns binary
        _responseBody = "binary-test";
        _responseContentType = "application/octet-stream";

        var endpoint = CreateEndpointWithPath("/api/binary", new Dictionary<string, string>
        {
            ["streamResponse"] = "true"
        });
        var producer = new HttpProducer(endpoint, endpoint.EndpointOptions);
        await producer.Start();

        var exchange = CreateExchange();
        await producer.Process(exchange);

        var stream = (Stream)exchange.Out!.Body!;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        text.Should().Be("binary-test");

        await producer.Stop();
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
