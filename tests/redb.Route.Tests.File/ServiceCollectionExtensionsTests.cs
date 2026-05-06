using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// Tests for DI extension methods.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteFile_RegistersFileComponent()
    {
        var services = new ServiceCollection();

        services.AddRedbRouteFile();

        var sp = services.BuildServiceProvider();
        var component = sp.GetService<FileComponent>();
        component.Should().NotBeNull();
        component!.Scheme.Should().Be("file");
    }

    [Fact]
    public void AddRedbRouteFile_Singleton()
    {
        var services = new ServiceCollection();

        services.AddRedbRouteFile();

        var sp = services.BuildServiceProvider();
        var c1 = sp.GetService<FileComponent>();
        var c2 = sp.GetService<FileComponent>();
        c1.Should().BeSameAs(c2);
    }
}
