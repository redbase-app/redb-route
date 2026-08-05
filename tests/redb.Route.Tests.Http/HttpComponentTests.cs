using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using HttpMethod = redb.Route.Http.HttpMethod;
using HttpDsl = redb.Route.Http.Http;

namespace redb.Route.Tests.Http;

public class HttpComponentTests
{
    [Fact]
    public void Scheme_ReturnsHttp()
    {
        var component = new HttpComponent();
        component.Scheme.Should().Be("http");
    }

    [Fact]
    public void HttpsComponent_Scheme_ReturnsHttps()
    {
        var component = new HttpsComponent();
        component.Scheme.Should().Be("https");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsHttpEndpoint()
    {
        var component = new HttpComponent();
        var parameters = new Dictionary<string, string>();
        var uri = new EndpointUri("http", "/api.example.com/orders", "http:api.example.com/orders", parameters);

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<HttpEndpoint>();
        endpoint.Uri.Should().BeSameAs(uri);
        endpoint.Component.Should().BeSameAs(component);
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new HttpComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_WithOptions_BindsCorrectly()
    {
        var component = new HttpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "POST",
            ["timeout"] = "60000",
            ["throwOnError"] = "false",
            ["contentType"] = "text/xml",
            ["bridgeHeaders"] = "false"
        };
        var uri = new EndpointUri("http", "/api.example.com/data", "http:api.example.com/data", parameters);

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Method.Should().Be(HttpMethod.POST);
        endpoint.EndpointOptions.Timeout.Should().Be(60000);
        endpoint.EndpointOptions.ThrowOnError.Should().BeFalse();
        endpoint.EndpointOptions.ContentType.Should().Be("text/xml");
        endpoint.EndpointOptions.BridgeHeaders.Should().BeFalse();
    }

    [Fact]
    public void CreateEndpoint_ConsumerOptions_BindsCorrectly()
    {
        var component = new HttpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = "9090",
            ["methods"] = "POST,PUT",
            ["cors"] = "true",
            ["corsOrigins"] = "https://example.com",
            ["inOut"] = "true",
            ["responseCode"] = "201",
            ["maxRequestBodySize"] = "1048576"
        };
        var uri = new EndpointUri("http", "/0.0.0.0:9090/webhook", "http:0.0.0.0:9090/webhook", parameters);

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Host.Should().Be("127.0.0.1");
        endpoint.EndpointOptions.Port.Should().Be(9090);
        endpoint.EndpointOptions.Methods.Should().Be("POST,PUT");
        endpoint.EndpointOptions.Cors.Should().BeTrue();
        endpoint.EndpointOptions.CorsOrigins.Should().Be("https://example.com");
        endpoint.EndpointOptions.InOut.Should().BeTrue();
        endpoint.EndpointOptions.ResponseCode.Should().Be(201);
        endpoint.EndpointOptions.MaxRequestBodySize.Should().Be(1048576);
    }

