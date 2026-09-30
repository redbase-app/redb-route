using System.Globalization;

namespace redb.Route.Core;

/// <summary>
/// The conversion behind the typed reads of a header, a property and a received body, as Camel converts: a missing
/// value, or one with no conversion to the type at all, reads as default; a value the conversion exists for but cannot
/// parse is an error naming the key and the type — never the value, which may be a credential. Numbers and dates are
/// read in the invariant culture, and a nullable target converts like its underlying type.
/// </summary>
internal static class TypedValue
{
    /// <summary>Converts <paramref name="value"/>, read under <paramref name="what"/>, to <typeparamref name="T"/>.</summary>
    /// <param name="value">The stored value.</param>
    /// <param name="what">What was read, for the error: <c>header 'orderId'</c>.</param>
    /// <exception cref="FormatException">The value cannot be parsed as <typeparamref name="T"/>.</exception>
    public static T? Convert<T>(object? value, string what)
    {
        if (value is null)
            return default;
        if (value is T typed)
            return typed;

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        // No conversion between the types (not IConvertible, or a pair ChangeType does not map): Camel reads null.
        if (value is not IConvertible || !typeof(IConvertible).IsAssignableFrom(target))
            return default;

        try
        {
            return (T)System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (InvalidCastException)
        {
            return default; // a pair of IConvertible types with no mapping (a char from a DateTime): no conversion
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new FormatException($"The {what} cannot be read as {target.Name}: its value does not parse as one.", ex);
        }
    }
}
