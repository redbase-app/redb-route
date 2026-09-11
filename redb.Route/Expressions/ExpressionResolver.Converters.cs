using System;
using System.Globalization;
using System.Reflection;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver - type conversion and comparison methods
/// </summary>
public static partial class ExpressionResolver
{
    #region Comparison methods

    /// <summary>
    /// Compares two values with the specified operator
    /// </summary>
    private static bool CompareValues(object? left, object? right, string operatorType)
    {
        DebugLog($"Comparing values: '{left}' {operatorType} '{right}'");
        
        var result = operatorType switch
        {
            "==" => AreEqual(left, right),
            "!=" => !AreEqual(left, right),
            ">" => CompareNumeric(left, right) > 0,
            "<" => CompareNumeric(left, right) < 0,
            ">=" => CompareNumeric(left, right) >= 0,
            "<=" => CompareNumeric(left, right) <= 0,
            _ => false
        };
        
        DebugLog($"Comparison result: {result}");
        return result;
    }

    /// <summary>
    /// Checks equality of two values considering their types
    /// </summary>
    private static bool AreEqual(object? left, object? right)
    {
        DebugLog($"Equality check: '{left}' (type: {left?.GetType().Name ?? "null"}) == '{right}' (type: {right?.GetType().Name ?? "null"})");

        // Handling null values
        if (left == null && right == null)
        {
            DebugLog("Both values are null - equal");
            return true;
        }
        
        if (left == null || right == null)
        {
            DebugLog($"One of the values is null: left={left ?? "null"}, right={right ?? "null"} - not equal");
            return false;
        }

        // If both values are of the same type, use standard comparison
        if (left.GetType() == right.GetType())
        {
            var result = left.Equals(right);
            DebugLog($"Direct comparison of same types: {left} == {right} = {result}");
            return result;
        }

        // Try numeric comparison
        if (TryConvertToNumber(left, out double leftNum) && TryConvertToNumber(right, out double rightNum))
        {
            var result = leftNum - rightNum < double.Epsilon;
            DebugLog($"Numeric equality comparison: {leftNum} == {rightNum} = {result}");
            return result;
        }

        // Try boolean comparison — STRICT conversion only. Truthiness must not leak into
        // equality: under the total rule "x" and 42 are both truthy, which would make
        // 42 == 'x' true. Only explicit boolean words, bools and numbers take part here.
        if (TryConvertToBoolStrict(left, out var leftBool) && TryConvertToBoolStrict(right, out var rightBool))
        {
            var result = leftBool == rightBool;
            DebugLog($"Converted boolean comparison: {leftBool} == {rightBool} = {result}");
            return result;
        }

        // If neither numeric nor boolean comparison worked, compare as strings
        if (left is bool || right is bool)
        {
            var result = left.Equals(right);
            DebugLog($"Boolean comparison: {left} == {right} = {result}");
            return result;
        }

        // String comparison
        var leftStr = left.ToString();
        var rightStr = right.ToString();
        var stringResult = string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);
        DebugLog($"String comparison: '{leftStr}' == '{rightStr}' = {stringResult}");
        return stringResult;
    }

    /// <summary>
    /// Compares numeric values
    /// </summary>
    private static int CompareNumeric(object? left, object? right)
    {
        DebugLog($"Numeric comparison: '{left}' vs '{right}'");
        
        if (TryConvertToNumber(left, out double leftNum) && TryConvertToNumber(right, out double rightNum))
        {
            var result = leftNum.CompareTo(rightNum);
            DebugLog($"Numeric comparison: {leftNum} vs {rightNum} = {result}");
            return result;
        }

        // If not numbers, compare as strings
        var leftStr = left?.ToString() ?? string.Empty;
        var rightStr = right?.ToString() ?? string.Empty;
        var stringResult = string.Compare(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);
        DebugLog($"String comparison: '{leftStr}' vs '{rightStr}' = {stringResult}");
        return stringResult;
    }

    #endregion

    #region Type conversion methods

    /// <summary>
    /// Converts a value to a boolean by the single DSL truthiness rule
    /// (<see cref="Predicates.RouteTruthiness"/>). The conversion is total, so this always
    /// returns <c>true</c>; the Try-shape is kept because the word-logic operators and
    /// <c>logical()</c> call it, and their call sites predate the unification.
    /// </summary>
    public static bool TryConvertToBool(object? value, out bool result)
    {
        return _TryConvertToBool(value, out result);
    }

    // Method with _ prefix to break recursion
    private static bool _TryConvertToBool(object? value, out bool result)
    {
        result = Predicates.RouteTruthiness.ToBoolean(value);
        return true;
    }

    /// <summary>
    /// Strict partial boolean conversion for equality coercion: a bool is itself, an explicit
    /// boolean word parses, a number is non-zero. Anything else — an arbitrary string, an
    /// object — refuses to convert, so equality falls through to its string comparison instead
    /// of comparing truthiness.
    /// </summary>
    private static bool TryConvertToBoolStrict(object? value, out bool result)
    {
        switch (value)
        {
            case null:
                result = false;
                return true;
            case bool b:
                result = b;
                return true;
            case string s:
                return Predicates.RouteTruthiness.TryParseBooleanWord(s, out result);
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                result = Convert.ToInt64(value) != 0;
                return true;
            case float f:
                result = f != 0f;
                return true;
            case double d:
                result = d != 0d;
                return true;
            case decimal m:
                result = m != 0m;
                return true;
            default:
                result = false;
                return false;
        }
    }

    /// <summary>
    /// Attempts to convert a value to DateTime
    /// </summary>
    private static bool TryConvertToDateTime(object? value, out DateTime result)
    {
        result = default;
        
        if (value == null) return false;
        
        if (value is DateTime dateTime)
        {
            result = dateTime;
            return true;
        }
        
        if (value is DateTimeOffset dateTimeOffset)
        {
            result = dateTimeOffset.DateTime;
            return true;
        }
        
        if (value is string stringValue)
        {
            return DateTime.TryParse(stringValue, out result);
        }
        
        return false;
    }

    /// <summary>
    /// Attempts to convert a value to a number
    /// </summary>
    private static bool TryConvertToNumber(object? value, out double result)
    {
        result = 0;
        
        if (value == null) return false;
        
        if (value is int intValue)
        {
            result = intValue;
            return true;
        }
        
        if (value is long longValue)
        {
            result = longValue;
            return true;
        }
        
        if (value is double doubleValue)
        {
            result = doubleValue;
            return true;
        }
        
        if (value is decimal decimalValue)
        {
            result = (double)decimalValue;
            return true;
        }
        
        if (value is float floatValue)
        {
            result = floatValue;
            return true;
        }
        
        if (value is string strValue && double.TryParse(strValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedValue))
        {
            result = parsedValue;
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Attempts to convert a value to a double. Public wrapper for AST function implementations.
    /// </summary>
    public static bool TryConvertToDouble(object? value, out double result)
        => TryConvertToNumber(value, out result);

    /// <summary>
    /// Attempts to convert a value to int
    /// </summary>
    private static bool TryConvertToInt(object? value, out int result)
    {
        result = 0;
        
        if (value is int intValue)
        {
            result = intValue;
            return true;
        }
        
        if (value is double doubleValue && doubleValue == Math.Floor(doubleValue))
        {
            result = (int)doubleValue;
            return true;
        }
        
        return int.TryParse(value?.ToString(), out result);
    }

    #endregion

    #region Literal parsing

    /// <summary>
    /// Parses a literal value
    /// </summary>
    public static object? ParseLiteral(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Null literal
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            DebugLog($"Null literal detected: '{value}'");
            return null;
        }

        // String literals
        if ((value.StartsWith("'") && value.EndsWith("'")) ||
            (value.StartsWith("\"") && value.EndsWith("\"")))
        {
            var stringLiteral = value.Substring(1, value.Length - 2);
            DebugLog($"String literal detected: '{value}' -> '{stringLiteral}'");
            return stringLiteral;
        }

        // Numeric literals
        if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var intValue))
        {
            DebugLog($"Integer literal detected: {intValue}");
            return intValue;
        }

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleValue))
        {
            DebugLog($"Floating-point literal detected: {doubleValue}");
            return doubleValue;
        }

        // Boolean literals
        if (bool.TryParse(value, out var boolValue))
        {
            DebugLog($"Boolean literal detected: {boolValue}");
            return boolValue;
        }

        DebugLog($"Value '{value}' returned as string");
        return value;
    }

    #endregion
}