    [Fact]
    public void CreateEndpoint_AuthOptions_BindsCorrectly()
    {
        var component = new HttpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["authScheme"] = "Basic",
            ["username"] = "admin",
            ["password"] = "secret",
            ["followRedirects"] = "false",
            ["maxRedirects"] = "5",
            ["copyResponseHeaders"] = "false"
        };
        var uri = new EndpointUri("http", "/api.example.com/secure", "http:api.example.com/secure", parameters);

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.AuthScheme.Should().Be(HttpAuthScheme.Basic);
        endpoint.EndpointOptions.Username.Should().Be("admin");
        endpoint.EndpointOptions.Password.Should().Be("secret");
        endpoint.EndpointOptions.FollowRedirects.Should().BeFalse();
        endpoint.EndpointOptions.MaxRedirects.Should().Be(5);
        endpoint.EndpointOptions.CopyResponseHeaders.Should().BeFalse();
    }

    [Fact]
    public void CreateProducer_ReturnsHttpProducer()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "/api.example.com/test", "http:api.example.com/test", new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);

        var producer = endpoint.CreateProducer();

        producer.Should().BeOfType<HttpProducer>();
    }

    [Fact]
    public void CreateConsumer_ReturnsHttpConsumer()
    {
        var component = new HttpComponent();
        component.ServerManager = new SharedHttpServerManager();
        var uri = new EndpointUri("http", "/0.0.0.0:8080/webhook", "http:0.0.0.0:8080/webhook", new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().BeOfType<HttpConsumer>();
    }

    [Fact]
    public void CreateConsumer_NullProcessor_Throws()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "/0.0.0.0:8080/webhook", "http:0.0.0.0:8080/webhook", new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);

        var act = () => endpoint.CreateConsumer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void HttpEndpoint_IsHttps_ReturnsFalseForHttp()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "/api.example.com/test", "http:api.example.com/test", new Dictionary<string, string>());
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.IsHttps.Should().BeFalse();
        endpoint.SchemePrefix.Should().Be("http://");
    }

    [Fact]
    public void HttpEndpoint_IsHttps_ReturnsTrueForHttps()
    {
        var component = new HttpsComponent();
        var uri = new EndpointUri("https", "/api.example.com/test", "https:api.example.com/test", new Dictionary<string, string>());
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.IsHttps.Should().BeTrue();
        endpoint.SchemePrefix.Should().Be("https://");
    }

    [Fact]
    public void HttpEndpoint_BuildProducerUrl_IncludesScheme()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "/api.example.com/orders", "http:api.example.com/orders", new Dictionary<string, string>());
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.BuildProducerUrl().Should().Be("http://api.example.com/orders");
    }

    [Fact]
    public void HttpEndpoint_BuildProducerUrl_Https()
    {
        var component = new HttpsComponent();
        var uri = new EndpointUri("https", "/api.example.com/orders", "https:api.example.com/orders", new Dictionary<string, string>());
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.BuildProducerUrl().Should().Be("https://api.example.com/orders");
    }

    [Theory]
    [InlineData("/0.0.0.0:8080/webhook", "/webhook")]
    [InlineData("/0.0.0.0:8080", "/")]
    [InlineData("/localhost:9090/api/v1", "/api/v1")]
    [InlineData("localhost:9090/api/v1", "/api/v1")]
    [InlineData("0.0.0.0:8080", "/")]
    public void HttpEndpoint_ConsumerPath_VariousFormats(string path, string expected)
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", path, $"http:{path}", new Dictionary<string, string>());
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.ConsumerPath.Should().Be(expected);
    }

    [Theory]
    [InlineData("/api/honest/echo", "/api/honest/echo")]
    [InlineData("/api", "/api")]
    [InlineData("/webhook", "/webhook")]
    [InlineData("/", "/")]
    public void ConsumerPath_FluentListenWithHostPort_KeepsFullRoutePath(string listenPath, string expected)
    {
        // Regression: Http.Listen("/api/honest/echo").Host(..).Port(..) must register the FULL path,
        // not drop the first segment. The fluent DSL puts host/port in the query string
        // ("http:/api/honest/echo?host=0.0.0.0&port=5092"), so the whole leading-slash path is the route.
        var uri = EndpointUriParser.Parse(HttpDsl.Listen(listenPath).Host("0.0.0.0").Port(5092));
        var endpoint = (HttpEndpoint)new HttpComponent().CreateEndpoint(uri);

        endpoint.ConsumerPath.Should().Be(expected);
        endpoint.EndpointOptions.Host.Should().Be("0.0.0.0");
        endpoint.EndpointOptions.Port.Should().Be(5092);
    }

    [Fact]
    public void ConsumerPath_FluentListenPortOnly_KeepsFullRoutePath()
    {
        // Port-only (host defaulted) still puts port in the query, so the path stays the route.
        var uri = EndpointUriParser.Parse(HttpDsl.Listen("/api/orders").Port(8080));
        var endpoint = (HttpEndpoint)new HttpComponent().CreateEndpoint(uri);

        endpoint.ConsumerPath.Should().Be("/api/orders");
    }

    // ── DefaultCors (component-level CORS) ──

    [Fact]
    public void DefaultCors_Disabled_ByDefault()
    {
        var component = new HttpComponent();
        component.DefaultCors.Enabled.Should().BeFalse();
        component.DefaultCors.Origins.Should().BeNull();
        component.DefaultCors.Credentials.Should().BeFalse();
    }

    [Fact]
    public void DefaultCors_AppliedWhenEndpointOmitsCors()
    {
        var component = new HttpComponent();
        component.DefaultCors.Enabled = true;
        component.DefaultCors.Origins = "https://example.com";
        component.DefaultCors.Credentials = true;

        // No cors params in URI
        var uri = new EndpointUri("http", "/0.0.0.0:8080/webhook", "http:0.0.0.0:8080/webhook", new Dictionary<string, string>());
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Cors.Should().BeTrue();
        endpoint.EndpointOptions.CorsOrigins.Should().Be("https://example.com");
        endpoint.EndpointOptions.CorsCredentials.Should().BeTrue();
    }

    [Fact]
    public void DefaultCors_EndpointOverridesGlobal()
    {
        var component = new HttpComponent();
        component.DefaultCors.Enabled = true;
        component.DefaultCors.Origins = "https://global.com";
        component.DefaultCors.Credentials = true;

        // Endpoint explicitly sets different values
        var uri = new EndpointUri("http", "/0.0.0.0:8080/webhook", "http:0.0.0.0:8080/webhook",
            new Dictionary<string, string>
            {
                ["cors"] = "true",
                ["corsOrigins"] = "https://override.com",
                ["corsCredentials"] = "false"
            });
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.CorsOrigins.Should().Be("https://override.com");
        endpoint.EndpointOptions.CorsCredentials.Should().BeFalse();
    }

    [Fact]
    public void DefaultCors_EndpointDisablesCors_GlobalEnabled()
    {
        var component = new HttpComponent();
        component.DefaultCors.Enabled = true;

        var uri = new EndpointUri("http", "/0.0.0.0:8080/internal", "http:0.0.0.0:8080/internal",
            new Dictionary<string, string> { ["cors"] = "false" });
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Cors.Should().BeFalse();
    }

    [Fact]
    public void DefaultCors_EndpointEnablesCors_GlobalDisabled()
    {
        var component = new HttpComponent();
        // DefaultCors.Enabled is false by default

        // The strict-CORS contract requires an explicit origin policy when Cors=true.
        // Endpoints that opt in via the URI must therefore also supply corsOrigins (or a resolver).
        var uri = new EndpointUri("http", "/0.0.0.0:8080/webhook", "http:0.0.0.0:8080/webhook",
            new Dictionary<string, string> { ["cors"] = "true", ["corsOrigins"] = "*" });
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Cors.Should().BeTrue();
    }

    // ── Method prefix shorthand (http:GET:/path) ──

    [Fact]
    public void CreateEndpoint_MethodPrefix_SetsMethodAndMethods()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "POST:/connect/token", "http://POST:/connect/token", new Dictionary<string, string>());

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Method.Should().Be(HttpMethod.POST);
        endpoint.EndpointOptions.Methods.Should().Be("POST");
        endpoint.ConsumerPath.Should().Be("/connect/token");
    }

    [Fact]
    public void CreateEndpoint_MethodPrefix_Get_SetsConsumerPath()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "GET:/.well-known/openid-configuration", "http://GET:/.well-known/openid-configuration", new Dictionary<string, string>());

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Methods.Should().Be("GET");
        endpoint.ConsumerPath.Should().Be("/.well-known/openid-configuration");
    }

    [Fact]
    public void CreateEndpoint_MethodPrefix_WithHostPort()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "POST:0.0.0.0:8080/api/orders", "http://POST:0.0.0.0:8080/api/orders", new Dictionary<string, string>());

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Method.Should().Be(HttpMethod.POST);
        endpoint.EndpointOptions.Methods.Should().Be("POST");
        endpoint.EndpointOptions.Host.Should().Be("0.0.0.0");
        endpoint.EndpointOptions.Port.Should().Be(8080);
        endpoint.ConsumerPath.Should().Be("/api/orders");
    }

    [Fact]
    public void CreateEndpoint_MethodPrefix_CaseInsensitive()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "get:/test", "http://get:/test", new Dictionary<string, string>());

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Method.Should().Be(HttpMethod.GET);
        endpoint.EndpointOptions.Methods.Should().Be("GET");
    }

    [Fact]
    public void CreateEndpoint_MethodPrefix_QueryParamsTakePriority()
    {
        var component = new HttpComponent();
        var parameters = new Dictionary<string, string> { ["methods"] = "POST,PUT" };
        var uri = new EndpointUri("http", "GET:/test", "http://GET:/test?methods=POST,PUT", parameters);

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        // Query param wins over prefix
        endpoint.EndpointOptions.Methods.Should().Be("POST,PUT");
    }

    [Fact]
    public void CreateEndpoint_NoMethodPrefix_WorksAsUsual()
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "0.0.0.0:8080/webhook", "http://0.0.0.0:8080/webhook", new Dictionary<string, string>());

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Host.Should().Be("0.0.0.0");
        endpoint.EndpointOptions.Port.Should().Be(8080);
        endpoint.ConsumerPath.Should().Be("/webhook");
    }

    [Fact]
    public void CreateEndpoint_HostnameLikeMethod_NotConfused()
    {
        // "getdata" should NOT be parsed as "GET" — must be exact match
        var component = new HttpComponent();
        var uri = new EndpointUri("http", "getdata.example.com/api", "http://getdata.example.com/api", new Dictionary<string, string>());

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Methods.Should().BeNull();
        endpoint.ConsumerPath.Should().Be("/api");
    }

    [Theory]
    [InlineData("DELETE:/resource/{id}", "DELETE", "/resource/{id}")]
    [InlineData("PATCH:/users/123", "PATCH", "/users/123")]
    [InlineData("PUT:/items", "PUT", "/items")]
    public void CreateEndpoint_MethodPrefix_AllMethods(string path, string expectedMethod, string expectedConsumerPath)
    {
        var component = new HttpComponent();
        var uri = new EndpointUri("http", path, $"http://{path}", new Dictionary<string, string>());

        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Methods.Should().Be(expectedMethod);
        endpoint.ConsumerPath.Should().Be(expectedConsumerPath);
    }
}
