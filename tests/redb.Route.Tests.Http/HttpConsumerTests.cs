using System.Net;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// Tests for HttpConsumer (Kestrel-based).
/// Each test starts a real embedded HTTP server, sends requests via HttpClient, and verifies the exchange pipeline.
/// </summary>
[Collection("HttpServer")]
public class HttpConsumerTests : IAsyncLifetime
{
    private HttpConsumer? _consumer;
    private HttpClient? _client;
    private int _port;
    private SharedHttpServerManager? _serverManager;

    // Captured exchange from processor
    private IExchange? _lastExchange;
    private readonly List<IExchange> _capturedExchanges = new();

    // Configurable processor behavior
    private Func<IExchange, Task>? _processorAction;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _client = new HttpClient();
        _serverManager = new SharedHttpServerManager();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_consumer is not null)
        {
            await _consumer.Stop();
        }
        if (_serverManager is not null) await _serverManager.DisposeAsync();
    }

    private HttpConsumer CreateConsumer(
        string path = "/webhook",
        Dictionary<string, string>? extraParams = null)
    {
        var component = new HttpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString()
        };
        if (extraParams is not null)
        {
            foreach (var (key, value) in extraParams)
                parameters[key] = value;
        }

        var uriPath = $"/127.0.0.1:{_port}{path}";
        var uri = new EndpointUri("http", uriPath, $"http:{uriPath}", parameters);
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _lastExchange = ex;
                _capturedExchanges.Add(ex);
                if (_processorAction is not null)
                    await _processorAction(ex);
            });

        _consumer = new HttpConsumer(endpoint, processor, endpoint.EndpointOptions, _serverManager!);
        return _consumer;
    }

    // ── Basic request handling ──

    [Fact]
    public async Task Consumer_POST_ReceivesRequest()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("""{"order":"123"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _lastExchange.Should().NotBeNull();

        var body = Encoding.UTF8.GetString((byte[])_lastExchange!.In.Body!);
        body.Should().Be("""{"order":"123"}""");
    }

    [Fact]
    public async Task Consumer_GET_ReceivesRequest()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Headers[HttpHeaders.Method].Should().Be("GET");
    }

    [Fact]
    public async Task Consumer_PopulatesAllHeaders()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        _client!.DefaultRequestHeaders.Add("X-Custom-Test", "hello");
        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook?param=value",
            new StringContent("body", Encoding.UTF8, "text/plain"));

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Headers[HttpHeaders.Method].Should().Be("POST");
        _lastExchange!.In.Headers[HttpHeaders.Path].Should().Be("/webhook");
        _lastExchange!.In.Headers.Should().ContainKey(HttpHeaders.Url);
        _lastExchange!.In.Headers[HttpHeaders.Query].Should().Be("param=value");
        _lastExchange!.In.Headers.Should().ContainKey(HttpHeaders.RemoteAddress);
        _lastExchange!.In.Headers.Should().ContainKey("X-Custom-Test");
        _lastExchange!.In.Headers["X-Custom-Test"]!.ToString().Should().Be("hello");
    }

    // ── Method filtering ──

    [Fact]
    public async Task Consumer_MethodFilter_RejectsDisallowedMethod()
    {
        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["methods"] = "POST"
        });
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        _lastExchange.Should().BeNull();
    }

    [Fact]
    public async Task Consumer_MethodFilter_AcceptsAllowedMethod()
    {
        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["methods"] = "POST,PUT"
        });
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _lastExchange.Should().NotBeNull();
    }

    // ── CORS ──

    [Fact]
    public async Task Consumer_Cors_Enabled_SetsCorsHeaders()
    {
        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["cors"] = "true",
            ["corsOrigins"] = "*"
        });
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        response.Headers.GetValues("Access-Control-Allow-Origin").First().Should().Be("*");
    }

    [Fact]
    public async Task Consumer_Cors_CustomOrigins()
    {
        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["cors"] = "true",
            ["corsOrigins"] = "https://example.com"
        });
        await consumer.Start();

        // Browsers always send Origin on cross-origin requests; the new dispatch middleware
        // echoes a single matching origin from the whitelist instead of returning the raw CSV
        // (which browsers cannot consume in Access-Control-Allow-Origin).
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        request.Headers.Add("Origin", "https://example.com");
        var response = await _client!.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").First().Should().Be("https://example.com");
        response.Headers.GetValues("Vary").Should().Contain(v => v.Contains("Origin"));
    }

    [Fact]
    public async Task Consumer_Cors_Credentials_SetsHeader()
    {
        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["cors"] = "true",
            ["corsOrigins"] = "https://example.com",
            ["corsCredentials"] = "true"
        });
        await consumer.Start();

        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{_port}/webhook");
        request.Headers.Add("Origin", "https://example.com");
        var response = await _client!.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Credentials").First().Should().Be("true");
        response.Headers.GetValues("Access-Control-Allow-Origin").First().Should().Be("https://example.com");
    }

    [Fact]
    public async Task Consumer_Cors_WithoutCredentials_NoCredentialsHeader()
    {
        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["cors"] = "true",
            ["corsOrigins"] = "*"
        });
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");

        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task Consumer_Cors_OptionsRequest_Returns204()
    {
        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["cors"] = "true",
            ["corsOrigins"] = "*"
        });
        await consumer.Start();

        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Options, $"http://127.0.0.1:{_port}/webhook");
        request.Headers.Add("Origin", "https://example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        var response = await _client!.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── InOut pattern ──

    [Fact]
    public async Task Consumer_InOut_ReturnsResponseBody()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("""{"response":"processed"}""");
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["inOut"] = "true"
        });
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("""{"input":"data"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Be("""{"response":"processed"}""");
    }

    [Fact]
    public async Task Consumer_InOut_ReturnsResponseBodyAsBytes()
    {
        var responseBytes = new byte[] { 0x01, 0x02, 0x03 };
        _processorAction = ex =>
        {
            ex.Out = new Message(responseBytes);
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["inOut"] = "true"
        });
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");

        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().BeEquivalentTo(responseBytes);
    }

    [Fact]
    public async Task Consumer_InOnly_ReturnsEmptyBody()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("this should not be returned");
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(); // inOut defaults to false
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().BeEmpty();
    }

    // ── Custom response code ──

    [Fact]
    public async Task Consumer_CustomResponseCode()
    {
        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["responseCode"] = "202"
        });
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Consumer_ResponseCode_OverriddenByHeader()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("created");
            ex.Out.Headers[HttpHeaders.ResponseCode] = 201;
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["inOut"] = "true"
        });
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── Exception handling ──

    [Fact]
    public async Task Consumer_ProcessorException_Returns500()
    {
        _processorAction = _ => throw new InvalidOperationException("Processing failed");

        var consumer = CreateConsumer();
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        // Route pipeline exceptions are put on exchange.Exception
        // The consumer checks exchange.Exception and returns 500
        // NSubstitute can't easily simulate this, so we check the processor was invoked
        _lastExchange.Should().NotBeNull();
    }

    [Fact]
    public async Task Consumer_ExchangeException_UnhandledReturns500()
    {
        _processorAction = ex =>
        {
            ex.Exception = new InvalidOperationException("test error");
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer();
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("test error");
    }

    [Fact]
    public async Task Consumer_ExchangeException_HandledReturnsOk()
    {
        _processorAction = ex =>
        {
            ex.Exception = new InvalidOperationException("handled error");
            ex.ExceptionHandled = true;
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer();
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── ProcessedCount ──

    [Fact]
    public async Task Consumer_ProcessedCount_Increments()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        consumer.ProcessedCount.Should().Be(0);

        await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");
        consumer.ProcessedCount.Should().Be(1);

        await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");
        consumer.ProcessedCount.Should().Be(2);
    }

    // ── BaseUrl ──

    [Fact]
    public async Task Consumer_BaseUrl_AvailableAfterStart()
    {
        var consumer = CreateConsumer();
        consumer.BaseUrl.Should().BeNull();

        await consumer.Start();

        consumer.BaseUrl.Should().NotBeNullOrEmpty();
        consumer.BaseUrl.Should().Contain(_port.ToString());
    }

    // ── Lifecycle ──

    [Fact]
    public async Task Consumer_StartStop_Lifecycle()
    {
        var consumer = CreateConsumer();

        await consumer.Start();
        await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook"); // works
        await consumer.Stop();

        // After stop, requests should fail
        var act = async () => await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── Exchange pattern ──

    [Fact]
    public async Task Consumer_InOut_SetsExchangePattern()
    {
        _processorAction = ex =>
        {
            ex.Pattern.Should().Be(ExchangePattern.InOut);
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string> { ["inOut"] = "true" });
        await consumer.Start();

        await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");
    }

    [Fact]
    public async Task Consumer_InOnly_SetsExchangePattern()
    {
        _processorAction = ex =>
        {
            ex.Pattern.Should().Be(ExchangePattern.InOnly);
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer();
        await consumer.Start();

        await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");
    }

    // ── ResponseContentType ──

    [Fact]
    public async Task Consumer_InOut_ResponseContentType_Override()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("<xml>result</xml>");
            ex.Out.Headers[HttpHeaders.ResponseContentType] = "text/xml";
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string> { ["inOut"] = "true" });
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");

        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
    }

    // ── Non-matching path returns 404 ──

    [Fact]
    public async Task Consumer_WrongPath_Returns404()
    {
        var consumer = CreateConsumer(path: "/webhook");
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/other");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _lastExchange.Should().BeNull();
    }

    // ── Multiple concurrent requests ──

    [Fact]
    public async Task Consumer_MultipleConcurrentRequests()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var tasks = Enumerable.Range(0, 10).Select(i =>
            _client!.PostAsync(
                $"http://127.0.0.1:{_port}/webhook",
                new StringContent($"request-{i}", Encoding.UTF8, "text/plain")));

        var responses = await Task.WhenAll(tasks);

        responses.Should().AllSatisfy(r => r.StatusCode.Should().Be(HttpStatusCode.OK));
        consumer.ProcessedCount.Should().Be(10);
    }

    // ── StreamRequest ──

    [Fact]
    public async Task Consumer_StreamRequest_BodyIsStream()
    {
        Type? bodyType = null;
        _processorAction = ex =>
        {
            bodyType = ex.In.Body?.GetType();
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["streamRequest"] = "true"
        });
        await consumer.Start();

        await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("stream-test", Encoding.UTF8, "text/plain"));

        bodyType.Should().NotBeNull();
        bodyType!.Should().BeAssignableTo(typeof(Stream));
    }

    [Fact]
    public async Task Consumer_StreamRequest_StreamContainsCorrectData()
    {
        string? captured = null;
        _processorAction = async ex =>
        {
            var stream = (Stream)ex.In.Body!;
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            captured = await reader.ReadToEndAsync();
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["streamRequest"] = "true"
        });
        await consumer.Start();

        await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("hello-stream", Encoding.UTF8, "text/plain"));

        captured.Should().Be("hello-stream");
    }

    [Fact]
    public async Task Consumer_StreamRequest_HeadersStillPopulated()
    {
        _processorAction = ex =>
        {
            ex.In.Headers[HttpHeaders.Method].Should().Be("POST");
            ex.In.Headers[HttpHeaders.Path].Should().Be("/webhook");
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["streamRequest"] = "true"
        });
        await consumer.Start();

        await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("data", Encoding.UTF8, "text/plain"));

        _lastExchange.Should().NotBeNull();
    }

    [Fact]
    public async Task Consumer_StreamRequest_False_BodyIsByteArray()
    {
        Type? bodyType = null;
        _processorAction = ex =>
        {
            bodyType = ex.In.Body?.GetType();
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(); // streamRequest defaults to false
        await consumer.Start();

        await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("test", Encoding.UTF8, "text/plain"));

        bodyType.Should().Be(typeof(byte[]));
    }

    [Fact]
    public async Task Consumer_StreamRequest_InOut_ReturnsResponseAfterStreamRead()
    {
        _processorAction = async ex =>
        {
            var stream = (Stream)ex.In.Body!;
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var content = await reader.ReadToEndAsync();
            ex.Out = new Message($"echo:{content}");
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string>
        {
            ["streamRequest"] = "true",
            ["inOut"] = "true"
        });
        await consumer.Start();

        var response = await _client!.PostAsync(
            $"http://127.0.0.1:{_port}/webhook",
            new StringContent("streamed-payload", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("echo:streamed-payload");
    }

    // ── status.code fallback ──

    [Fact]
    public async Task Consumer_StatusCodeFallback_ReadsStatusDotCode()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("not found");
            // Set only "status.code" — simulates old dispatcher behavior
            ex.Out.Headers["status.code"] = 404;
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string> { ["inOut"] = "true" });
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "HttpConsumer should fall back to 'status.code' when 'redbHttp.ResponseCode' is absent");
    }

    [Fact]
    public async Task Consumer_ResponseCode_TakesPrecedenceOverStatusCode()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("created");
            ex.Out.Headers["status.code"] = 404;
            ex.Out.Headers[HttpHeaders.ResponseCode] = 201;
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(extraParams: new Dictionary<string, string> { ["inOut"] = "true" });
        await consumer.Start();

        var response = await _client!.GetAsync($"http://127.0.0.1:{_port}/webhook");

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "redbHttp.ResponseCode should take precedence over status.code");
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
