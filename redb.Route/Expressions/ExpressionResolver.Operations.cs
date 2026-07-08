using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver - operations (unary, binary, increment/decrement, JSONPath)
/// </summary>
public static partial class ExpressionResolver
{
    #region Types and helper methods for working with properties

    /// <summary>
    /// Property location type
    /// </summary>
    private enum PropertyLocation
    {
        Property,  // exchange.Properties
        Header,    // exchange.In.Headers
        Body,      // exchange.In.body
        Direct     // direct access to exchange.Properties (without prefix)
    }

    /// <summary>
    /// Increment/decrement operation type
    /// </summary>
    private enum OperationType
    {
        PrefixIncrement,   // ++x - returns the new value
        PrefixDecrement,   // --x - returns the new value
        PostfixIncrement,  // x++ - returns the old value
        PostfixDecrement   // x-- - returns the old value
    }

    /// <summary>
    /// Determines the type and actual name of the property
    /// </summary>
    private static (PropertyLocation location, string actualName) ParsePropertyLocation(string propertyName)
    {
        if (propertyName.StartsWith(PROPERTY_PREFIX))
            return (PropertyLocation.Property, propertyName.Substring(PROPERTY_PREFIX.Length));
        
        if (propertyName.StartsWith(HEADER_PREFIX))
            return (PropertyLocation.Header, propertyName.Substring(HEADER_PREFIX.Length));
        
        if (propertyName.StartsWith(BODY_PREFIX) || propertyName == "body")
            return (PropertyLocation.Body, propertyName);
        
        return (PropertyLocation.Direct, propertyName);
    }

    /// <summary>
    /// Gets the property value from exchange depending on its location
    /// </summary>
    private static object? GetPropertyValue(IExchange exchange, PropertyLocation location, string name)
    {
        return location switch
        {
            PropertyLocation.Property => exchange.getProperty<object>(name),
            PropertyLocation.Header => exchange.In.getHeader<object>(name),
            PropertyLocation.Body => exchange.In.getBody<object>(),
            PropertyLocation.Direct => exchange.getProperty<object>(name),
            _ => null
        };
    }

    /// <summary>
    /// Sets the property value in exchange depending on its location
    /// </summary>
    private static void SetPropertyValue(IExchange exchange, PropertyLocation location, string name, object? value)
    {
        switch (location)
        {
            case PropertyLocation.Property:
                exchange.setProperty(name, value);
                break;
            case PropertyLocation.Header:
                exchange.In.setHeader(name, value);
                break;
            case PropertyLocation.Direct:
                exchange.setProperty(name, value);
                break;
            // Body does not support setting values
        }
    }

    /// <summary>
    /// Unified method for increment and decrement operations
    /// </summary>
    private static object? ApplyIncrementDecrementOperation(
        IExchange exchange,
        string propertyName,
        OperationType operation)
    {
        DebugLog($"Applying operation {operation} to property: '{propertyName}'");
        
        // Determine the type and name of the property
        var (location, actualName) = ParsePropertyLocation(propertyName);
        
        // Body does not support increment/decrement
        if (location == PropertyLocation.Body)
        {
            DebugLog($"Body prefix detected. Increment/decrement operations are not supported for message body.");
            return exchange.In.getBody<object>();
        }
        
        // Get the current value
        var currentValue = GetPropertyValue(exchange, location, actualName);
        
        // If value is null, initialize with zero
        if (currentValue == null)
        {
            DebugLog($"Property '{actualName}' not found, initializing with value 0");
            currentValue = 0;
        }
        
        // Convert to number
        if (!TryConvertToNumber(currentValue, out double numValue))
        {
            DebugLog($"Value '{currentValue}' is not a number, attempting conversion");
            if (int.TryParse(currentValue?.ToString(), out int parsedInt))
                numValue = parsedInt;
            else
                numValue = 0;
        }
        
        // Determine the new value and return value depending on the operation
        var (newValue, returnValue) = operation switch
        {
            OperationType.PrefixIncrement => (numValue + 1, numValue + 1),
            OperationType.PrefixDecrement => (numValue - 1, numValue - 1),
            OperationType.PostfixIncrement => (numValue + 1, numValue),
            OperationType.PostfixDecrement => (numValue - 1, numValue),
            _ => (numValue, numValue)
        };
        
        DebugLog($"Operation {operation}: current={numValue}, new={newValue}, returning={returnValue}");
        
        // Convert to int if possible
        var valueToSet = newValue == Math.Floor(newValue) ? (object)(int)newValue : newValue;
        var valueToReturn = returnValue == Math.Floor(returnValue) ? (object)(int)returnValue : returnValue;
        
        // Update the value in exchange
        SetPropertyValue(exchange, location, actualName, valueToSet);
        
        // Verify the update for debugging
        if (_debugEnabled)
        {
            var updatedValue = GetPropertyValue(exchange, location, actualName);
            DebugLog($"Update verification: new value in {location}: {updatedValue}");
        }
        
        // Return the corresponding value (old for postfix, new for prefix)
        return valueToReturn;
    }

