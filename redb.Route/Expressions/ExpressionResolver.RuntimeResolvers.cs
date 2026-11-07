using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver - runtime property and method resolution methods
/// </summary>
public static partial class ExpressionResolver
{
    #region Getting properties from Exchange

    /// <summary>
    /// Gets a property value from exchange
    /// </summary>
    private static object? GetExchangeProperty(IExchange exchange, string propertyName)
    {
        DebugLog($"Getting exchange property: '{propertyName}'");
        
        if (propertyName.StartsWith("exception."))
        {
            var exceptionPropertyName = propertyName.Substring(EXCEPTION_PREFIX.Length); // remove "exception."
            DebugLog($"Getting exception property: '{exceptionPropertyName}'");
            
            if (exchange.Exception == null)
            {
                DebugLog("Exchange.Exception is null");
                return null;
            }
            
            return ResolvePropertyPath(exchange.Exception, exceptionPropertyName);
        }
        else if (propertyName.Equals("exception"))
        {
            DebugLog("Getting full exception object");
            return exchange.Exception;
        }
        else if (propertyName.StartsWith("property."))
        {
            // Smart resolution: literal name first (dots included), then a nested member path.
            // This is the same rule the value path has always used; without it the AST branch
            // answered null for "property.cfg.limit" while the value branch answered 5.
            var actualPropertyName = propertyName.Substring(PROPERTY_PREFIX.Length);
            DebugLog($"Getting property: '{actualPropertyName}'");
            return actualPropertyName.Contains('.')
                ? ResolvePropertySmart(exchange, actualPropertyName)
                : exchange.getProperty<object>(actualPropertyName);
        }
        else if (propertyName.StartsWith("header."))
        {
            // Same smart rule for headers: "header.user.Age" is the Age member of the "user"
            // header unless a header literally named "user.Age" exists.
            var headerName = propertyName.Substring(HEADER_PREFIX.Length);
            DebugLog($"Getting header: '{headerName}'");
            return headerName.Contains('.')
                ? ResolveHeaderSmart(exchange, headerName)
                : exchange.In.getHeader<object>(headerName);
        }
        else if (propertyName.StartsWith("body."))
        {
            var bodyPath = propertyName.Substring(BODY_PREFIX.Length);
            DebugLog($"Getting body path: '{bodyPath}'");
            return ResolvePropertyPath(exchange.In.getBody<object>(), bodyPath);
        }
        else if (propertyName == "body")
        {
            DebugLog($"Getting body");
            return exchange.In.getBody<object>();
        }
        else if (propertyName == "contentType")
        {
            // The removed hand-written branch resolved contentType; the unified engine has to
            // keep every accessor the language documents.
            DebugLog("Getting contentType");
            return exchange.In.ContentType;
        }
        else if (IsAttachmentsPath(propertyName))
        {
            // The attachments of the In message, as Camel Simple's ${attachments}: before the exchange-property
            // fallback, so a property named "attachments" no longer answers here.
            DebugLog($"Getting attachments: '{propertyName}'");
            return ResolveAttachmentsPath(exchange, propertyName);
        }
        else
        {
            // Pluggable prefix resolvers (e.g. conversation., tool.) take precedence
            // over the default property fallback so that custom roots aren't masked
            // by a coincidentally-matching exchange property.
            var (handled, value) = TryResolveByPrefix(exchange, propertyName);
            if (handled)
            {
                DebugLog($"Resolved via prefix resolver: '{propertyName}' -> {value}");
                return value;
            }

            DebugLog($"Getting default property: '{propertyName}'");
            return exchange.getProperty<object>(propertyName);
        }
    }

    /// <summary>
    /// Smart property resolution with support for dots in names
    /// Priority: literal name → nested path
    /// </summary>
    private static object? ResolvePropertySmart(IExchange exchange, string fullPath)
    {
        DebugLog($"Smart property resolution: '{fullPath}'");
        
        // 1. FIRST: check literal name (with dots)
        if (exchange.Properties.ContainsKey(fullPath))
        {
            DebugLog($"Found property with literal name: '{fullPath}'");
            return exchange.getProperty<object>(fullPath);
        }
        
