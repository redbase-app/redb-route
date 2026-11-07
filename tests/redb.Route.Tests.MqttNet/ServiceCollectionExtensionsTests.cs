using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.MqttNet;
using redb.Route.MqttNet.Connection;

namespace redb.Route.Tests.MqttNet;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteMqtt_RegistersMqttComponent()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteMqtt();

        var sp = services.BuildServiceProvider();
        sp.GetService<MqttComponent>().Should().NotBeNull();
    }

    [Fact]
    public void AddRedbRouteMqtt_RegistersBrokerRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteMqtt();

        var sp = services.BuildServiceProvider();
        sp.GetService<IMqttBrokerRegistry>().Should().NotBeNull();
    }

    [Fact]
    public void AddRedbRouteMqtt_RegistersClientFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteMqtt();

        var sp = services.BuildServiceProvider();
        sp.GetService<IMqttClientFactory>().Should().NotBeNull();
    }

    [Fact]
    public void AddBroker_RegistersBrokerInRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteMqtt(mqtt =>
        {
            mqtt.AddBroker("test", o =>
            {
                o.Server = "mqtt.test.com";
                o.Port = 8883;
            });
        });

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IMqttBrokerRegistry>();
        registry.Contains("test").Should().BeTrue();
        registry.GetOptions("test").Server.Should().Be("mqtt.test.com");
        registry.GetOptions("test").Port.Should().Be(8883);
    }

    [Fact]
    public void AddBroker_WithPrebuiltOptions_RegistersBrokerInRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        var options = new MqttBrokerOptions { Server = "broker.local", Port = 1884 };
        services.AddRedbRouteMqtt(mqtt =>
        {
            mqtt.AddBroker("direct", options);
        });

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IMqttBrokerRegistry>();
        registry.GetOptions("direct").Should().BeSameAs(options);
    }

    [Fact]
    public void AddMultipleBrokers_AllRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteMqtt(mqtt =>
        {
            mqtt.AddBroker("prod", o => o.Server = "prod.mqtt.com");
            mqtt.AddBroker("dev", o => o.Server = "dev.mqtt.com");
            mqtt.AddBroker("staging", o => o.Server = "staging.mqtt.com");
        });

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IMqttBrokerRegistry>();
        registry.Contains("prod").Should().BeTrue();
        registry.Contains("dev").Should().BeTrue();
        registry.Contains("staging").Should().BeTrue();
    }

    [Fact]
    public void UseClientFactory_ReplacesDefault()
    {
        var customFactory = Substitute.For<IMqttClientFactory>();
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        services.AddRedbRouteMqtt(mqtt =>
        {
            mqtt.UseClientFactory(customFactory);
        });

        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IMqttClientFactory>().Should().BeSameAs(customFactory);
    }

    [Fact]
    public void ConfigurationBuilder_Services_ExposesServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());

        IServiceCollection? capturedServices = null;
        services.AddRedbRouteMqtt(mqtt =>
        {
            capturedServices = mqtt.Services;
        });

        capturedServices.Should().BeSameAs(services);
    }
}
