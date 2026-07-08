using redb.Route.Http;
using HttpMethod = redb.Route.Http.HttpMethod;

namespace redb.Route.Tests.Http;

public class HttpEndpointOptionsTests
{
    [Fact]
    public void CorsCredentials_WithoutOrigins_ThrowsOnValidation()
    {
        var options = new HttpEndpointOptions
        {
            Cors = true,
            CorsCredentials = true
        };

        // The strict Cors-without-origins check fires first, which subsumes the credentials
        // case: any combination of Cors=true with no whitelist and no resolver is rejected.
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Cors=true requires CorsOrigins*");
    }

    [Fact]
    public void CorsCredentials_WithOrigins_PassesValidation()
    {
        var options = new HttpEndpointOptions
        {
            Cors = true,
            CorsCredentials = true,
            CorsOrigins = "https://example.com"
        };

        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var options = new HttpEndpointOptions();

        options.Method.Should().Be(HttpMethod.GET);
        options.Timeout.Should().Be(30_000);
        options.ContentType.Should().Be("application/json");
        options.ThrowOnError.Should().BeTrue();
        options.BridgeHeaders.Should().BeTrue();
        options.AuthScheme.Should().Be(HttpAuthScheme.None);
        options.FollowRedirects.Should().BeTrue();
        options.MaxRedirects.Should().Be(50);
        options.CopyResponseHeaders.Should().BeTrue();
        options.PreserveHostHeader.Should().BeFalse();
        options.Host.Should().Be("0.0.0.0");
        options.Port.Should().Be(8080);
        options.Cors.Should().BeFalse();
        options.MaxRequestBodySize.Should().Be(10 * 1024 * 1024);
        options.Ssl.Should().BeFalse();
        options.ResponseCode.Should().Be(200);
        options.InOut.Should().BeFalse();
        options.StreamRequest.Should().BeFalse();
    }

    [Fact]
    public void Validate_ValidOptions_NoException()
    {
        var options = new HttpEndpointOptions();
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NegativeTimeout_Throws()
    {
        var options = new HttpEndpointOptions { Timeout = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Timeout*");
    }

    [Fact]
    public void Validate_InvalidPort_Negative_Throws()
    {
        var options = new HttpEndpointOptions { Port = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Port*");
    }

    [Fact]
    public void Validate_InvalidPort_TooHigh_Throws()
    {
        var options = new HttpEndpointOptions { Port = 70000 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Port*");
    }

    [Fact]
    public void Validate_NegativeMaxRedirects_Throws()
    {
        var options = new HttpEndpointOptions { MaxRedirects = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxRedirects*");
    }

    [Fact]
    public void Validate_NegativeMaxRequestBodySize_Throws()
    {
        var options = new HttpEndpointOptions { MaxRequestBodySize = -1 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxRequestBodySize*");
    }

    [Fact]
    public void Validate_ResponseCode_TooLow_Throws()
    {
        var options = new HttpEndpointOptions { ResponseCode = 99 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ResponseCode*");
    }

    [Fact]
    public void Validate_ResponseCode_TooHigh_Throws()
    {
        var options = new HttpEndpointOptions { ResponseCode = 600 };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ResponseCode*");
    }

    [Fact]
    public void Validate_BasicAuth_WithoutUsername_Throws()
    {
        var options = new HttpEndpointOptions
        {
            AuthScheme = HttpAuthScheme.Basic,
            Password = "pass"
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Username*");
    }

    [Fact]
    public void Validate_BasicAuth_WithoutPassword_Throws()
    {
        var options = new HttpEndpointOptions
        {
            AuthScheme = HttpAuthScheme.Basic,
            Username = "user"
        };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Password*");
    }

    [Fact]
    public void Validate_BasicAuth_WithCredentials_NoException()
    {
        var options = new HttpEndpointOptions
        {
            AuthScheme = HttpAuthScheme.Basic,
            Username = "user",
            Password = "pass"
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Ssl_WithoutCertPath_Throws()
    {
        var options = new HttpEndpointOptions { Ssl = true };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*SslCertPath*");
    }

    [Fact]
    public void Validate_Ssl_WithCertPath_NoException()
    {
        var options = new HttpEndpointOptions
        {
            Ssl = true,
            SslCertPath = "/path/to/cert.pfx"
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ValidBoundaryValues()
    {
        var options = new HttpEndpointOptions
        {
            Timeout = 0,
            Port = 0,
            MaxRedirects = 0,
            MaxRequestBodySize = 0,
            ResponseCode = 100
        };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ResponseCode_599_NoException()
    {
        var options = new HttpEndpointOptions { ResponseCode = 599 };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Port_65535_NoException()
    {
        var options = new HttpEndpointOptions { Port = 65535 };
        var act = () => options.Validate();
        act.Should().NotThrow();
    }

    // ── ExplicitParameters ──────────────────────────────────────────

    [Fact]
    public void ExplicitParameters_ExtractsParamPrefix()
    {
        var options = new HttpEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["param.userId"] = "123",
            ["param.format"] = "json",
            ["method"] = "POST"
        });

        options.ExplicitParameters.Should().HaveCount(2);
        options.ExplicitParameters["userId"].Should().Be("123");
        options.ExplicitParameters["format"].Should().Be("json");
    }

    [Fact]
    public void ExplicitParameters_Empty_WhenNoParamPrefix()
    {
        var options = new HttpEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["method"] = "POST",
            ["timeout"] = "5000"
        });

        options.ExplicitParameters.Should().BeEmpty();
    }

    [Fact]
    public void ExplicitParameters_PreservesExpressionValues()
    {
        var options = new HttpEndpointOptions();
        options.BindFromUri(new Dictionary<string, string>
        {
            ["param.userId"] = "${header.userId}",
            ["param.status"] = "active"
        });

        options.ExplicitParameters["userId"].Should().Be("${header.userId}");
        options.ExplicitParameters["status"].Should().Be("active");
    }
}