        // 2. THEN: if no literal name and there's a dot - try nested path
        if (fullPath.Contains('.'))
        {
            var firstDot = fullPath.IndexOf('.');
            var rootPropertyName = fullPath.Substring(0, firstDot);
            var remainingPath = fullPath.Substring(firstDot + 1);
            
            DebugLog($"Trying nested path: property='{rootPropertyName}', path='{remainingPath}'");
            
            if (exchange.Properties.ContainsKey(rootPropertyName))
            {
                var propertyValue = exchange.getProperty<object>(rootPropertyName);
                if (propertyValue != null)
                {
                    return ResolvePropertyPath(propertyValue, remainingPath);
                }
            }
        }
        
        DebugLog($"Property not found: '{fullPath}'");
        return null;
    }

    /// <summary>
    /// Smart header resolution with support for dots in names
    /// Priority: literal name → nested path
    /// </summary>
    private static object? ResolveHeaderSmart(IExchange exchange, string fullPath)
    {
        DebugLog($"Smart header resolution: '{fullPath}'");
        
        // 1. FIRST: check literal name (with dots)
        if (exchange.In.Headers.ContainsKey(fullPath))
        {
            DebugLog($"Found header with literal name: '{fullPath}'");
            return exchange.In.getHeader<object>(fullPath);
        }
        
        // 2. THEN: if no literal name and there's a dot - try nested path
        if (fullPath.Contains('.'))
        {
            var firstDot = fullPath.IndexOf('.');
            var rootHeaderName = fullPath.Substring(0, firstDot);
            var remainingPath = fullPath.Substring(firstDot + 1);
            
            DebugLog($"Trying nested path: header='{rootHeaderName}', path='{remainingPath}'");
            
            if (exchange.In.Headers.ContainsKey(rootHeaderName))
            {
                var headerValue = exchange.In.getHeader<object>(rootHeaderName);
                if (headerValue != null)
                {
                    return ResolvePropertyPath(headerValue, remainingPath);
                }
            }
        }
        
        DebugLog($"Header not found: '{fullPath}'");
        _logger?.LogDebug("Expression: header '{HeaderName}' not found, resolving to empty", fullPath);
        return null;
    }

    /// <summary>
    /// Gets a property value from body via runtime reflection
    /// </summary>
    private static object? ResolveBodyProperty(IExchange exchange, string propertyPath)
    {
        DebugLog($"ResolveBodyProperty: getting '{propertyPath}' from body via runtime reflection");
        
