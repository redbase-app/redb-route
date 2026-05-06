using redb.Route.Abstractions;
using redb.Route.Abstractions.Typed;
using redb.Route.Definitions;

namespace redb.Route.Extensions;

/// <summary>
/// Extension methods for converting an untyped <see cref="IRouteDefinition"/>
/// to a typed <see cref="IRouteDefinition{TIn}"/>.
/// </summary>
public static class RouteDefinitionTypedExtensions
{
    /// <summary>
    /// Narrows the route definition to a typed view expecting <typeparamref name="TIn"/>
    /// as the message body type. Automatically inserts a <c>ConvertBody&lt;TIn&gt;()</c> step
    /// that deserializes the body using the exchange's ContentType and the registered
    /// <see cref="IDataFormatRegistry"/>. Enables type-safe Filter, Transform, and Process overloads.
    /// </summary>
    /// <typeparam name="TIn">Expected body type.</typeparam>
    /// <param name="definition">Untyped route definition.</param>
    /// <returns>Typed route definition wrapping the original.</returns>
    public static IRouteDefinition<TIn> OfType<TIn>(this IRouteDefinition definition)
    {
        if (definition is RouteDefinition concrete)
        {
            // Skip auto-conversion for primitive/string/byte[] types that don't need deserialization
            if (typeof(TIn) != typeof(string) && typeof(TIn) != typeof(byte[]) && !typeof(TIn).IsPrimitive)
                concrete.ConvertBody<TIn>();

            return new RouteDefinition<TIn>(concrete);
        }

        throw new InvalidOperationException(
            $"OfType<T>() requires a {nameof(RouteDefinition)} instance, got {definition.GetType().Name}.");
    }
}
