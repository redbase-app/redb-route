using Microsoft.Extensions.DependencyInjection;
using redb.Route.Llm.Extensions;

namespace redb.Route.Tests.Llm;

public sealed class LlmServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteLlm_RegistersComponentEngineAndRegistry()
    {
        var services = new ServiceCollection();
        var ctx = new RouteContext();
        services.AddSingleton<IRouteContext>(ctx);
        services.AddRedbRouteLlm();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IAgentEngine>().Should().BeOfType<AgentEngine>();
        sp.GetRequiredService<IToolDescriptorRegistry>().Should().BeOfType<ToolDescriptorRegistry>();

        // Trigger registrar so it pushes the component into the route context.
        sp.GetRequiredService<ILlmComponentRegistrar>();

        ctx.GetComponent<LlmComponent>("llm").Should().NotBeNull();
    }

    [Fact]
    public void AddLlmConnectionFactory_RegistersFactoryByName()
    {
        var services = new ServiceCollection();
        var ctx = new RouteContext();
        services.AddSingleton<IRouteContext>(ctx);
        services.AddRedbRouteLlm();
        services.AddLlmConnectionFactory("claude", f =>
        {
            f.Provider = "stub";
            f.ModelId = "haiku";
        });

        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ILlmFactoryRegistrar>();

        var factory = ctx.GetFromRegistry<LlmConnectionFactory>("claude");
        factory.Should().NotBeNull();
        factory!.Name.Should().Be("claude");
        factory.Provider.Should().Be("stub");
        factory.ModelId.Should().Be("haiku");
    }

    [Fact]
    public void AddLlmConnectionFactory_NameWinsOverConfigureOverride()
    {
        var services = new ServiceCollection();
        var ctx = new RouteContext();
        services.AddSingleton<IRouteContext>(ctx);
        services.AddRedbRouteLlm();
        services.AddLlmConnectionFactory("registered-name", f =>
        {
            f.Name = "evil-rename"; // user attempted override
            f.Provider = "stub";
        });

        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ILlmFactoryRegistrar>();

        var factory = ctx.GetFromRegistry<LlmConnectionFactory>("registered-name");
        factory.Should().NotBeNull();
        factory!.Name.Should().Be("registered-name");

        ctx.GetFromRegistry<LlmConnectionFactory>("evil-rename").Should().BeNull();
    }

    [Fact]
    public void AddLlmConnectionFactory_NullName_Throws()
    {
        var services = new ServiceCollection();
        var act = () => services.AddLlmConnectionFactory(null!, _ => { });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddLlmConnectionFactory_NullConfigure_Throws()
    {
        var services = new ServiceCollection();
        var act = () => services.AddLlmConnectionFactory("c", null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