        try
        {
            // Get body as object
            var body = exchange.In.getBody<object>();
            
            if (body == null)
            {
                DebugLog("ResolveBodyProperty: body is null");
                return null;
            }
            
            DebugLog($"ResolveBodyProperty: body type = {body.GetType().Name}");
            
            // Use ResolvePropertyPath for runtime property access
            var result = ResolvePropertyPath(body, propertyPath);
            
            DebugLog($"ResolveBodyProperty: result = {result}");
            return result;
        }
        catch (Exception ex) when (ex is not ExpressionSandboxViolationException)
        {
            DebugLog($"ResolveBodyProperty: error - {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Property path resolution

    /// <summary>
    /// Resolves a property path with exchange passed for dynamic index support
    /// </summary>
    private static object? ResolvePropertyPathWithExchange(object? obj, string path, IExchange exchange)
    {
        DebugLog($"Resolving property path with exchange: obj={obj?.GetType().Name ?? "null"}, path='{path}'");
        
        if (string.IsNullOrEmpty(path))
        {
            DebugLog($"Path is empty, returning: {obj}");
            return obj;
        }
        
        if (obj == null)
        {
            DebugLog("Source object is null, cannot get nested property");
            return null;
        }
        
        // Split path into parts accounting for possible operators
        int dotIndex = IndexOfPathDot(path);
        int operatorIndex = FindFirstOperator(path);
        
        // If comparison operator found before dot or no dot found
        if ((operatorIndex >= 0 && (dotIndex == -1 || operatorIndex < dotIndex)) || dotIndex == -1)
        {
            string propertyName = operatorIndex >= 0 ? path.Substring(0, operatorIndex).Trim() : path.Trim();
            DebugLog($"Getting simple property: '{propertyName}'");
            
            object? result = ResolvePropertyPartWithExchange(obj, propertyName, exchange);
            DebugLog($"Result of getting property '{propertyName}': {result}");
            return result;
        }
        
        // Process path with nested properties
        string firstPart = path.Substring(0, dotIndex).Trim();
        string remainingPath = path.Substring(dotIndex + 1).Trim();
        
        DebugLog($"Processing nested path: first part '{firstPart}', remaining path '{remainingPath}'");
        
        // Get the first part of the nested path
        object? firstObj = ResolvePropertyPartWithExchange(obj, firstPart, exchange);
        
        if (firstObj == null)
        {
            DebugLog($"Failed to get object at path '{firstPart}', returning null");
            return null;
        }
        
        // Recursively process the remaining path
        return ResolvePropertyPathWithExchange(firstObj, remainingPath, exchange);
    }

    /// <summary>
    /// Resolves a property path on an object with support for dictionary and array access via square brackets
    /// </summary>
    private static object? ResolvePropertyPath(object? obj, string path)
    {
        DebugLog($"Resolving property path: obj={obj?.GetType().Name ?? "null"}, path='{path}'");
        
        if (obj == null || string.IsNullOrEmpty(path))
        {
            DebugLog($"Object is null or path is empty, returning: {obj}");
            return obj;
        }

        var parts = SplitPathWithBrackets(path);
        var current = obj;

        foreach (var part in parts)
        {
            if (current == null)
            {
                DebugLog($"Current object is null at part: '{part}'");
                return null;
            }
                    
            current = ResolvePropertyPart(current, part);
            if (current == null)
            {
                DebugLog($"Failed to resolve part '{part}'");
                return null;
            }
        }

        DebugLog($"Property path resolution result: {current}");
        return current;
    }

    /// <summary>
    /// Resolves a single part of a property path with exchange support for dynamic indices
    /// </summary>
    private static object? ResolvePropertyPartWithExchange(object current, string part, IExchange exchange)
    {
        DebugLog($"Resolving part '{part}' in type {current.GetType().Name} with exchange");
        
        // Check if the part is an index or method access
        if (part.Contains("[") && part.EndsWith("]"))
        {
            // Handle index access with support for expressions in indices
            var indexStart = part.IndexOf("[");
            var propertyName = part.Substring(0, indexStart);
            var indexExpression = part.Substring(indexStart + 1, part.Length - indexStart - 2);
            
            DebugLog($"Index access: property='{propertyName}', index='{indexExpression}'");
            
            // Get the object to which the index will be applied
            object? targetObject;
            if (string.IsNullOrEmpty(propertyName))
            {
                targetObject = current; // If property name is empty, use the current object
            }
            else
            {
                // Otherwise get property from current object
                targetObject = ResolvePropertyPart(current, propertyName);
                if (targetObject == null)
                {
                    DebugLog($"Failed to get target object for index access: '{propertyName}'");
                    return null;
                }
            }
            
            // If index contains an expression, evaluate it
            if (indexExpression.Contains("${"))
            {
                indexExpression = ProcessTemplate(indexExpression, exchange);
                DebugLog($"Evaluated index expression: '{indexExpression}'");
            }
            
            // Apply index access
            return ResolveIndexAccess(targetObject, indexExpression, current);
        }
        else if (part.Contains("(") && part.EndsWith(")"))
        {
            // Handle method call
            var methodStart = part.IndexOf("(");
            var methodName = part.Substring(0, methodStart);
            var paramsPart = part.Substring(methodStart + 1, part.Length - methodStart - 2);
            
            DebugLog($"Method call: method='{methodName}', parameters='{paramsPart}'");
            
            // Parse parameters, supporting possible expressions
            var parameters = ParseMethodParameters(paramsPart, exchange);
            
            // Call the method
            return ResolveMethod(current, methodName, parameters, exchange);
        }
        else
        {
            // Standard property access
            try
            {
                // Check special properties/methods
                if (part == "length" || part == "Length")
                {
                    return ResolveLength(current);
                }
                else if (part == "size" || part == "Size" || part == "count" || part == "Count")
                {
                    // For collections and strings, return element count
                    return ResolveLength(current);
                }
                
                // Get property info
                var property = FindProperty(current.GetType(), part);
                
                if (property != null)
                {
                    DebugLog($"Found property '{part}' in type {current.GetType().Name}");
                    return property.GetValue(current);
                }
                
                // If property not found, check for string key indexer
                var indexerProperty = current.GetType().GetProperty("Item", 
                    BindingFlags.Instance | BindingFlags.Public);
                
                if (indexerProperty != null && indexerProperty.GetIndexParameters().Length == 1)
                {
                    var indexParam = indexerProperty.GetIndexParameters()[0];
                    if (indexParam.ParameterType == typeof(string))
                    {
                        DebugLog($"Using string indexer for '{part}'");
                        try
                        {
                            return indexerProperty.GetValue(current, new object[] { part });
                        }
                        catch (Exception ex) when (ex is not ExpressionSandboxViolationException)
                        {
                            DebugLog($"Error using string indexer: {ex.Message}");
                        }
                    }
                }
                
                // Try to find a field
                var field = FindField(current.GetType(), part);
                
                if (field != null)
                {
                    DebugLog($"Found field '{part}' in type {current.GetType().Name}");
                    return field.GetValue(current);
                }
                
                // Check IDictionary (non-generic dictionary)
                if (current is System.Collections.IDictionary dictionary)
                {
                    foreach (var key in dictionary.Keys)
                    {
                        if (key?.ToString()?.Equals(part, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            DebugLog($"Found key '{part}' in dictionary");
                            return dictionary[key];
                        }
                    }
                }
                
                // Check typed dictionaries (IDictionary<string, T>)
                var dictionaryType = current.GetType().GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && 
                                         i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IDictionary<,>) &&
                                         i.GetGenericArguments()[0] == typeof(string));
                
                if (dictionaryType != null)
                {
                    DebugLog($"Object implements IDictionary<string,T>, trying to get value by key '{part}'");
                    var containsMethod = dictionaryType.GetMethod("ContainsKey");
                    var getItemMethod = dictionaryType.GetProperty("Item").GetGetMethod();
                    
                    if (containsMethod != null && getItemMethod != null &&
                        (bool)containsMethod.Invoke(current, new object[] { part }))
                    {
                        return getItemMethod.Invoke(current, new object[] { part });
                    }
                }
                
                // If nothing found
                DebugLog($"Failed to find property, field or indexer '{part}' in type {current.GetType().Name}");
                return null;
            }
            catch (Exception ex) when (ex is not ExpressionSandboxViolationException and not ExpressionEvaluationException)
            {
                DebugLog($"Error resolving path part '{part}': {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Resolves a single part of a property path, supporting square brackets for element access
    /// </summary>
    private static object? ResolvePropertyPart(object current, string part)
    {
        DebugLog($"Resolving part '{part}' in type {current.GetType().Name}");

        // Check if the part contains square brackets
        var bracketStart = part.IndexOf('[');
        if (bracketStart >= 0)
        {
            var bracketEnd = part.LastIndexOf(']');
            if (bracketEnd > bracketStart)
            {
                var propertyName = part.Substring(0, bracketStart);
                var indexExpression = part.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
                
                DebugLog($"Detected element access: property='{propertyName}', index='{indexExpression}'");

                // First get the object (dictionary or array)
                object? targetObject;
                if (string.IsNullOrEmpty(propertyName))
                {
                    // Direct access to current object (e.g., [0] or ['key'])
                    targetObject = current;
                }
                else
                {
                    // Access object's property
                    var property = FindProperty(current.GetType(), propertyName);
                    if (property != null)
                    {
                        targetObject = property.GetValue(current);
                    }
                    else
                    {
                        // Same rule as the plain member step: a public field is a member too.
                        var field = FindField(current.GetType(), propertyName);
                        if (field == null)
                        {
                            DebugLog($"Member '{propertyName}' not found");
                            return null;
                        }
                        targetObject = field.GetValue(current);
                    }
                }

                if (targetObject == null)
                {
                    DebugLog($"Target object for element access is null");
                    return null;
                }

                return ResolveIndexAccess(targetObject, indexExpression, current);
            }
        }

        // Check for special methods
        if (part.EndsWith("()"))
        {
            var methodName = part.Substring(0, part.Length - 2);
            return ResolveMethod(current, methodName, new object[0], null);
        }

        // Check for methods with parameters (e.g., contains('text') or substring(1,3))
        var parenStart = part.IndexOf('(');
        if (parenStart >= 0 && part.EndsWith(")"))
        {
            var methodName = part.Substring(0, parenStart);
            var paramsPart = part.Substring(parenStart + 1, part.Length - parenStart - 2);
            DebugLog($" Detected method call: '{methodName}' with parameters '{paramsPart}'");
            var parameters = ParseMethodParameters(paramsPart, null);
            DebugLog($" Parameters parsed: {parameters.Length} items");
            var result = ResolveMethod(current, methodName, parameters, null);
            DebugLog($" Method '{methodName}' result: {result}");
            return result;
        }

        // Regular property access
        DebugLog($"Looking for property '{part}' in type {current.GetType().Name}");
        var normalProperty = FindProperty(current.GetType(), part);
        if (normalProperty != null)
        {
            var value = normalProperty.GetValue(current);
            DebugLog($"Found property '{part}', value: {value}");
            return value;
        }

        // A public field is a member like any other: header.x.Field, property.x.Field and
        // body.Field must all read it. Until 2026-08-29 only one of the three paths did.
        var normalField = FindField(current.GetType(), part);
        if (normalField != null)
        {
            var value = normalField.GetValue(current);
            DebugLog($"Found field '{part}', value: {value}");
            return value;
        }

        // Check IDictionary (non-generic)
        if (current is System.Collections.IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                if (key?.ToString()?.Equals(part, StringComparison.OrdinalIgnoreCase) == true)
                {
                    DebugLog($"Found key '{part}' in dictionary");
                    return dictionary[key];
                }
            }
        }

        // Check typed dictionaries (IDictionary<string, T>)
        var dictionaryType = current.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                                 i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IDictionary<,>) &&
                                 i.GetGenericArguments()[0] == typeof(string));

        if (dictionaryType != null)
        {
            DebugLog($"Object implements IDictionary<string,T>, trying to get value by key '{part}'");
            var containsMethod = dictionaryType.GetMethod("ContainsKey");
            var getItemMethod = dictionaryType.GetProperty("Item")?.GetGetMethod();

            if (containsMethod != null && getItemMethod != null &&
                (bool)containsMethod.Invoke(current, new object[] { part })!)
            {
                return getItemMethod.Invoke(current, new object[] { part });
            }
        }

        DebugLog($"Property '{part}' not found");
        return null;
    }

    /// <summary>
    /// Splits a property path at its separators: a dot outside parentheses, square brackets and quotes, so a method
    /// argument keeps its own dots (<c>list.contains(5.0)</c>, <c>name.replace('a.b', 'c')</c>). Empty segments are
    /// kept, as <see cref="string.Split(char, StringSplitOptions)"/> keeps them: <c>a..b</c> reads nothing.
    /// </summary>
    private static string[] SplitPathWithBrackets(string path)
    {
        var parts = new List<string>();
        var start = 0;
        while (true)
        {
            var dot = IndexOfPathDot(path, start);
            parts.Add(dot < 0 ? path[start..] : path[start..dot]);
            if (dot < 0)
                return parts.ToArray();
            start = dot + 1;
        }
    }

    /// <summary>
    /// The first path separator of <paramref name="path"/> at or after <paramref name="start"/>: a dot outside
    /// parentheses, square brackets and quoted literals; -1 when there is none.
    /// </summary>
    private static int IndexOfPathDot(string path, int start = 0)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = start; i < path.Length; i++)
        {
            var ch = path[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < path.Length)
                    i++;
                else if (ch == quote)
                    quote = '\0';
                continue;
            }
            switch (ch)
            {
                case '\'' or '"': quote = ch; break;
                case '(' or '[': depth++; break;
                case ')' or ']': if (depth > 0) depth--; break;
                case '.' when depth == 0: return i;
            }
        }
        return -1;
    }

