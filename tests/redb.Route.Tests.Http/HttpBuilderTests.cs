using redb.Route.Core;
using redb.Route.Expressions;
using HttpDsl = redb.Route.Http.Http;
using HttpsDsl = redb.Route.Http.Https;

namespace redb.Route.Tests.Http;

public class HttpBuilderTests
{
    private static ConstantExpression C(string s) => new(s);
    // ── Factory methods ─────────────────────────────────────────────

    [Fact]
    public void Get_SetsMethodParam()
    {
        var uri = HttpDsl.Get("api.example.com/users").Build();
        uri.Should().Contain("method=GET");
    }

    [Fact]
    public void Post_SetsMethodParam()
    {
        var uri = HttpDsl.Post("api.example.com/users").Build();
        uri.Should().Contain("method=POST");
    }

    [Fact]
    public void Put_SetsMethodParam()
    {
        var uri = HttpDsl.Put("api.example.com/users/1").Build();
        uri.Should().Contain("method=PUT");
    }

    [Fact]
    public void Delete_SetsMethodParam()
    {
        var uri = HttpDsl.Delete("api.example.com/users/1").Build();
        uri.Should().Contain("method=DELETE");
    }

    [Fact]
    public void Patch_SetsMethodParam()
    {
        var uri = HttpDsl.Patch("api.example.com/users/1").Build();
        uri.Should().Contain("method=PATCH");
    }

    [Fact]
    public void Listen_HasNoMethodParam()
    {
        var uri = HttpDsl.Listen("/webhook").Build();
        uri.Should().NotContain("method=");
    }

    [Fact]
    public void NullPath_Throws()
    {
        var act = () => HttpDsl.Get(null!);
        act.Should().Throw<ArgumentException>();
    }

    // ── Scheme ──────────────────────────────────────────────────────

    [Fact]
    public void Http_StartsWithHttpScheme()
    {
        var uri = HttpDsl.Get("api.example.com").Build();
        uri.Should().StartWith("http:");
    }

    [Fact]
    public void Https_StartsWithHttpsScheme()
    {
        var uri = HttpsDsl.Get("api.example.com").Build();
        uri.Should().StartWith("https:");
    }

    // ── Common params ───────────────────────────────────────────────

    [Fact]
    public void Timeout_SetsParam()
    {
        var uri = HttpDsl.Get("api.example.com").Timeout(5000).Build();
        uri.Should().Contain("timeout=5000");
    }

    [Fact]
    public void ContentType_SetsParam()
    {
        var uri = HttpDsl.Post("api.example.com").ContentType("text/xml").Build();
        uri.Should().Contain("contentType=text%2fxml");
    }

    // ── Producer params ─────────────────────────────────────────────

    [Fact]
    public void BasicAuth_SetsParams()
    {
        var uri = HttpDsl.Get("api.example.com").BasicAuth(C("user"), C("pass")).Build();
        uri.Should().Contain("authScheme=Basic");
        uri.Should().Contain("username=user");
        uri.Should().Contain("password=pass");
    }

    [Fact]
    public void BearerAuth_SetsScheme()
    {
        var uri = HttpDsl.Get("api.example.com").BearerAuth().Build();
        uri.Should().Contain("authScheme=Bearer");
    }

    [Fact]
    public void NoThrowOnError_SetsParam()
    {
        var uri = HttpDsl.Get("api.example.com").NoThrowOnError().Build();
        uri.Should().Contain("throwOnError=false");
    }

    [Fact]
    public void NoFollowRedirects_SetsParam()
    {
        var uri = HttpDsl.Get("api.example.com").NoFollowRedirects().Build();
        uri.Should().Contain("followRedirects=false");
    }

    [Fact]
    public void MaxRedirects_SetsParam()
    {
        var uri = HttpDsl.Get("api.example.com").MaxRedirects(10).Build();
        uri.Should().Contain("maxRedirects=10");
    }

    [Fact]
    public void PreserveHostHeader_SetsParam()
    {
        var uri = HttpDsl.Get("api.example.com").PreserveHostHeader().Build();
        uri.Should().Contain("preserveHostHeader=true");
    }

    // ── Consumer params ─────────────────────────────────────────────

