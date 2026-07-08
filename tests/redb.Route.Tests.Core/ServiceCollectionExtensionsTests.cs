using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.RedbCore;
using redb.Route.RedbCore.Repositories;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for <see cref="ServiceCollectionExtensions.AddRedbRouteCore"/>.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteCore_NoConfigure_DoesNotThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddRedbRouteCore();

        act.Should().NotThrow();
    }

    [Fact]
    public void UseIdempotentRepository_RegistersOptions()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Substitute.For<IRedbService>());

        services.AddRedbRouteCore(core =>
        {
            core.UseIdempotentRepository(o =>
            {
                o.ProcessorName = "my-route";
                o.Ttl = TimeSpan.FromDays(7);
            });
        });

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<RedbIdempotentOptions>();

        options.ProcessorName.Should().Be("my-route");
        options.Ttl.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public void UseIdempotentRepository_RegistersIIdempotentRepository()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Substitute.For<IRedbService>());

        services.AddRedbRouteCore(core =>
        {
            core.UseIdempotentRepository(o => o.ProcessorName = "test");
        });

        var sp = services.BuildServiceProvider();
        var repo = sp.GetRequiredService<IIdempotentRepository>();

        repo.Should().BeOfType<RedbIdempotentRepository>();
    }

    [Fact]
    public void UseIdempotentRepository_DefaultOptions()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Substitute.For<IRedbService>());

        services.AddRedbRouteCore(core =>
        {
            core.UseIdempotentRepository();
        });

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<RedbIdempotentOptions>();

        options.ProcessorName.Should().BeEmpty();
        options.Ttl.Should().BeNull();
    }

    [Fact]
    public void AddRedbRouteCore_Builder_ExposesServices()
    {
        var services = new ServiceCollection();

        RedbCoreConfigurationBuilder? capturedBuilder = null;
        services.AddRedbRouteCore(core =>
        {
            capturedBuilder = core;
        });

        capturedBuilder.Should().NotBeNull();
        capturedBuilder!.Services.Should().BeSameAs(services);
    }
}