    #endregion

    #region Index access resolution

    /// <summary>
    /// Resolves element access by index or key
    /// </summary>
    private static object? ResolveIndexAccess(object targetObject, string indexExpression, object rootObject)
    {
        DebugLog($"Resolving element access: object={targetObject.GetType().Name}, index='{indexExpression}'");

        // Determine the index value
        object? indexValue = ResolveIndexValue(indexExpression, rootObject);
        
        DebugLog($"Index value: {indexValue} (type: {indexValue?.GetType().Name ?? "null"})");

        // Dictionary access
        if (targetObject is IDictionary<string, object> stringDict)
        {
            var key = indexValue?.ToString();
            if (key != null && stringDict.TryGetValue(key, out var dictValue))
            {
                DebugLog($"Found dictionary element by key '{key}': {dictValue}");
                return dictValue;
            }
            DebugLog($"Key '{key}' not found in dictionary");
            return null;
        }

        // Non-generic dictionary access
        if (targetObject is System.Collections.IDictionary dict)
        {
            var key = indexValue?.ToString();
            if (key != null && dict.Contains(key))
            {
                var dictValue = dict[key];
                DebugLog($"Found dictionary element by key '{key}': {dictValue}");
                return dictValue;
            }
            DebugLog($"Key '{key}' not found in dictionary");
            return null;
        }

