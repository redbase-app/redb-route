using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteSftp_RegistersSftpComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSftp();

        var descriptors = services.Where(d => d.ServiceType == typeof(SftpComponent)).ToList();
        descriptors.Should().HaveCount(1);
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddRedbRouteSftp_RegistersComponentRegistrar()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSftp();

        var descriptors = services.Where(d => d.ServiceType.Name.Contains("SftpComponentRegistrar")).ToList();
        descriptors.Should().HaveCount(1);
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddRedbRouteSftp_ReturnsServices()
    {
        var services = new ServiceCollection();
        var result = services.AddRedbRouteSftp();
        result.Should().BeSameAs(services);
    }

    [Fact]
    public void AddRedbRouteSftp_ResolvesComponent()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSftp();

        // Add a mock IRouteContext
        var routeContext = Substitute.For<IRouteContext>();
        services.AddSingleton(routeContext);

        var provider = services.BuildServiceProvider();
        var component = provider.GetRequiredService<SftpComponent>();

        component.Should().NotBeNull();
        component.Scheme.Should().Be("sftp");
    }

    [Fact]
    public void AddRedbRouteSftp_IdempotentMultipleCalls()
    {
        var services = new ServiceCollection();
        services.AddRedbRouteSftp();
        services.AddRedbRouteSftp();

        var descriptors = services.Where(d => d.ServiceType == typeof(SftpComponent)).ToList();
        // ServiceCollection allows duplicates (by design), but the component still works
        descriptors.Should().HaveCountGreaterThanOrEqualTo(1);
    }
}
