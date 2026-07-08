using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteHttp_RegistersHttpComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteHttp();
        var sp = services.BuildServiceProvider();

        var component = sp.GetService<HttpComponent>();
        component.Should().NotBeNull();
        component!.Scheme.Should().Be("http");
    }

    [Fact]
    public void AddRedbRouteHttp_RegistersHttpsComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteHttp();
        var sp = services.BuildServiceProvider();

        var component = sp.GetService<HttpsComponent>();
        component.Should().NotBeNull();
        component!.Scheme.Should().Be("https");
    }

    [Fact]
    public void AddRedbRouteHttp_ComponentsAreSingleton()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteHttp();
        var sp = services.BuildServiceProvider();

        var first = sp.GetService<HttpComponent>();
        var second = sp.GetService<HttpComponent>();
        first.Should().BeSameAs(second);

        var firstHttps = sp.GetService<HttpsComponent>();
        var secondHttps = sp.GetService<HttpsComponent>();
        firstHttps.Should().BeSameAs(secondHttps);
    }

    [Fact]
    public void AddRedbRouteHttp_WithCorsConfig_AppliedToComponents()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteHttp(cors =>
        {
            cors.Enabled = true;
            cors.Origins = "https://app.example.com";
            cors.Credentials = true;
        });
        var sp = services.BuildServiceProvider();

        var http = sp.GetRequiredService<HttpComponent>();
        http.DefaultCors.Enabled.Should().BeTrue();
        http.DefaultCors.Origins.Should().Be("https://app.example.com");
        http.DefaultCors.Credentials.Should().BeTrue();

        var https = sp.GetRequiredService<HttpsComponent>();
        https.DefaultCors.Enabled.Should().BeTrue();
        https.DefaultCors.Origins.Should().Be("https://app.example.com");
        https.DefaultCors.Credentials.Should().BeTrue();
    }

    [Fact]
    public void AddRedbRouteHttp_WithoutCorsConfig_DefaultsApplied()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteHttp();
        var sp = services.BuildServiceProvider();

        var http = sp.GetRequiredService<HttpComponent>();
        http.DefaultCors.Enabled.Should().BeFalse();
        http.DefaultCors.Origins.Should().BeNull();
        http.DefaultCors.Credentials.Should().BeFalse();
    }

    [Fact]
    public void AddRedbRouteHttp_RegistersComponentsInRouteContext()
    {
        var routeContext = Substitute.For<IRouteContext>();

        var services = new ServiceCollection();
        services.AddSingleton(routeContext);
        services.AddRedbRouteHttp();

        var sp = services.BuildServiceProvider();

        // Resolve the registrar to trigger component registration
        var registrar = sp.GetService<IHttpComponentRegistrar>();
        registrar.Should().NotBeNull();

        // Verify AddComponent was called for both http and https
        routeContext.Received(1).AddComponent(Arg.Is<HttpComponent>(c => c.Scheme == "http"));
        routeContext.Received(1).AddComponent(Arg.Is<HttpsComponent>(c => c.Scheme == "https"));
    }
}
