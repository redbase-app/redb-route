using System.Reflection;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// The sides of a component: both, unless its class declares the only one it has with
/// <see cref="EndpointRoleAttribute"/> (Apache Camel's <c>producerOnly</c> / <c>consumerOnly</c>). The context, the
/// Route-XML catalog, the editor schema and <c>check</c> all read this one declaration, and refuse the missing side
/// in the one message <see cref="MissingSide"/> builds.
/// </summary>
public static class EndpointSides
{
    /// <summary>The declaration on <paramref name="componentType"/>; <c>null</c> for both sides.</summary>
    public static EndpointRoleAttribute? Declaration(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        return componentType.GetCustomAttribute<EndpointRoleAttribute>(inherit: true);
    }

    /// <summary>The only side <paramref name="component"/> has; <c>null</c> for both.</summary>
    public static EndpointRole? Of(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return Declaration(component.GetType())?.Role;
    }

    /// <summary>
    /// The refusal of the side <paramref name="scheme"/> does not have, given the only one it has, with the
    /// component's hint when there is one.
    /// </summary>
    public static string MissingSide(string scheme, EndpointRole only, string? hint)
    {
        var text = only == EndpointRole.Producer
            ? $"{scheme} has no consumer side: a route cannot start from it ({scheme} is producer-only)."
            : $"{scheme} has no producer side: a route cannot send to it ({scheme} is consumer-only).";
        return string.IsNullOrWhiteSpace(hint) ? text : text + " " + hint;
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/>, the exception the components throw themselves, when
    /// <paramref name="endpoint"/>'s component has no <paramref name="creating"/> side.
    /// </summary>
    public static void Refuse(IEndpoint endpoint, EndpointRole creating)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Declaration(endpoint.Component.GetType()) is not { } declaration || declaration.Role == creating)
            return;
        throw new NotSupportedException(MissingSide(endpoint.Uri.Scheme, declaration.Role, declaration.Hint));
    }

    /// <summary>The producer of <paramref name="endpoint"/>, once <see cref="Refuse"/> found that it has one.</summary>
    public static IProducer CreateProducerOnItsSide(this IEndpoint endpoint)
    {
        Refuse(endpoint, EndpointRole.Producer);
        return endpoint.CreateProducer();
    }

    /// <summary>The consumer of <paramref name="endpoint"/>, once <see cref="Refuse"/> found that it has one.</summary>
    public static IConsumer CreateConsumerOnItsSide(this IEndpoint endpoint, IProcessor processor)
    {
        Refuse(endpoint, EndpointRole.Consumer);
        return endpoint.CreateConsumer(processor);
    }
}
