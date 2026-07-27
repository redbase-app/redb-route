using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// Basic/Bearer credentials and the TLS certificate password must be able to live in the registry
/// instead of the endpoint URI, so they never reach logs, telemetry, or the dashboard.
/// </summary>
public sealed class HttpConnectionFactoryTests
{
    private const string Secret = "b3arer-T0PSECRET";

    private static HttpComponent Wire(string name, HttpConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new HttpComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesBasicCredentials_WhenUriCarriesNone()
    {
        var component = Wire("billing-api", new HttpConnectionFactory
        {
            AuthScheme = HttpAuthScheme.Basic,
            Username = "svc",
            Password = Secret
        });

        // Validate() requires Username+Password when AuthScheme=Basic — this throws
        // if the factory is not applied first.
        var uri = EndpointUriParser.Parse("http://billing.corp.local/invoices?connectionFactory=billing-api");
        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Secret);
        uri.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void Factory_SuppliesBearerToken()
    {
        var component = Wire("api", new HttpConnectionFactory
        {
            AuthScheme = HttpAuthScheme.Bearer,
            AuthToken = Secret
        });

        var uri = EndpointUriParser.Parse("http://api.corp.local/v1?connectionFactory=api");
        component.CreateEndpoint(uri).Should().NotBeNull();

        uri.ToUriString().Should().NotContain(Secret);
    }

    [Fact]
    public void FactoryAuthToken_SupportsExpressions_LikeTheUriForm()
    {
        // A ${...} token from the factory must become a per-request expression, exactly as
        // BindFromUri does for authToken=${...} — not a literal.
        var component = Wire("api", new HttpConnectionFactory
        {
            AuthScheme = HttpAuthScheme.Bearer,
            AuthToken = "${header.jwt}"
        });

        var uri = EndpointUriParser.Parse("http://api.corp.local/v1?connectionFactory=api");
        var endpoint = (HttpEndpoint)component.CreateEndpoint(uri);

        var token = endpoint.EndpointOptions.AuthToken;
        token.HasValue.Should().BeTrue();
        token!.Value.IsDynamic.Should().BeTrue("a ${...} factory token must resolve per request");
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        var component = Wire("f", new HttpConnectionFactory
        {
            AuthScheme = HttpAuthScheme.Basic,
            Username = "factory-user",
            Password = "factory-pw"
        });

        var uri = EndpointUriParser.Parse(
            "http://api.corp.local/v1?connectionFactory=f&username=uri-user&password=uri-pw");
        component.CreateEndpoint(uri).Should().NotBeNull();

        uri.RawParameters["username"].Should().Be("uri-user");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new HttpComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("http://api.corp.local/v1?connectionFactory=absent");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dsl_EmitsConnectionFactory()
    {
        // fully qualified: the test namespace itself is ...Tests.Http, which shadows the DSL class
        var uri = redb.Route.Http.Http.Get("http://api.corp.local/v1")
            .ConnectionFactory("billing-api").Build();

        uri.Should().Contain("connectionFactory=billing-api");
        uri.Should().NotContain("password=");
    }
}
