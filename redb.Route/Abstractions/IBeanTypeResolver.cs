namespace redb.Route.Abstractions;

/// <summary>
/// Resolves a <c>bean:</c> type name (<c>Namespace.Type, AssemblyName</c> or a bare full name) to
/// a CLR type. Registered as a context service (<see cref="IRouteContext.AddService"/>): hosts
/// that load user code into their own <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// (a Tsak package) register a resolver that searches the package's assemblies first. When none
/// is registered, <see cref="Components.Bean.DefaultBeanTypeResolver"/> searches the loaded
/// assemblies of the process.
/// </summary>
public interface IBeanTypeResolver
{
    /// <summary>Returns the resolved type, or null when no loaded assembly carries it.</summary>
    Type? Resolve(string typeName);
}