    #endregion

    #region Unary operations

    /// <summary>
    /// Applies unary logical negation to a value
    /// </summary>
    private static object? ApplyUnaryNot(object? value)
    {
        DebugLog($"Applying unary logical negation to: {value}");
        if (value == null) return true; // null is treated as false, negation is true
        
        // Try to convert to bool
        if (TryConvertToBool(value, out bool boolValue))
        {
            DebugLog($"Value converted to bool: {boolValue}, negation: {!boolValue}");
            return !boolValue;
        }
        
        // If conversion to bool failed, return the original value
        DebugLog($"Failed to convert to bool, returning original value");
        return value;
    }

    /// <summary>
    /// Applies unary plus to numbers or performs concatenation for strings and collections
    /// </summary>
    private static object? ApplyUnaryPlus(object? value)
    {
        DebugLog($"Applying unary plus to: {value}");
        if (value == null) return null;
        
        // For numeric types - return the number itself
        if (TryConvertToNumber(value, out double num))
        {
            return num;
        }
        
        // For strings - return the string as-is
        if (value is string str)
        {
            return str;
        }
        
        // For collections - return the collection as-is
        if (value is System.Collections.IEnumerable)
        {
            return value;
        }
        
        // By default return the value as-is
        return value;
    }
    
    /// <summary>
    /// Applies unary minus to numbers
    /// </summary>
    private static object? ApplyUnaryMinus(object? value)
    {
        DebugLog($"Applying unary minus to: {value}");
        if (value == null) return null;
        
        // For numeric types - negate the sign
        if (TryConvertToNumber(value, out double num))
        {
            return -num;
        }
        
        // For other types return as-is
        return value;
    }

    #endregion

    #region Prefix increment/decrement operations

    /// <summary>
    /// Applies prefix increment (++x) for numeric values and updates the value in exchange
    /// </summary>
    private static object? ApplyPrefixIncrement(IExchange exchange, string propertyName)
        => ApplyIncrementDecrementOperation(exchange, propertyName, OperationType.PrefixIncrement);
    
    /// <summary>
    /// Applies prefix decrement (--x) for numeric values and updates the value in exchange
    /// </summary>
    private static object? ApplyPrefixDecrement(IExchange exchange, string propertyName)
        => ApplyIncrementDecrementOperation(exchange, propertyName, OperationType.PrefixDecrement);

    #endregion

    #region Postfix increment/decrement operations

    /// <summary>
    /// Applies postfix increment (x++) for numeric values and updates the value in exchange
    /// </summary>
    private static object? ApplyPostfixIncrement(IExchange exchange, string propertyName)
        => ApplyIncrementDecrementOperation(exchange, propertyName, OperationType.PostfixIncrement);
    
    /// <summary>
    /// Applies postfix decrement (x--) for numeric values and updates the value in exchange
    /// </summary>
    private static object? ApplyPostfixDecrement(IExchange exchange, string propertyName)
        => ApplyIncrementDecrementOperation(exchange, propertyName, OperationType.PostfixDecrement);

    #endregion

    #region Binary operations

    /// <summary>
    /// Applies the addition operation for different data types
    /// </summary>
    private static object? ApplyAddition(object? left, object? right)
    {
        DebugLog($"Applying addition operation: '{left}' + '{right}'");
        
        if (left == null || right == null)
        {
            DebugLog("One of the operands is null");
            return left ?? right; // Return the non-null operand, or null if both are null
        }
        
        // If both operands are numeric, perform arithmetic addition
        if (TryConvertToNumber(left, out double leftNum) && TryConvertToNumber(right, out double rightNum))
        {
            var result = leftNum + rightNum;
            DebugLog($"Numeric addition: {leftNum} + {rightNum} = {result}");
            
            // Return an integer if the result is whole
            if (result == Math.Floor(result))
            {
                return (int)result;
            }
            
            return result;
        }
        
        // For strings and other types, perform concatenation
        var leftStr = left.ToString() ?? string.Empty;
        var rightStr = right.ToString() ?? string.Empty;
        var strResult = leftStr + rightStr;
        DebugLog($"String concatenation: '{leftStr}' + '{rightStr}' = '{strResult}'");
        
        return strResult;
    }
    
    /// <summary>
    /// Applies the subtraction operation for numeric types
    /// </summary>
    private static object? ApplySubtraction(object? left, object? right)
    {
        DebugLog($"Applying subtraction operation: '{left}' - '{right}'");
        
        if (left == null || right == null)
        {
            DebugLog("One of the operands is null");
            return left; // For subtraction, return the left operand if one of them is null
        }
        
        // Perform subtraction only for numeric types
        if (TryConvertToNumber(left, out double leftNum) && TryConvertToNumber(right, out double rightNum))
        {
            var result = leftNum - rightNum;
            DebugLog($"Numeric subtraction: {leftNum} - {rightNum} = {result}");
            
            // Return an integer if the result is whole
            if (result == Math.Floor(result))
            {
                return (int)result;
            }
            
            return result;
        }
        
