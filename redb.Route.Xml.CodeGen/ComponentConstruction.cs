using System.Reflection;
using redb.Route.Abstractions;

namespace redb.Route.Xml.CodeGen;

/// <summary>
/// Creates the components the catalog describes: with a constructor callable without arguments (none at all, or only
/// optional ones, as <c>HttpComponent(string scheme = "http")</c>). A component it cannot create is named on the warning
/// writer, one line each, so a scheme never drops out of the catalog in silence (mcp did, until its constructor without
/// arguments).
/// </summary>
internal static class ComponentConstruction
{
    /// <summary>The components among <paramref name="types"/>, warning on <paramref name="warnings"/> for each one left out.</summary>
    public static IEnumerable<IComponent> Create(IEnumerable<Type> types, TextWriter warnings)
    {
        foreach (var type in types.Where(t => !t.IsAbstract && !t.IsInterface && typeof(IComponent).IsAssignableFrom(t)))
        {
            var constructor = type.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.IsOptional));
            if (constructor is null)
            {
                warnings.WriteLine(
                    $"warning: {type.FullName}: no constructor callable without arguments; its scheme is not in the catalog.");
                continue;
            }

            IComponent component;
            try
            {
                component = (IComponent)constructor.Invoke([.. constructor.GetParameters().Select(p => p.DefaultValue)]);
            }
            catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException)
            {
                warnings.WriteLine(
                    $"warning: {type.FullName}: constructor failed ({ex.InnerException?.Message ?? ex.Message}); " +
                    "its scheme is not in the catalog.");
                continue;
            }
            yield return component;
        }
    }
}