        // Array or list access
        if (targetObject is System.Collections.IList list)
        {
            if (indexValue is int intIndex)
            {
                if (intIndex >= 0 && intIndex < list.Count)
                {
                    var listValue = list[intIndex];
                    DebugLog($"Found list element at index {intIndex}: {listValue}");
                    return listValue;
                }
                DebugLog($"Index {intIndex} out of list bounds (size: {list.Count})");
                return null;
            }
            
            // Try to convert to int
            if (int.TryParse(indexValue?.ToString(), out var parsedIndex))
            {
                if (parsedIndex >= 0 && parsedIndex < list.Count)
                {
                    var listValue = list[parsedIndex];
                    DebugLog($"Found list element at index {parsedIndex}: {listValue}");
                    return listValue;
                }
                DebugLog($"Index {parsedIndex} out of list bounds (size: {list.Count})");
                return null;
            }
        }

        // Array access
        if (targetObject is Array array)
        {
            if (indexValue is int intIndex)
            {
                if (intIndex >= 0 && intIndex < array.Length)
                {
                    var arrayValue = array.GetValue(intIndex);
                    DebugLog($"Found array element at index {intIndex}: {arrayValue}");
                    return arrayValue;
                }
                DebugLog($"Index {intIndex} out of array bounds (size: {array.Length})");
                return null;
            }
            
            // Try to convert to int
            if (int.TryParse(indexValue?.ToString(), out var parsedIndex))
            {
                if (parsedIndex >= 0 && parsedIndex < array.Length)
                {
                    var arrayValue = array.GetValue(parsedIndex);
                    DebugLog($"Found array element at index {parsedIndex}: {arrayValue}");
                    return arrayValue;
                }
                DebugLog($"Index {parsedIndex} out of array bounds (size: {array.Length})");
                return null;
            }
        }

