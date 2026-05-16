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
    /// Compares two values using the specified operator
    /// </summary>
    private static bool CompareExpressionValues(object? left, object? right, string operatorStr)
    {
        DebugLog($"Comparing values: '{left}' {operatorStr} '{right}'");
        
        if (left == null || right == null)
        {
            // Special handling of null values
            if (operatorStr == "==") return left == right;
            if (operatorStr == "!=") return left != right;
            
            DebugLog("One of the values is null, cannot perform numeric comparison");
            return false;
        }
        
        // Try to convert both values to numeric, if possible
        if (TryConvertToNumber(left, out double leftNum) && TryConvertToNumber(right, out double rightNum))
        {
            DebugLog($"Numeric comparison: {leftNum} {operatorStr} {rightNum}");
            
            switch (operatorStr)
            {
                case "==": return leftNum == rightNum;
                case "!=": return leftNum != rightNum;
                case ">": return leftNum > rightNum;
                case "<": return leftNum < rightNum;
                case ">=": return leftNum >= rightNum;
                case "<=": return leftNum <= rightNum;
            }
        }
        
        // For string and other comparison types
        DebugLog($"String comparison: '{left}' {operatorStr} '{right}'");
        
        switch (operatorStr)
        {
            case "==": return left.Equals(right);
            case "!=": return !left.Equals(right);
            default:
                DebugLog($"Operator {operatorStr} is not supported for non-numeric types");
                return false;
        }
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

        // Try boolean comparison
        if (TryConvertToBool(left, out var leftBool) && TryConvertToBool(right, out var rightBool))
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
    /// Converts a value to bool for expressions - single parameter function for Expression API
    /// </summary>
    private static bool ConvertToBoolExpression(object? value)
    {
        DebugLog($"Converting to bool: '{value}' (type: {value?.GetType().Name ?? "null"})");
        
        if (value == null)
            return false;
        
        if (value is bool boolValue)
            return boolValue;
        
        if (value is string strValue)
        {
            if (bool.TryParse(strValue, out bool parsedBool))
                return parsedBool;
            
            // A string is considered true if it is not empty
            return !string.IsNullOrEmpty(strValue);
        }
        
        if (value is int intValue)
            return intValue != 0;
        
        if (value is long longValue)
            return longValue != 0;
        
        if (value is double doubleValue)
            return doubleValue != 0;
        
        if (value is decimal decimalValue)
            return decimalValue != 0;
        
        // For other types - object exists, so it's true
        return true;
    }

    /// <summary>
    /// Attempts to convert a value to boolean
    /// </summary>
    public static bool TryConvertToBool(object? value, out bool result) 
    {
        return _TryConvertToBool(value, out result);
    }

    // Method with _ prefix to break recursion
    private static bool _TryConvertToBool(object? value, out bool result)
    {
        if (value == null)
        {
            result = false;
            return true;
        }

        switch (value)
        {
            case bool b:
                result = b;
                return true;
                
            case string s:
                return TryParseBoolFromString(s, out result);
                
            case int i:
                result = i != 0;
                return true;
                
            case double d:
                result = Math.Abs(d) > double.Epsilon;
                return true;
                
            case decimal m:
                result = m != 0;
                return true;
                
            case long l:
                result = l != 0;
                return true;
                
            default:
                result = false;
                return false;
        }
    }

    /// <summary>
    /// Parses a boolean value from a string with support for various formats
    /// </summary>
    private static bool TryParseBoolFromString(string s, out bool result)
    {
        result = false;
        if (string.IsNullOrEmpty(s)) return false;
        
        var normalized = s.Trim().ToLowerInvariant();
        
        // Standard boolean values
        if (bool.TryParse(normalized, out result))
        {
            return true;
        }
        
        // Additional formats
        result = normalized switch
        {
            "1" or "yes" or "y" or "on" or "да" or "истина" => true,
            "0" or "no" or "n" or "off" or "нет" or "ложь" => false,
            _ => false
        };
        
        return normalized is "1" or "yes" or "y" or "on" or "да" or "истина" or 
                            "0" or "no" or "n" or "off" or "нет" or "ложь";
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

