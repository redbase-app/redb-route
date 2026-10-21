namespace redb.Route.Abstractions;

/// <summary>
/// Resolves a type name of a route (<c>Namespace.Type, AssemblyName</c> or a bare full name) to a CLR type: the
/// class of <c>bean:</c>, the <c>type</c> of an XML <c>&lt;bean&gt;</c>, the SQL <c>outputClass</c>. Registered as a
/// context service (<see cref="IRouteContext.AddService"/>): hosts that load user code into their own
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> register a resolver that searches that code first, as
/// Apache Camel's ClassResolver does under OSGi. Tsak registers one in the context of every module: the module's
/// assemblies (a package's entry points and companions), then the process. When none is registered,
/// <see cref="Components.Bean.DefaultBeanTypeResolver"/> searches the loaded assemblies of the process, and with two
/// assemblies of one name loaded (a module reloaded beside its previous version) finds the one loaded first.
/// </summary>
public interface IBeanTypeResolver
{
    /// <summary>Returns the resolved type, or null when no loaded assembly carries it.</summary>
    Type? Resolve(string typeName);
}
