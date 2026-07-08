using System;
using System.Linq;
using System.Reflection;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver - string manipulation methods
/// </summary>
public static partial class ExpressionResolver
{
    /// <summary>
    /// Resolves the contains method for strings and collections
    /// </summary>
    private static object? ResolveContains(object current, object[] parameters)
    {
        DebugLog($"ResolveContains: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (parameters.Length != 1)
        {
            DebugLog($"ResolveContains: invalid parameter count: {parameters.Length}");
            return null;
        }

        var searchValue = parameters[0];
        DebugLog($"ResolveContains: searching for value '{searchValue}' in object of type {current?.GetType().Name}");

        // For strings
        if (current is string str)
        {
            var searchStr = searchValue?.ToString() ?? "";
            var result = str.Contains(searchStr);
            DebugLog($"ResolveContains: string '{str}' contains '{searchStr}' = {result}");
            return result;
        }

        // For collections
        if (current is System.Collections.IEnumerable enumerable && current is not string)
        {
            foreach (var item in enumerable)
            {
                if (AreEqual(item, searchValue))
                {
                    DebugLog($"ResolveContains: collection contains element '{searchValue}' = true");
                    return true;
                }
            }
            DebugLog($"ResolveContains: collection does NOT contain element '{searchValue}' = false");
            return false;
        }

        DebugLog($"ResolveContains: unsupported type {current?.GetType().Name}");
        return null;
    }

    /// <summary>
    /// Resolves the substring method for strings
    /// </summary>
    private static object? ResolveSubstring(object current, object[] parameters)
    {
        DebugLog($"ResolveSubstring: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (current is not string str)
        {
            DebugLog($"ResolveSubstring: object is not a string: {current?.GetType().Name}");
            return null;
        }

        if (parameters.Length == 0 || parameters.Length > 2)
        {
            DebugLog($"ResolveSubstring: invalid parameter count: {parameters.Length}");
            return null;
        }

        // Get startIndex
        if (!TryConvertToInt(parameters[0], out int startIndex))
        {
            DebugLog($"ResolveSubstring: failed to convert startIndex: {parameters[0]}");
            return null;
        }

        DebugLog($"ResolveSubstring: string='{str}', startIndex={startIndex}");

        // Check bounds
        if (startIndex < 0 || startIndex >= str.Length)
        {
            DebugLog($"ResolveSubstring: startIndex {startIndex} is out of bounds for string of length {str.Length}");
            return "";
        }

        // One parameter - substring(startIndex)
        if (parameters.Length == 1)
        {
            var result = str.Substring(startIndex);
            DebugLog($"ResolveSubstring: substring({startIndex}) = '{result}'");
            return result;
        }

        // Two parameters - substring(startIndex, length)
        if (!TryConvertToInt(parameters[1], out int length))
        {
            DebugLog($"ResolveSubstring: failed to convert length: {parameters[1]}");
            return null;
        }

        DebugLog($"ResolveSubstring: string='{str}', startIndex={startIndex}, length={length}");

        // Check bounds for length
        if (length < 0)
        {
            DebugLog($"ResolveSubstring: negative length: {length}");
            return "";
        }

        if (startIndex + length > str.Length)
        {
            length = str.Length - startIndex;
            DebugLog($"ResolveSubstring: adjusted length to {length}");
        }

        var result2 = str.Substring(startIndex, length);
        DebugLog($"ResolveSubstring: substring({startIndex}, {length}) = '{result2}'");
        return result2;
    }

    /// <summary>
    /// Resolves the trim method for strings
    /// </summary>
    private static object? ResolveTrim(object current, object[] parameters)
    {
        DebugLog($"ResolveTrim: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (current is not string str)
        {
            DebugLog($"ResolveTrim: object is not a string: {current?.GetType().Name}");
            return null;
        }

        // No parameters - regular trim
        if (parameters.Length == 0)
        {
            var result = str.Trim();
            DebugLog($"ResolveTrim: trim() = '{result}'");
            return result;
        }

        // With one parameter - trim specific characters
        if (parameters.Length == 1)
        {
            var trimChars = parameters[0]?.ToString();
            if (string.IsNullOrEmpty(trimChars))
            {
                var result = str.Trim();
                DebugLog($"ResolveTrim: trim(empty chars) = '{result}'");
                return result;
            }

            var result2 = str.Trim(trimChars.ToCharArray());
            DebugLog($"ResolveTrim: trim('{trimChars}') = '{result2}'");
            return result2;
        }

        DebugLog($"ResolveTrim: invalid parameter count: {parameters.Length}");
        return null;
    }

    /// <summary>
    /// Resolves the toupper method for strings
    /// </summary>
    private static object? ResolveToUpper(object current, object[] parameters)
    {
        DebugLog($"ResolveToUpper: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (current is not string str)
        {
            DebugLog($"ResolveToUpper: object is not a string: {current?.GetType().Name}");
            return null;
        }

        var result = str.ToUpper();
        DebugLog($"ResolveToUpper: toupper() = '{result}'");
        return result;
    }

