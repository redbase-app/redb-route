using Microsoft.Extensions.DependencyInjection;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

/// <summary>
/// Acceptance test for the documented registration path (Ф11 волна Б):
/// <c>AddRedbRoute()</c> + <c>AddRedbRouteFirebase()</c> must register the components in the
/// route context through <see cref="IRouteContextConfigurator"/> — the hook
/// <see cref="RouteHostedService"/> actually applies at startup. A lazy marker singleton
/// nobody resolves does NOT count.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddRedbRouteFirebase_HostedStartupPath_RegistersAllSchemes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedbRoute();
        services.AddRedbRouteFirebase();

        await using var sp = services.BuildServiceProvider();
        var context = sp.GetRequiredService<RouteContext>();

        // Exactly what RouteHostedService.StartAsync does before Start():
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        context.HasComponent("fcm").Should().BeTrue(
            "AddRedbRouteFirebase обязан регистрировать компоненты через IRouteContextConfigurator");
        context.HasComponent("fstore").Should().BeTrue();
        context.HasComponent("fbstorage").Should().BeTrue();
    }

    [Fact]
    public async Task AddRedbRouteFirebase_ComponentsShareCredentialProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedbRoute();
        services.AddRedbRouteFirebase();

        await using var sp = services.BuildServiceProvider();
        var context = sp.GetRequiredService<RouteContext>();
        foreach (var configurator in sp.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);

        var provider = sp.GetRequiredService<IFirebaseCredentialProvider>();
        sp.GetRequiredService<FcmComponent>().CredentialProvider.Should().BeSameAs(provider);
        sp.GetRequiredService<FirestoreComponent>().CredentialProvider.Should().BeSameAs(provider);
        sp.GetRequiredService<FirebaseStorageComponent>().CredentialProvider.Should().BeSameAs(provider);
    }
}