    [Fact]
    public void Host_SetsParam()
    {
        var uri = HttpDsl.Listen("/api").Host(C("127.0.0.1")).Build();
        uri.Should().Contain("host=127.0.0.1");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = HttpDsl.Listen("/api").Port(9090).Build();
        uri.Should().Contain("port=9090");
    }

    [Fact]
    public void Methods_SetsParam()
    {
        var uri = HttpDsl.Listen("/api").Methods("POST,PUT").Build();
        uri.Should().Contain("methods=POST%2cPUT");
    }

    [Fact]
    public void Cors_SetsParam()
    {
        var uri = HttpDsl.Listen("/api").Cors().Build();
        uri.Should().Contain("cors=true");
    }

    [Fact]
    public void CorsWithOrigins_SetsBothParams()
    {
        var uri = HttpDsl.Listen("/api").Cors("https://example.com").Build();
        uri.Should().Contain("cors=true");
        uri.Should().Contain("corsOrigins=");
    }

    [Fact]
    public void SslCert_SetsParams()
    {
        var uri = HttpDsl.Listen("/api").SslCert(C("cert.pfx"), C("pass")).Build();
        uri.Should().Contain("ssl=true");
        uri.Should().Contain("sslCertPath=cert.pfx");
        uri.Should().Contain("sslCertPassword=pass");
    }

    [Fact]
    public void InOut_SetsParam()
    {
        var uri = HttpDsl.Listen("/api").InOut().Build();
        uri.Should().Contain("inOut=true");
    }

    [Fact]
    public void StreamRequest_SetsParam()
    {
        var uri = HttpDsl.Listen("/api").StreamRequest().Build();
        uri.Should().Contain("streamRequest=true");
    }

    [Fact]
    public void StreamResponse_SetsParam()
    {
        var uri = HttpDsl.Get("api.example.com/data").StreamResponse().Build();
        uri.Should().Contain("streamResponse=true");
    }

    [Fact]
    public void StreamResponse_RoundTrip()
    {
        var original = HttpDsl.Get("api.example.com/data").StreamResponse().Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.RawParameters["streamResponse"].Should().Be("true");
    }