    /// <summary>
    /// Resolves the tolower method for strings
    /// </summary>
    private static object? ResolveToLower(object current, object[] parameters)
    {
        DebugLog($"ResolveToLower: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (current is not string str)
        {
            DebugLog($"ResolveToLower: object is not a string: {current?.GetType().Name}");
            return null;
        }

        var result = str.ToLower();
        DebugLog($"ResolveToLower: tolower() = '{result}'");
        return result;
    }

    /// <summary>
    /// Resolves the replace method for strings
    /// </summary>
    private static object? ResolveReplace(object current, object[] parameters)
    {
        DebugLog($"ResolveReplace: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (current is not string str)
        {
            DebugLog($"ResolveReplace: object is not a string: {current?.GetType().Name}");
            return null;
        }

        if (parameters.Length != 2)
        {
            DebugLog($"ResolveReplace: invalid parameter count: {parameters.Length}");
            return null;
        }

        var searchValue = parameters[0]?.ToString();
        var replaceValue = parameters[1]?.ToString();

        if (string.IsNullOrEmpty(searchValue))
        {
            DebugLog($"ResolveReplace: empty search pattern");
            return str;
        }

        var result = str.Replace(searchValue, replaceValue);
        DebugLog($"ResolveReplace: replace('{searchValue}', '{replaceValue}') = '{result}'");
        return result;
    }

    /// <summary>
    /// Resolves the startswith method for strings
    /// </summary>
    private static object? ResolveStartsWith(object current, object[] parameters)
    {
        DebugLog($"ResolveStartsWith: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (current is not string str)
        {
            DebugLog($"ResolveStartsWith: object is not a string: {current?.GetType().Name}");
            return null;
        }

        if (parameters.Length != 1)
        {
            DebugLog($"ResolveStartsWith: invalid parameter count: {parameters.Length}");
            return null;
        }

        var searchValue = parameters[0]?.ToString();

        if (string.IsNullOrEmpty(searchValue))
        {
            DebugLog($"ResolveStartsWith: empty search pattern");
            return true;
        }

        var result = str.StartsWith(searchValue);
        DebugLog($"ResolveStartsWith: startswith('{searchValue}') = {result}");
        return result;
    }

    /// <summary>
    /// Resolves the endswith method for strings
    /// </summary>
    private static object? ResolveEndsWith(object current, object[] parameters)
    {
        DebugLog($"ResolveEndsWith: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (current is not string str)
        {
            DebugLog($"ResolveEndsWith: object is not a string: {current?.GetType().Name}");
            return null;
        }

        if (parameters.Length != 1)
        {
            DebugLog($"ResolveEndsWith: invalid parameter count: {parameters.Length}");
            return null;
        }

        var searchValue = parameters[0]?.ToString();

        if (string.IsNullOrEmpty(searchValue))
        {
            DebugLog($"ResolveEndsWith: empty search pattern");
            return true;
        }

        var result = str.EndsWith(searchValue);
        DebugLog($"ResolveEndsWith: endswith('{searchValue}') = {result}");
        return result;
    }

    /// <summary>
    /// Resolves the indexof method for strings
    /// </summary>
    private static object? ResolveIndexOf(object current, object[] parameters)
    {
        DebugLog($"ResolveIndexOf: object={current?.GetType().Name ?? "null"}, parameters={parameters.Length}");
        
        if (current is not string str)
        {
            DebugLog($"ResolveIndexOf: object is not a string: {current?.GetType().Name}");
            return null;
        }

        if (parameters.Length != 1)
        {
            DebugLog($"ResolveIndexOf: invalid parameter count: {parameters.Length}");
            return null;
        }

        var searchValue = parameters[0]?.ToString();

        if (string.IsNullOrEmpty(searchValue))
        {
            DebugLog($"ResolveIndexOf: empty search pattern");
            return null;
        }

        var result = str.IndexOf(searchValue);
        DebugLog($"ResolveIndexOf: indexof('{searchValue}') = {result}");
        return result;
    }

    /// <summary>
    /// Resolves a method via reflection
    /// </summary>
    private static object? ResolveMethodByReflection(object current, string methodName, object[] parameters)
    {
        DebugLog($"Searching for method '{methodName}' via reflection");
        
        var type = current.GetType();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        
        if (methods.Length == 0)
        {
            DebugLog($"Method '{methodName}' not found in type {type.Name}");
            return null;
        }
        
        // Find a matching method by parameter count
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == parameters.Length);
        if (method == null)
        {
            DebugLog($"Method '{methodName}' with {parameters.Length} parameters not found");
            return null;
        }
        
        try
        {
            var result = method.Invoke(current, parameters);
            DebugLog($"Method '{methodName}' invocation successful: {result}");
            return result;
        }
        catch (Exception ex)
        {
            DebugLog($"Error invoking method '{methodName}': {ex.Message}");
            return null;
        }
    }
}