        DebugLog($"Failed to access element: object type {targetObject.GetType().Name} does not support indexing");
        return null;
    }

    /// <summary>
    /// Resolves an index value (can be a literal or a property reference)
    /// </summary>
    private static object? ResolveIndexValue(string indexExpression, object rootObject)
    {
        DebugLog($"Resolving index value: '{indexExpression}'");

        // Check if this is a property reference
        if (indexExpression.StartsWith("property."))
        {
            var propertyName = indexExpression.Substring(PROPERTY_PREFIX.Length);
            DebugLog($"Index is a property reference: '{propertyName}'");
            
            // Get the property value from rootObject (which should be IExchange)
            if (rootObject is IExchange exchange)
            {
                var propertyValue = GetExchangeProperty(exchange, propertyName);
                DebugLog($"Property '{propertyName}' value: {propertyValue}");
                return propertyValue;
            }
            else
            {
                DebugLog($"Root object is not IExchange, cannot get property");
                return null;
            }
        }

        // Otherwise parse as literal
        var literalValue = ParseLiteral(indexExpression);
        DebugLog($"Index as literal: '{indexExpression}' -> {literalValue}");
        return literalValue;
    }

    #endregion

    #region Method resolution

    /// <summary>
    /// Resolves a method call on an object
    /// </summary>
    private static object? ResolveMethod(object current, string methodName, object[] parameters, IExchange? exchange)
    {
        DebugLog($"Resolving method '{methodName}' with {parameters.Length} parameters on type {current.GetType().Name}");
        
