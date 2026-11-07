using System.Reflection;

namespace redb.Route.Expressions;

public static partial class ExpressionResolver
{
    /// <summary>
    /// <c>value.method(args)</c> in the AST (findings register row 28): a built-in dot helper of the language
    /// (<c>length</c> / <c>count</c> / <c>size</c>, <c>contains</c>, <c>substring</c>, <c>trim</c>, <c>toUpper</c>,
    /// <c>toLower</c>, <c>replace</c>, <c>startsWith</c>, <c>endsWith</c>, <c>indexOf</c>), else a public method of the
    /// value's type with that many arguments, refused under the template sandbox. A <c>null</c> value gives <c>null</c>,
    /// as reading a member of one does. A method that throws, or one the type does not have, is an error naming the call
    /// and its position: nothing reads as <c>null</c> silently.
    /// </summary>
    internal static object? Ast_CallMethod(object? target, string method, object?[] arguments, string written, int position)
    {
        if (target is null)
            return null;
        var parameters = arguments.Select(a => a!).ToArray();
        try
        {
            switch (method.ToLowerInvariant())
            {
                case "length" or "count" or "size": return ResolveLength(target);
                case "contains": return ResolveContains(target, parameters);
                case "substring": return ResolveSubstring(target, parameters);
                case "trim": return ResolveTrim(target, parameters);
                case "toupper": return ResolveToUpper(target, parameters);
                case "tolower": return ResolveToLower(target, parameters);
                case "replace": return ResolveReplace(target, parameters);
                case "startswith": return ResolveStartsWith(target, parameters);
                case "endswith": return ResolveEndsWith(target, parameters);
                case "indexof": return ResolveIndexOf(target, parameters);
            }
        }
        catch (Exception ex) when (ex is not (ExpressionSandboxViolationException or ExpressionEvaluationException))
        {
            throw Threw(written, position, ex);
        }

        // Anything beyond the helpers runs code of the value's type: refused where an expression may only read members.
        if (ExpressionSandbox.IsActive)
            throw new ExpressionSandboxViolationException(method, target.GetType());

        var type = target.GetType();
        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, method, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            throw new ExpressionEvaluationException(
                $"'{method}' is not a method of {type.Name} at position {position + 1} (in {written}).");
        var chosen = candidates.FirstOrDefault(m => m.GetParameters().Length == arguments.Length)
            ?? throw new ExpressionEvaluationException(
                $"'{method}' of {type.Name} takes {string.Join(" or ", candidates.Select(m => m.GetParameters().Length).Distinct().Order())} " +
                $"argument(s), not {arguments.Length}, at position {position + 1} (in {written}).");
        try
        {
            return chosen.Invoke(target, ConvertArguments(chosen, arguments));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            throw Threw(written, position, inner);
        }
        catch (ArgumentException ex)
        {
            throw Threw(written, position, ex);
        }
    }

    private static ExpressionEvaluationException Threw(string written, int position, Exception ex)
        => new($"{written} at position {position + 1} threw {ex.GetType().Name}: {ex.Message}", ex);

    /// <summary>The arguments converted to the method's parameter types where they differ (an int literal for a long).</summary>
    private static object?[] ConvertArguments(MethodInfo method, object?[] arguments)
    {
        var parameters = method.GetParameters();
        var converted = new object?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            var target = Nullable.GetUnderlyingType(parameters[i].ParameterType) ?? parameters[i].ParameterType;
            converted[i] = arguments[i] is { } value && !target.IsInstanceOfType(value) && value is IConvertible
                           && typeof(IConvertible).IsAssignableFrom(target)
                ? System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture)
                : arguments[i];
        }
        return converted;
    }
}
