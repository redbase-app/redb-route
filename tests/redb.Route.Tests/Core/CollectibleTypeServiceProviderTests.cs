using System.Reflection;
using System.Reflection.Emit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using redb.Route.Core;
using Xunit;

namespace redb.Route.Tests.Core;

/// <summary>
/// The provider beans and components are built with: a service type over a type of a collectible load context never
/// reaches the host's container, which keeps every type it was asked for and would keep the module with it.
/// </summary>
public sealed class CollectibleTypeServiceProviderTests
{
    private static readonly ModuleBuilder Module = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("Collectible" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect)
        .DefineDynamicModule("main");

    private static readonly Type ModuleType = EmitBean("Module.Bean", type => typeof(ILogger<>).MakeGenericType(type), "Logger");

    /// <summary>A settings class of the module: public, with a parameterless constructor, nothing configures it.</summary>
    private static readonly Type SettingsType = EmitSettings();

    private static readonly Type OptionsBeanType = EmitBean("Module.OptionsBean", _ => typeof(IOptions<>).MakeGenericType(SettingsType), "Options");

    private readonly ServiceProvider _host = new ServiceCollection().AddLogging().AddOptions()
        .AddSingleton<Uri>(new Uri("http://host")).BuildServiceProvider();

    [Fact]
    public void A_type_of_the_host_goes_to_the_host()
    {
        var services = CollectibleTypeServiceProvider.For(_host);

        services.GetService(typeof(Uri)).Should().BeSameAs(_host.GetService(typeof(Uri)));
        services.GetService(typeof(ILogger<CollectibleTypeServiceProviderTests>)).Should().BeSameAs(
            _host.GetService(typeof(ILogger<CollectibleTypeServiceProviderTests>)));
        ((IServiceProviderIsService)services).IsService(typeof(Uri)).Should().BeTrue();
    }

    [Fact]
    public void A_logger_of_a_module_type_is_made_from_the_host_logger_factory()
    {
        var loggerType = typeof(ILogger<>).MakeGenericType(ModuleType);
        var services = CollectibleTypeServiceProvider.For(_host);

        services.GetService(loggerType).Should().BeAssignableTo(loggerType);
        ((IServiceProviderIsService)services).IsService(loggerType).Should().BeTrue();
    }

    [Fact]
    public void A_list_of_a_module_type_is_empty_and_any_other_service_over_it_is_absent()
    {
        var services = CollectibleTypeServiceProvider.For(_host);

        ((Array)services.GetService(typeof(IEnumerable<>).MakeGenericType(ModuleType))!).Length.Should().Be(0);
        services.GetService(ModuleType).Should().BeNull();
        services.GetService(typeof(Lazy<>).MakeGenericType(ModuleType)).Should().BeNull();
        ((IServiceProviderIsService)services).IsService(ModuleType).Should().BeFalse();
        ((IKeyedServiceProvider)services).GetKeyedService(ModuleType, "k").Should().BeNull();
    }

    [Fact]
    public void ActivatorUtilities_builds_a_module_bean_with_its_own_logger()
    {
        var bean = ActivatorUtilities.CreateInstance(CollectibleTypeServiceProvider.For(_host), ModuleType);

        bean.GetType().GetField("Logger")!.GetValue(bean).Should().NotBeNull();
    }

    [Fact]
    public void The_options_services_of_a_module_type_hold_its_default_instance_as_the_host_would_build_them()
    {
        var services = CollectibleTypeServiceProvider.For(_host);
        var isService = (IServiceProviderIsService)services;

        foreach (var open in new[] { typeof(IOptions<>), typeof(IOptionsSnapshot<>) })
        {
            var options = services.GetService(open.MakeGenericType(SettingsType));
            options.Should().NotBeNull(open.Name);
            isService.IsService(open.MakeGenericType(SettingsType)).Should().BeTrue(open.Name);
            // IOptionsSnapshot<T> takes Value from IOptions<T>
            typeof(IOptions<>).MakeGenericType(SettingsType).GetProperty("Value")!.GetValue(options).Should().BeOfType(SettingsType);
        }

        var monitor = services.GetService(typeof(IOptionsMonitor<>).MakeGenericType(SettingsType))!;
        monitor.GetType().GetProperty("CurrentValue")!.GetValue(monitor).Should().BeOfType(SettingsType);
        var factory = services.GetService(typeof(IOptionsFactory<>).MakeGenericType(SettingsType))!;
        factory.GetType().GetMethod("Create")!.Invoke(factory, [Options.DefaultName]).Should().BeOfType(SettingsType);
        services.GetService(typeof(IOptionsMonitorCache<>).MakeGenericType(SettingsType)).Should().NotBeNull();
    }

    [Fact]
    public void ActivatorUtilities_builds_a_module_bean_that_takes_its_own_options()
    {
        var bean = ActivatorUtilities.CreateInstance(CollectibleTypeServiceProvider.For(_host), OptionsBeanType);

        bean.GetType().GetField("Options")!.GetValue(bean).Should().NotBeNull();
    }

    [Fact]
    public void A_wrapped_provider_is_not_wrapped_twice()
    {
        var once = CollectibleTypeServiceProvider.For(_host);

        CollectibleTypeServiceProvider.For(once).Should().BeSameAs(once);
    }

    [Fact]
    public void Arrays_and_generics_over_a_module_type_involve_it()
    {
        CollectibleTypeServiceProvider.InvolvesCollectible(ModuleType.MakeArrayType()).Should().BeTrue();
        CollectibleTypeServiceProvider.InvolvesCollectible(typeof(Dictionary<,>).MakeGenericType(typeof(string), ModuleType))
            .Should().BeTrue();
        CollectibleTypeServiceProvider.InvolvesCollectible(typeof(List<string>)).Should().BeFalse();
        CollectibleTypeServiceProvider.InvolvesCollectible(typeof(List<>)).Should().BeFalse();
    }

    private static Type EmitSettings()
    {
        var type = Module.DefineType("Module.Settings", TypeAttributes.Public | TypeAttributes.Sealed);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        return type.CreateType();
    }

    /// <summary>
    /// A public class of the collectible assembly whose constructor takes one service (built from the type itself) and
    /// keeps it in the public field <paramref name="fieldName"/>.
    /// </summary>
    private static Type EmitBean(string name, Func<Type, Type> parameter, string fieldName)
    {
        var type = Module.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed);
        var field = type.DefineField(fieldName, typeof(object), FieldAttributes.Public);
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [parameter(type)]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        return type.CreateType();
    }
}