        try
        {
            switch (methodName.ToLowerInvariant())
            {
                case "length":
                case "count":
                case "size":
                    return ResolveLength(current);
                    
                case "contains":
                    return ResolveContains(current, parameters);
                    
                case "substring":
                    return ResolveSubstring(current, parameters);
                    
                case "trim":
                    return ResolveTrim(current, parameters);
                    
                case "toupper":
                    return ResolveToUpper(current, parameters);
                    
                case "tolower":
                    return ResolveToLower(current, parameters);
                    
                case "replace":
                    return ResolveReplace(current, parameters);
                    
                case "startswith":
                    return ResolveStartsWith(current, parameters);
                    
                case "endswith":
                    return ResolveEndsWith(current, parameters);
                    
                case "indexof":
                    return ResolveIndexOf(current, parameters);
                    
                default:
                    // Anything beyond the built-in helpers is a reflection call on the object: refused
                    // inside a sandbox (templates), where an expression reads members but never runs code.
                    if (ExpressionSandbox.IsActive)
                        throw new ExpressionSandboxViolationException(methodName, current.GetType());
                    return ResolveMethodByReflection(current, methodName, parameters);
            }
        }
        catch (Exception ex) when (ex is not ExpressionSandboxViolationException)
        {
            DebugLog($"Error calling method '{methodName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves the length/size method
    /// </summary>
    private static object? ResolveLength(object current)
    {
        DebugLog($"Resolving length/size for type {current.GetType().Name}");
        
        // For strings
        if (current is string str)
        {
            var result = str.Length;
            DebugLog($"String length: {result}");
            return result;
        }
        
        // For collections
        if (current is System.Collections.ICollection collection)
        {
            var result = collection.Count;
            DebugLog($"Collection size: {result}");
            return result;
        }
        
        // For arrays
        if (current is Array array)
        {
            var result = array.Length;
            DebugLog($"Array length: {result}");
            return result;
        }
        
        // Try to find Length or Count property
        var lengthProperty = current.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
        if (lengthProperty != null)
        {
            var result = lengthProperty.GetValue(current);
            DebugLog($"Length property: {result}");
            return result;
        }
        
        var countProperty = current.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        if (countProperty != null)
        {
            var result = countProperty.GetValue(current);
            DebugLog($"Count property: {result}");
            return result;
        }
        
        DebugLog($"Failed to determine length for type {current.GetType().Name}");
        return null;
    }

    #endregion

    #region Method parameter parsing

    /// <summary>
    /// Parses method parameters from a string
    /// </summary>
    private static object[] ParseMethodParameters(string parametersString, IExchange? exchange)
    {
        DebugLog($"ParseMethodParameters: input string='{parametersString}'");
        
        if (string.IsNullOrEmpty(parametersString))
        {
            DebugLog("ParseMethodParameters: empty parameter string, returning empty array");
            return Array.Empty<object>();
        }

        var paramStrings = SplitParameters(parametersString);
        DebugLog($"ParseMethodParameters: split into {paramStrings.Length} parameters: [{string.Join(", ", paramStrings.Select(p => $"'{p}'"))}]");
        
        var parameters = new List<object>();

        foreach (var paramStr in paramStrings)
        {
            var trimmed = paramStr.Trim();
            DebugLog($"ParseMethodParameters: processing parameter '{trimmed}'");
            
            // If parameter starts with property., it's a property reference
            if (trimmed.StartsWith("property."))
            {
                var propertyName = trimmed.Substring(PROPERTY_PREFIX.Length);
                DebugLog($"ParseMethodParameters: property reference '{propertyName}'");
                
                if (exchange != null)
                {
                    var value = GetExchangeProperty(exchange, propertyName);
                    DebugLog($"ParseMethodParameters: property '{propertyName}' value = '{value}' (type: {value?.GetType().Name ?? "null"})");
                    parameters.Add(value);
                }
                else
                {
                    DebugLog($"ParseMethodParameters: exchange is null, cannot get property '{propertyName}', using string as-is");
                    parameters.Add(trimmed); // Add as string if exchange is unavailable
                }
            }
            // If parameter is in quotes, it's a string literal
            else if ((trimmed.StartsWith("'") && trimmed.EndsWith("'")) || 
                     (trimmed.StartsWith("\"") && trimmed.EndsWith("\"")))
            {
                var stringValue = trimmed.Substring(1, trimmed.Length - 2);
                DebugLog($"ParseMethodParameters: string literal '{stringValue}'");
                parameters.Add(stringValue);
            }
            // Try to convert to number
            else if (TryConvertToInt(trimmed, out int intValue))
            {
                DebugLog($"ParseMethodParameters: integer {intValue}");
                parameters.Add(intValue);
            }
            // Otherwise treat as string
            else
            {
                DebugLog($"ParseMethodParameters: plain string '{trimmed}'");
                parameters.Add(trimmed);
            }
        }

        DebugLog($"ParseMethodParameters: final parameters: [{string.Join(", ", parameters.Select(p => $"'{p}' ({p?.GetType().Name ?? "null"})"))}]");
        return parameters.ToArray();
    }

    /// <summary>
    /// Splits a parameter string into individual parameters accounting for quotes
    /// </summary>
    private static string[] SplitParameters(string paramsPart)
    {
        var parameters = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';
        
        for (int i = 0; i < paramsPart.Length; i++)
        {
            var ch = paramsPart[i];
            
            if (!inQuotes && (ch == '\'' || ch == '"'))
            {
                inQuotes = true;
                quoteChar = ch;
                current.Append(ch);
            }
            else if (inQuotes && ch == quoteChar)
            {
                inQuotes = false;
                current.Append(ch);
            }
            else if (!inQuotes && ch == ',')
            {
                parameters.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        
        if (current.Length > 0)
        {
            parameters.Add(current.ToString());
        }
        
        return parameters.ToArray();
    }

    #endregion

    #region Public method ResolveExpression

    /// <summary>
    /// Evaluates an expression in the context of an exchange, by the same engine as every other position: a text with
    /// <c>${...}</c> placeholders renders as a template, any other text reads exactly as the inside of a <c>${...}</c>
    /// hole (a bare path is a lookup, an unquoted <c>jpath</c>/<c>xpath</c> path is allowed). What a payload template's
    /// <c>expr(...)</c> calls.
    /// </summary>
    /// <remarks>
    /// Until rows 2 and 22 of the 2026-11-05 findings register this was a dispatcher of its own: it sent a text to the
    /// expression engine only when it saw an operator written with spaces, a function call or an index, and returned any
    /// other text as it was, so <c>expr('1>0')</c> gave the text "1>0" and a <c>bean:</c> call came back unevaluated.
    /// </remarks>
    public static object? ResolveExpression(string expression, IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));

        if (string.IsNullOrEmpty(expression))
            return null;

        return TemplateScanner.HasPlaceholder(expression)
            ? ProcessTemplate(expression, exchange)
            : string.IsNullOrWhiteSpace(expression) ? null : GetCompiledPlaceholder(expression)(exchange);
    }

    #endregion
}