    [Fact]
    public void ResponseCode_SetsParam()
    {
        var uri = HttpDsl.Listen("/api").ResponseCode(202).Build();
        uri.Should().Contain("responseCode=202");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = HttpDsl.Post("api.example.com/data").Timeout(5000);
        uri.Should().StartWith("http:api.example.com/data?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = HttpDsl.Get("api.example.com").BasicAuth(C("u"), C("p"));
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chains ─────────────────────────────────────────────────

    [Fact]
    public void FullProducerChain_BuildsCorrectUri()
    {
        var uri = HttpsDsl.Post("api.example.com/submit")
            .ContentType("application/xml")
            .Timeout(10000)
            .BasicAuth(C("admin"), C("secret"))
            .Build();

        uri.Should().StartWith("https:api.example.com/submit?");
        uri.Should().Contain("method=POST");
        uri.Should().Contain("contentType=application%2fxml");
        uri.Should().Contain("timeout=10000");
        uri.Should().Contain("authScheme=Basic");
    }

    [Fact]
    public void FullConsumerChain_BuildsCorrectUri()
    {
        var uri = HttpDsl.Listen("/webhook")
            .Port(8080)
            .Cors("*")
            .InOut()
            .Build();

        uri.Should().StartWith("http:/webhook?");
        uri.Should().NotContain("method=");
        uri.Should().Contain("port=8080");
        uri.Should().Contain("cors=true");
        uri.Should().Contain("inOut=true");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = HttpDsl.Get("api.example.com/data").Timeout(5000).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("http");
        parsed.Path.Should().Be("api.example.com/data");
        parsed.RawParameters["method"].Should().Be("GET");
        parsed.RawParameters["timeout"].Should().Be("5000");
    }

    // ── Producer Host/Port ──────────────────────────────────────────

    [Fact]
    public void Producer_Host_EmbedsInUrl()
    {
        var uri = HttpDsl.Post("/orders").Host(C("api.example.com")).Build();
        uri.Should().StartWith("http:api.example.com/orders?");
        uri.Should().Contain("method=POST");
        uri.Should().NotContain("host=");
    }

    [Fact]
    public void Producer_HostAndPort_EmbedsInUrl()
    {
        var uri = HttpDsl.Post("/orders").Host(C("api.example.com")).Port(9090).Build();
        uri.Should().StartWith("http:api.example.com:9090/orders?");
        uri.Should().NotContain("host=");
        uri.Should().NotContain("port=");
    }

    [Fact]
    public void Producer_HostOnly_NoPortInUrl()
    {
        var uri = HttpDsl.Get("/status").Host(C("api.example.com")).Build();
        uri.Should().StartWith("http:api.example.com/status?");
        // No port segment between host and path
        uri.Should().NotContain("api.example.com:/");
    }

    [Fact]
    public void Producer_HostWithExpressionPort_EmbedsInUrl()
    {
        var uri = HttpDsl.Post("/orders").Host(C("api.example.com")).Port(C("${header.port}")).Build();
        uri.Should().StartWith("http:api.example.com:${header.port}/orders?");
    }

    [Fact]
    public void Producer_ExpressionHost_EmbedsInUrl()
    {
        var uri = HttpDsl.Put("/api/data").Host(C("${header.targetHost}")).Build();
        uri.Should().StartWith("http:${header.targetHost}/api/data?");
    }

    [Fact]
    public void Producer_ExpressionHostAndPort_EmbedsInUrl()
    {
        var uri = HttpDsl.Post("/api").Host(C("${property.host}")).Port(C("${property.port}")).Build();
        uri.Should().StartWith("http:${property.host}:${property.port}/api?");
    }

    [Fact]
    public void Producer_HostWithPathNoSlash_AddsSlash()
    {
        var uri = HttpDsl.Post("orders").Host(C("api.example.com")).Build();
        uri.Should().StartWith("http:api.example.com/orders?");
    }

    [Fact]
    public void Consumer_HostPort_StillQueryParams()
    {
        var uri = HttpDsl.Listen("/webhook").Host(C("0.0.0.0")).Port(8080).Build();
        uri.Should().StartWith("http:/webhook?");
        uri.Should().Contain("host=0.0.0.0");
        uri.Should().Contain("port=8080");
    }

    [Fact]
    public void Producer_HostPort_RoundTrip()
    {
        var original = HttpDsl.Post("/orders").Host(C("api.example.com")).Port(443).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("http");
        parsed.Path.Should().Be("api.example.com:443/orders");
        parsed.RawParameters["method"].Should().Be("POST");
    }

    [Fact]
    public void Https_Producer_HostPort_EmbedsInUrl()
    {
        var uri = HttpsDsl.Post("/secure").Host(C("api.example.com")).Port(8443).Build();
        uri.Should().StartWith("https:api.example.com:8443/secure?");
    }

    // ── Query Parameters (.Param) ──────────────────────────────────

    [Fact]
    public void Param_ConstantValue_SetsParamInUri()
    {
        var uri = HttpDsl.Post("api.example.com/users")
            .Param("format", "json")
            .Build();
        uri.Should().Contain("param.format=json");
    }

    [Fact]
    public void Param_WithExpression_SetsTemplateString()
    {
        var uri = HttpDsl.Post("api.example.com/users")
            .Param("userId", new HeaderExpression("userId"))
            .Build();
        uri.Should().Contain("param.userId=%24%7bheader.userId%7d");
    }

    [Fact]
    public void Param_WithConstantExpression_SetsValue()
    {
        var uri = HttpDsl.Post("api.example.com/users")
            .Param("limit", C("100"))
            .Build();
        uri.Should().Contain("param.limit=100");
    }

    [Fact]
    public void Param_MultipleParams_AllPresent()
    {
        var uri = HttpDsl.Get("api.example.com/search")
            .Param("q", "test")
            .Param("page", 1)
            .Param("sort", new HeaderExpression("sortField"))
            .Build();
        uri.Should().Contain("param.q=test");
        uri.Should().Contain("param.page=1");
        uri.Should().Contain("param.sort=%24%7bheader.sortField%7d");
    }

    [Fact]
    public void Param_NullValue_SetsEmptyString()
    {
        var uri = HttpDsl.Post("api.example.com/data")
            .Param("key", (object?)null)
            .Build();
        uri.Should().Contain("param.key=");
    }

    [Fact]
    public void Param_Chainable()
    {
        var builder = HttpDsl.Get("api.example.com/data");
        var result = builder.Param("x", "1");
        result.Should().BeSameAs(builder);
    }
}