        DebugLog("Operands are not numbers, subtraction is not possible");
        return left; // For non-numeric types, return the left operand
    }
    
    /// <summary>
    /// Applies the multiplication operation for numeric types
    /// </summary>
    private static object? ApplyMultiplication(object? left, object? right)
    {
        DebugLog($"Applying multiplication operation: '{left}' * '{right}'");
        
        if (left == null || right == null)
        {
            DebugLog("One of the operands is null");
            return null; // For multiplication, return null if one of the operands is null
        }
        
        // Perform multiplication only for numeric types
        if (TryConvertToNumber(left, out double leftNum) && TryConvertToNumber(right, out double rightNum))
        {
            var result = leftNum * rightNum;
            DebugLog($"Numeric multiplication: {leftNum} * {rightNum} = {result}");
            
            // Return an integer if the result is whole
            if (result == Math.Floor(result))
            {
                return (int)result;
            }
            
            return result;
        }
        
        // Support for string multiplication by number (repetition)
        if (left is string str && TryConvertToNumber(right, out double rightNumber) && 
            rightNumber >= 0 && rightNumber == Math.Floor(rightNumber))
        {
            var count = (int)rightNumber;
            var result = string.Concat(Enumerable.Repeat(str, count));
            DebugLog($"String multiplication: '{str}' * {count} = '{result}'");
            return result;
        }
        
        // Support for number multiplication by string (repetition)
        if (right is string str2 && TryConvertToNumber(left, out double leftNumber) && 
            leftNumber >= 0 && leftNumber == Math.Floor(leftNumber))
        {
            var count = (int)leftNumber;
            var result = string.Concat(Enumerable.Repeat(str2, count));
            DebugLog($"String multiplication: '{str2}' * {count} = '{result}'");
            return result;
        }
        
        DebugLog("Operands do not support multiplication");
        return null;
    }
    
    /// <summary>
    /// Applies the division operation for numeric types
    /// </summary>
    private static object? ApplyDivision(object? left, object? right)
    {
        DebugLog($"Applying division operation: {left} / {right}");
        
        if (left == null || right == null)
        {
            DebugLog("One of the operands is null, returning null");
            return null;
        }
        
        // Convert both values to numbers
        if (TryConvertToNumber(left, out double leftNumber) && TryConvertToNumber(right, out double rightNumber))
        {
            if (Math.Abs(rightNumber) < double.Epsilon)
            {
                DebugLog("Division by zero, returning null");
                return null;
            }
            
            var result = leftNumber / rightNumber;
            DebugLog($"Division result: {result}");
            
            // Check if we can return an integer result
            if (Math.Abs(result - Math.Floor(result)) < double.Epsilon)
            {
                return Convert.ToInt32(result);
            }
            
            return result;
        }
        
        DebugLog("Cannot perform division for non-numeric types");
        return null;
    }

    #endregion

    #region JSONPath operations

    /// <summary>
    /// Applies JSONPath to the message body.
    /// Returns <c>null</c> when the path selects nothing; propagates real errors.
    /// </summary>
    private static object? ApplyJPath(IExchange exchange, string jsonPath)
    {
        DebugLog($"Applying JSONPath: '{jsonPath}'");

        var body = exchange.In.getBody<object>();
        DebugLog($"Message body type: {(body != null ? body.GetType().Name : "null")}");

        if (body == null)
            return null;

        var jsonPathExpression = new JsonPathExpression(jsonPath);

        // For filter queries [?(...)] return JToken to preserve structure
        if (jsonPath.Contains("[?"))
        {
            var jTokenResult = jsonPathExpression.Evaluate<JToken>(exchange);
            DebugLog($"JSONPath result (JToken): {jTokenResult}");
            return jTokenResult;
        }

        // For regular queries — delegate to JsonPathExpression which handles
        // typed array conversion (int[], string[], etc.) inside ConvertJTokenToType<object>
        var result = jsonPathExpression.Evaluate<object>(exchange);
        DebugLog($"JSONPath result: {result}");
        return result;
    }
    
    #endregion

    #region XPath operations

    /// <summary>
    /// Applies XPath to the message body.
    /// Returns <c>null</c> when the body is <c>null</c>; propagates real errors.
    /// </summary>
    private static object? ApplyXPath(IExchange exchange, string xpath)
    {
        DebugLog($"Applying XPath: '{xpath}'");

        var body = exchange.In.getBody<object>();
        DebugLog($"Message body type: {(body != null ? body.GetType().Name : "null")}");

        if (body == null)
            return null;

        var xpathExpression = new XPathExpression(xpath);
        var result = xpathExpression.Evaluate<object>(exchange);
        DebugLog($"XPath result: {result}");
        return result;
    }

    #endregion
}
