// The test emits its module with PersistedAssemblyBuilder, which .NET 9 introduced.
#if NET9_0_OR_GREATER
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using redb.Route.Core;
using redb.Route.Serialization;

namespace redb.Route.Tests.Core;

/// <summary>
/// A type of a collectible load context that went through the JSON serializer of redb.Route no longer keeps the
/// context alive: on unload the hot reload caches of the .NET libraries (System.Text.Json's above all) are emptied.
/// Without that, System.Text.Json roots the type for good (https://github.com/dotnet/runtime/issues/65323).
/// </summary>
[Collection(CollectibleContextCollection.Name)]
public sealed class CollectibleContextCleanupTests
{
    // A context with no assembly: clearing "for" it leaves out no library.
    private static readonly AssemblyLoadContext NoAssemblies = new("no-assemblies", isCollectible: true);

    [Fact]
    public async Task A_module_type_serialized_as_json_lets_its_context_unload()
    {
        var module = SerializeFromCollectibleModule(keepCleanup: true);

        await Collect(module);

        module.IsAlive.Should().BeFalse("the System.Text.Json caches are emptied when the context unloads");
    }

    [Fact]
    public async Task Without_the_cleanup_the_json_caches_keep_the_context_alive()
    {
        var module = SerializeFromCollectibleModule(keepCleanup: false);

        await Collect(module);

        module.IsAlive.Should().BeTrue("the control: System.Text.Json roots a type it serialized");
        // Let the context go for the tests that follow.
        CollectibleContextCleanup.ClearMetadataUpdateCaches(NoAssemblies);
    }

    [Fact]
    public void The_handlers_of_the_loaded_libraries_are_found()
    {
        CollectibleContextCleanup.ClearMetadataUpdateCaches(NoAssemblies)
            .Should().BeGreaterThan(0, "System.Text.Json declares a hot reload handler");
    }

    /// <summary>
    /// Loads an emitted module with one POCO into a collectible context, serializes and deserializes an instance
    /// through <see cref="JsonMessageSerializer"/> and unloads the context. Out of line so nothing of the caller roots it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SerializeFromCollectibleModule(bool keepCleanup)
    {
        new RouteContext().Dispose(); // the cleanup starts with the first route context
        var context = new AssemblyLoadContext($"module-{Guid.NewGuid():N}", isCollectible: true);
        var type = context.LoadFromStream(new MemoryStream(EmitModuleWithPoco())).GetType("Module.Order")!;
        CollectibleContextCleanup.IsWatched(context).Should().BeTrue("a collectible context is watched once its assembly loads");

        var order = Activator.CreateInstance(type)!;
        type.GetProperty("Name")!.SetValue(order, "first");
        var serializer = new JsonMessageSerializer();
        var json = serializer.Serialize<object>(order);
        serializer.Deserialize(json, type).Should().NotBeNull();

        if (!keepCleanup)
        {
            var onUnloading = typeof(CollectibleContextCleanup).GetMethod("OnUnloading", BindingFlags.NonPublic | BindingFlags.Static)!;
            context.Unloading -= (Action<AssemblyLoadContext>)Delegate.CreateDelegate(typeof(Action<AssemblyLoadContext>), onUnloading);
        }

        context.Unload();
        return new WeakReference(context);
    }

    /// <summary>An assembly with one public class, <c>Module.Order</c>, with a string property <c>Name</c>.</summary>
    private static byte[] EmitModuleWithPoco()
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName("Module"), typeof(object).Assembly);
        var type = builder.DefineDynamicModule("Module").DefineType("Module.Order", TypeAttributes.Public | TypeAttributes.Class);
        var field = type.DefineField("_name", typeof(string), FieldAttributes.Private);
        var property = type.DefineProperty("Name", PropertyAttributes.None, typeof(string), null);
        const MethodAttributes accessor = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

        var get = type.DefineMethod("get_Name", accessor, typeof(string), Type.EmptyTypes);
        var il = get.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        property.SetGetMethod(get);

        var set = type.DefineMethod("set_Name", accessor, null, [typeof(string)]);
        il = set.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        property.SetSetMethod(set);

        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.CreateType();
        using var stream = new MemoryStream();
        builder.Save(stream);
        return stream.ToArray();
    }

    private static async Task Collect(WeakReference reference)
    {
        for (var i = 0; i < 100 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (reference.IsAlive)
                await Task.Delay(10);
        }
    }
}

/// <summary>Unload tests share the process-wide caches they empty: they run one after the other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CollectibleContextCollection
{
    public const string Name = "CollectibleContext";
}
#endif
