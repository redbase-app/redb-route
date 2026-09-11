namespace redb.Route.Abstractions;

/// <summary>
/// Resolves a resource reference (a schema file, an XSLT stylesheet) to an absolute path.
/// Registered as a context service (<see cref="IRouteContext.AddService"/>); hosts that unpack
/// resources elsewhere (a Tsak package root) register their own instance with the package root
/// as the search base. When none is registered, components fall back to
/// <see cref="Core.RouteResourceResolver.Default"/>, whose last probe is the current working
/// directory — today's behaviour.
/// </summary>
public interface IRouteResourceResolver
{
    /// <summary>Returns an absolute path to an existing file for the reference, or null when not found.</summary>
    string? Resolve(string reference);

    /// <summary>Where <see cref="Resolve"/> looked, for error messages. Defaults to the reference itself.</summary>
    string DescribeSearch(string reference) => reference;
}
