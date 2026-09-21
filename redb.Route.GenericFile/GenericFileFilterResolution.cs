using redb.Route.Abstractions;
using redb.Route.Extensions;

namespace redb.Route.GenericFile;

/// <summary>
/// Turns <c>filter=#myFilter</c> into the filter instance the consumer calls. Shared by the
/// file-based components so the reference is spelled and resolved the same way everywhere, and so
/// an unknown name fails at endpoint creation instead of quietly polling everything.
/// </summary>
public static class GenericFileFilterResolution
{
    /// <summary>
    /// Resolves <see cref="GenericFileEndpointOptions.Filter"/> from the route registry into
    /// <see cref="GenericFileEndpointOptions.FilterInstance"/>. Does nothing when no name was given
    /// or when the instance was already supplied in code.
    /// </summary>
    /// <param name="options">Endpoint options carrying the reference.</param>
    /// <param name="context">Route context owning the registry.</param>
    public static void ResolveFilter(this GenericFileEndpointOptions options, IRouteContext? context)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.Filter) || options.FilterInstance is not null)
            return;

        // The leading '#' is Camel's spelling of "this is a registry name"; both forms are accepted
        // so a route ported from Camel keeps working, and a name that is not registered fails loud.
        var name = options.Filter.StartsWith('#') ? options.Filter[1..] : options.Filter;
        options.FilterInstance = context.GetRequiredFromRegistry<IGenericFileFilter>(name);
    }
}
