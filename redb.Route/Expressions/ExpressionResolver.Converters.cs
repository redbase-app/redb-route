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

        // Numbers of different types, or a number and a numeric string: equal only when the values are equal
        if (TryNumbersEqual(left, right, out var numbersEqual))
        {
            DebugLog($"Numeric equality comparison: {left} == {right} = {numbersEqual}");
            return numbersEqual;
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
    /// <c>env:NAME</c>, as Apache Camel's <c>${env:NAME}</c>: the variable's value, <c>null</c> when it is not set. Read on
    /// every evaluation, so a changed variable is seen. Refused under the template sandbox: a template's data must not
    /// read the process environment.
    /// </summary>
    internal static object? ReadEnvironment(string name)
    {
        if (ExpressionSandbox.IsActive)
            throw new ExpressionSandboxViolationException("env:" + name);
        return Environment.GetEnvironmentVariable(name);
    }

    /// <summary>
    /// <c>a =~ b</c>, Apache Camel's equals-ignoring-case: two strings compare with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>; any other pair compares exactly as <c>==</c>.
    /// </summary>
    internal static bool EqualsIgnoreCase(object? left, object? right)
        => left is string l && right is string r
            ? string.Equals(l, r, StringComparison.OrdinalIgnoreCase)
            : AreEqual(left, right);

    /// <summary>
    /// <c>value range 'from..to'</c> / <c>value not range 'from..to'</c>: inclusive at both ends. A null value is in no
    /// range; a value that is not a number is an error naming it, in the words an increment uses.
    /// </summary>
    internal static bool InRange(object? value, decimal from, decimal to, bool negated, string written)
    {
        if (value is null)
            return negated;
        decimal number;
        switch (value)
        {
            case double d when !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < (double)decimal.MaxValue:
                number = (decimal)d;
                break;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) < (float)decimal.MaxValue:
                number = (decimal)f;
                break;
            case not (bool or double or float) when TryConvertToDecimal(value, out var converted):
                number = converted;
                break;
            default:
                throw new InvalidOperationException(
                    $"{written}: the value '{value}' ({value.GetType().Name}) is not a number.");
        }
        return (number >= from && number <= to) != negated;
    }

    /// <summary>
    /// Equality of two values of different types that are both numbers (or numeric strings). Without a
    /// <c>double</c> or <c>float</c> on either side both convert to <c>decimal</c> and compare exactly, so a
    /// <c>long</c> beyond double precision keeps its value; with one, both compare as <c>double</c>.
    /// </summary>
    private static bool TryNumbersEqual(object left, object right, out bool equal)
    {
        if (left is not (double or float) && right is not (double or float)
            && TryConvertToDecimal(left, out var leftDecimal) && TryConvertToDecimal(right, out var rightDecimal))
        {
            equal = leftDecimal == rightDecimal;
            return true;
        }

        if (TryConvertToNumber(left, out var leftNumber) && TryConvertToNumber(right, out var rightNumber))
        {
            equal = leftNumber == rightNumber;
            return true;
        }

        equal = false;
        return false;
    }

    /// <summary>The integral, decimal and numeric-string values of <see cref="TryConvertToNumber"/>, as a decimal.</summary>
    private static bool TryConvertToDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l: result = l; return true;
            case decimal d: result = d; return true;
            case string s: return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
            default: result = 0; return false;
        }
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

