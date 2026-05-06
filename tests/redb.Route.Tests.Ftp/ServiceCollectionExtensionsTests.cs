using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Ftp;

namespace redb.Route.Tests.Ftp;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteFtp_RegistersFtpComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteFtp();

        var descriptors = services.Where(d => d.ServiceType == typeof(FtpComponent)).ToList();
        descriptors.Should().HaveCount(1);
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddRedbRouteFtp_RegistersComponentRegistrar()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteFtp();

        var descriptors = services.Where(d => d.ServiceType.Name.Contains("FtpComponentRegistrar")).ToList();
        descriptors.Should().HaveCount(1);
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddRedbRouteFtp_ReturnsServices()
    {
        var services = new ServiceCollection();
        var result = services.AddRedbRouteFtp();
        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddRedbRouteFtp_ResolvesComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteFtp();

        var routeContext = Substitute.For<IRouteContext>();
        services.AddSingleton(routeContext);

        var provider = services.BuildServiceProvider();
        var component = provider.GetRequiredService<FtpComponent>();

        component.Should().NotBeNull();
        component.Scheme.Should().Be("ftp");
    }

    [Fact]
    public void AddRedbRouteFtp_IdempotentMultipleCalls()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteFtp();
        services.AddRedbRouteFtp();

        var descriptors = services.Where(d => d.ServiceType == typeof(FtpComponent)).ToList();
        descriptors.Should().HaveCountGreaterThanOrEqualTo(1);
    }
}
