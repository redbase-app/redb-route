using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using redb.Route.Abstractions;
using SysExpression = System.Linq.Expressions.Expression;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver — Expression Tree compilation methods
/// </summary>
public static partial class ExpressionResolver
{
    #region Template compilation

    /// <summary>
    /// Compiles a template string into a delegate
    /// </summary>
    private static Func<IExchange, string> CompileTemplate(string template)
    {
        DebugLog($" Compiling new template: '{template}'");
        
        try
        {
            DebugLog($"Starting template compilation: '{template}'");
            
            // Find all ${...} expressions in the template
            var matches = TemplateRegex.Matches(template);
            
            if (matches.Count == 0)
            {
                // If no variables found, return the template unchanged
                return _ => template;
            }
            
            DebugLog($"Found {matches.Count} expressions in template");
            
            // Split the template into parts and build an expression tree for compilation
            var parts = TemplateRegex.Split(template);
            var parameterExpression = SysExpression.Parameter(typeof(IExchange), "exchange");
            
            // Collect all parts into a string concatenation expression
            var expressionParts = new List<SysExpression>();
            
            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 0)
                {
                    // This is plain text between ${...} expressions
                    if (!string.IsNullOrEmpty(parts[i]))
                    {
                        expressionParts.Add(SysExpression.Constant(parts[i]));
                    }
                }
                else
                {
                    // This is a ${...} expression
                    var variableName = parts[i];
                    DebugLog($"Compiling expression: '{variableName}'");
                    
                    // Check whether this is an exchange variable
                    var propertyExpression = CompileTemplateExpression(variableName, parameterExpression);
                    expressionParts.Add(propertyExpression);
                }
            }
            
            // Combine all parts into a single expression
            SysExpression resultExpression = null;
            
            if (expressionParts.Count == 0)
            {
                resultExpression = SysExpression.Constant(string.Empty);
            }
            else if (expressionParts.Count == 1)
            {
                // Single expression — ensure string conversion via object?.ToString()
                var singlePart = expressionParts[0];
                var objValue = SysExpression.Convert(singlePart, typeof(object));
                var nullCheck = SysExpression.Equal(objValue, SysExpression.Constant(null, typeof(object)));
                var emptyStr = SysExpression.Constant(string.Empty);
                var toStr = SysExpression.Call(objValue, typeof(object).GetMethod("ToString")!);
                resultExpression = SysExpression.Condition(nullCheck, emptyStr, toStr);
            }
            else
            {
                // Use StringBuilder for optimized string concatenation
                var stringBuilderType = typeof(StringBuilder);
                var appendMethod = stringBuilderType.GetMethod("Append", new[] { typeof(object) });
                var toStringMethod = stringBuilderType.GetMethod("ToString", Type.EmptyTypes);
                
                // Create a StringBuilder instance
                var builderVar = SysExpression.Variable(stringBuilderType, "builder");
                var createBuilder = SysExpression.Assign(builderVar, SysExpression.New(stringBuilderType));
                
                // Append all parts
                var appendExpressions = expressionParts.Select(part => 
                    SysExpression.Call(builderVar, appendMethod, SysExpression.Convert(part, typeof(object))));
                
                // Call ToString
                var callToString = SysExpression.Call(builderVar, toStringMethod);
                
                // Assemble everything into a block expression
                var blockExpressions = new List<SysExpression> { createBuilder };
                blockExpressions.AddRange(appendExpressions);
                blockExpressions.Add(callToString);
                
                resultExpression = SysExpression.Block(new[] { builderVar }, blockExpressions);
            }
            
            // Create a lambda expression and compile it
            var lambda = SysExpression.Lambda<Func<IExchange, string>>(
                SysExpression.Convert(resultExpression, typeof(string)), 
                parameterExpression);
                
            var compiledTemplate = lambda.Compile();
            
            // Add to cache
            DebugLog($"Template successfully compiled: '{template}'");
            CacheCompiledTemplate(template, compiledTemplate);
            DebugLog($" Template compiled and cached: '{template}'");
            
            return compiledTemplate;
        }
        catch (Exception ex)
        {
            // Compilation error = syntax error → fail fast
            DebugLog($"Template compilation error: {ex.Message}");
            throw new ExpressionCompilationException(
                $"Failed to compile template '{template}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Compiles a single template expression
    /// </summary>
    private static SysExpression CompileTemplateExpression(string expression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling single template expression: '{expression}'");
        
        try
        {
            // Check if this is a logical() function call
            var logicalMatch = LogicalFunctionRegex.Match(expression);
            if (logicalMatch.Success)
            {
                var logicalExpression = logicalMatch.Groups[1].Value;
                DebugLog($"Detected logical function with expression: '{logicalExpression}'");
                
                // Compile the logical expression and convert the result to string
                var logicalFunc = CompileLogicalExpression(logicalExpression);
                var logicalCall = SysExpression.Call(
                    SysExpression.Constant(logicalFunc),
                    typeof(Func<IExchange, bool>).GetMethod("Invoke")!,
                    exchangeParam
                );
                
                // Convert bool to string
                var toStringMethod = typeof(bool).GetMethod("ToString", Type.EmptyTypes);
                return SysExpression.Call(logicalCall, toStringMethod!);
            }
            
            // Check for jpath function
            var jpathMatch = JPathFunctionRegex.Match(expression);
            if (jpathMatch.Success)
            {
                // Compile the jpath call
                var jpathExpression = CompileJPathExpression(expression, exchangeParam);
                
                // Convert the result to string
                var nullCheck = SysExpression.Equal(jpathExpression, SysExpression.Constant(null, typeof(object)));
                var emptyString = SysExpression.Constant(string.Empty);
                var toStringMethod = typeof(object).GetMethod("ToString");
                var toStringCall = SysExpression.Call(jpathExpression, toStringMethod!);
                
                // Return empty string if value is null, otherwise ToString()
                return SysExpression.Condition(nullCheck, emptyString, toStringCall);
            }
            
            // Check for xpath function
            var xpathMatchTpl = XPathFunctionRegex.Match(expression);
            if (xpathMatchTpl.Success)
            {
                var xpathExpression = CompileXPathExpression(expression, exchangeParam);
                
                var nullCheck = SysExpression.Equal(xpathExpression, SysExpression.Constant(null, typeof(object)));
                var emptyString = SysExpression.Constant(string.Empty);
                var toStringMethod = typeof(object).GetMethod("ToString");
                var toStringCall = SysExpression.Call(xpathExpression, toStringMethod!);
                
                return SysExpression.Condition(nullCheck, emptyString, toStringCall);
            }
            
            // Route function calls (upper, lower, trim, concat, etc.) and index access through AST
            if (HasFunctionCalls(expression) || HasIndexAccess(expression))
            {
                var astCompiled = CompileExpressionWithAst(expression);
                var astFunc = SysExpression.Constant(astCompiled);
                var invokeResult = SysExpression.Invoke(astFunc, exchangeParam);
                var invObjExpr = SysExpression.Convert(invokeResult, typeof(object));
                var invNullCheck = SysExpression.Equal(invObjExpr, SysExpression.Constant(null, typeof(object)));
                var invToStringMethod = typeof(object).GetMethod("ToString", Type.EmptyTypes);
                var invToStringCall = SysExpression.Call(invObjExpr, invToStringMethod!);
                return SysExpression.Condition(invNullCheck, SysExpression.Constant(string.Empty), invToStringCall);
            }
            
            // Check various expression types
            if (expression.StartsWith("body."))
            {
                DebugLog($"Detected body expression: '{expression}'");
                var propertyPath = expression.Substring(BODY_PREFIX.Length);
                DebugLog($"Resolving body property via runtime reflection: '{propertyPath}'");
                
                // Use runtime reflection to access body properties
                var resolveMethodInfo = typeof(ExpressionResolver).GetMethod(
                    nameof(ResolveBodyProperty), 
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                var bodyPropertyCall = SysExpression.Call(
                    resolveMethodInfo,
                    exchangeParam,
                    SysExpression.Constant(propertyPath));
                
                // Convert the result to string
                var nullCheck = SysExpression.Equal(bodyPropertyCall, SysExpression.Constant(null, typeof(object)));
                var emptyString = SysExpression.Constant(string.Empty);
                var toStringMethod = typeof(object).GetMethod("ToString");
                var toStringCall = SysExpression.Call(
                    SysExpression.Convert(bodyPropertyCall, typeof(object)), 
                    toStringMethod!);
                
                return SysExpression.Condition(nullCheck, emptyString, toStringCall);
            }
            
            if (expression == "body")
            {
                DebugLog($"Detected body expression (entire object): '{expression}'");
                var inProperty = SysExpression.Property(exchangeParam, nameof(IExchange.In));
                var getBodyMethod = typeof(IMessage).GetMethods()
                    .FirstOrDefault(m => m.Name == "getBody" && !m.IsGenericMethod);
                var bodyValue = SysExpression.Call(inProperty, getBodyMethod!);
                return bodyValue;
            }

            if (expression == "contentType")
            {
                DebugLog($"Detected contentType expression");
                var inProperty = SysExpression.Property(exchangeParam, nameof(IExchange.In));
                var contentTypeProperty = SysExpression.Property(inProperty, nameof(IMessage.ContentType));
                var nullCheck = SysExpression.Equal(contentTypeProperty, SysExpression.Constant(null, typeof(string)));
                return SysExpression.Condition(nullCheck, SysExpression.Constant(string.Empty), contentTypeProperty);
            }
            
            if (expression.StartsWith("header."))
            {
                var headerName = expression.Substring(HEADER_PREFIX.Length); // Strip "header." prefix
                DebugLog($"Detected header expression with name: '{headerName}'");
                
                // Check whether headerName contains dots indicating nested properties
                if (headerName.Contains('.'))
                {
                    DebugLog($"Detected nested header: '{headerName}'");
                    
                    var resolveHeaderSmartMethod = typeof(ExpressionResolver).GetMethod(
                        nameof(ResolveHeaderSmart), 
                        BindingFlags.NonPublic | BindingFlags.Static);
                    
                    var headerSmartCall = SysExpression.Call(
                        resolveHeaderSmartMethod,
                        exchangeParam,
                        SysExpression.Constant(headerName));
                    
                    // Convert the result to string
                    var nullCheckSmart = SysExpression.Equal(headerSmartCall, SysExpression.Constant(null, typeof(object)));
                    var emptyStringSmart = SysExpression.Constant(string.Empty);
                    var toStringMethodSmart = typeof(object).GetMethod("ToString");
                    var toStringCallSmart = SysExpression.Call(
                        SysExpression.Convert(headerSmartCall, typeof(object)),
                        toStringMethodSmart!);
                    
                    return SysExpression.Condition(nullCheckSmart, emptyStringSmart, toStringCallSmart);
                }
                
                // Simple header — also use ResolveHeaderSmart for safe TryGetValue
                var resolveMethod = typeof(ExpressionResolver).GetMethod(
                    nameof(ResolveHeaderSmart),
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                var headerResult = SysExpression.Call(
                    resolveMethod,
                    exchangeParam,
                    SysExpression.Constant(headerName));
                
                var nullCheckSimple = SysExpression.Equal(headerResult, SysExpression.Constant(null, typeof(object)));
                var emptyStringSimple = SysExpression.Constant(string.Empty);
                var toStringSimple = SysExpression.Call(
                    SysExpression.Convert(headerResult, typeof(object)),
                    typeof(object).GetMethod("ToString")!);
                
                return SysExpression.Condition(nullCheckSimple, emptyStringSimple, toStringSimple);
            }
            
            if (expression.StartsWith("exception."))
            {
                var exceptionPropertyName = expression.Substring(EXCEPTION_PREFIX.Length); // Strip "exception." prefix
                DebugLog($"Detected exception expression with name: '{exceptionPropertyName}'");
                
                var exceptionProperty = SysExpression.Property(exchangeParam, nameof(IExchange.Exception));
                
                // Check for null
                var nullCheck = SysExpression.Equal(exceptionProperty, SysExpression.Constant(null, typeof(Exception)));
                var emptyString = SysExpression.Constant(string.Empty);
                
                // Get the exception property (e.g. Message, StackTrace)
                var exceptionPropertyInfo = typeof(Exception).GetProperty(exceptionPropertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (exceptionPropertyInfo != null)
                {
                    var propertyAccess = SysExpression.Property(exceptionProperty, exceptionPropertyInfo);
                    var toStringMethod = typeof(object).GetMethod("ToString");
                    var toStringCall = SysExpression.Call(propertyAccess, toStringMethod!);
                    
                    // Return empty string if exception is null, otherwise the property value
                    return SysExpression.Condition(nullCheck, emptyString, toStringCall);
                }
                else
                {
                    DebugLog($"Property Exception.{exceptionPropertyName} not found");
                    return emptyString;
                }
            }
            
            if (expression.Equals("exception"))
            {
                DebugLog("Detected exception expression (entire object)");
                
                var exceptionProperty = SysExpression.Property(exchangeParam, nameof(IExchange.Exception));
                
                // Check for null
                var nullCheck = SysExpression.Equal(exceptionProperty, SysExpression.Constant(null, typeof(Exception)));
                var emptyString = SysExpression.Constant(string.Empty);
                
                var toStringMethod = typeof(object).GetMethod("ToString");
                var toStringCall = SysExpression.Call(exceptionProperty, toStringMethod!);
                
                // Return empty string if exception is null, otherwise ToString()
                return SysExpression.Condition(nullCheck, emptyString, toStringCall);
            }
            
            // Check whether the expression contains operations BEFORE prefix-based dispatch,
            // because expressions like "property.a + property.b" start with "property." but need
            // arithmetic compilation, not simple property access.
            if (HasBinaryOperatorsInExpression(expression))
            {
                // Route all binary expressions through AST for uniform handling
                var astCompiled = CompileExpressionWithAst(expression);
                var astFunc = SysExpression.Constant(astCompiled);
                var invokeResult = SysExpression.Invoke(astFunc, exchangeParam);
                var invObjExpr = SysExpression.Convert(invokeResult, typeof(object));
                var invNullCheck = SysExpression.Equal(invObjExpr, SysExpression.Constant(null, typeof(object)));
                var invToStringMethod = typeof(object).GetMethod("ToString", Type.EmptyTypes);
                var invToStringCall = SysExpression.Call(invObjExpr, invToStringMethod!);
                return SysExpression.Condition(invNullCheck, SysExpression.Constant(string.Empty), invToStringCall);
            }
            
            if (expression.StartsWith("property."))
            {
                var propertyName = expression.Substring(PROPERTY_PREFIX.Length);
                DebugLog($"Processing property: '{propertyName}'");
                
                // Check whether propertyName contains dots indicating nested properties
                if (propertyName.Contains('.'))
                {
                    DebugLog($"Detected nested property: '{propertyName}'");
                    
                    // Use ResolvePropertyPathWithExchange for complex path resolution
                    var resolveMethodInfo = typeof(ExpressionResolver).GetMethod(
                        nameof(ResolvePropertyPathWithExchange), 
                        BindingFlags.NonPublic | BindingFlags.Static);
                    
                    // Find the first path segment (before the first dot)
                    var firstDotIndex = propertyName.IndexOf('.');
                    var firstProperty = propertyName.Substring(0, firstDotIndex);
                    var remainingPath = propertyName.Substring(firstDotIndex + 1);
                    
                    // Get the root object
                    var getPropertyMethodInfo = typeof(ExpressionResolver).GetMethod(
                        nameof(GetExchangeProperty), 
                        BindingFlags.NonPublic | BindingFlags.Static);
                    
                    var rootObj = SysExpression.Call(
                        getPropertyMethodInfo,
                        exchangeParam,
                        SysExpression.Constant(firstProperty));
                    
                    // Resolve the remaining path
                    var propertyCall = SysExpression.Call(
                        resolveMethodInfo,
                        rootObj,
                        SysExpression.Constant(remainingPath),
                        exchangeParam);
                    
                    return SysExpression.Convert(propertyCall, typeof(object));
                }
                else
                {
                    // Simple property
                    var getPropertyMethodInfo = typeof(ExpressionResolver).GetMethod(
                        nameof(GetExchangeProperty), 
                        BindingFlags.NonPublic | BindingFlags.Static);
                    
                    var propertyCall = SysExpression.Call(
                        getPropertyMethodInfo,
                        exchangeParam,
                        SysExpression.Constant(propertyName));
                    
                    return SysExpression.Convert(propertyCall, typeof(object));
                }
            }
            
            // Check whether the expression contains operations
            if (HasOperations(expression))
            {
                // If it contains operations, compile as a full expression
                var compiledExpression = CompileExpression(expression, exchangeParam);
                
                // Convert the result to string safely (null → empty string)
                var objExpr = SysExpression.Convert(compiledExpression, typeof(object));
                var nullCheck = SysExpression.Equal(objExpr, SysExpression.Constant(null, typeof(object)));
                var emptyString = SysExpression.Constant(string.Empty);
                var toStringMethod = typeof(object).GetMethod("ToString", Type.EmptyTypes);
                var toStringCall = SysExpression.Call(objExpr, toStringMethod!);
                
                return SysExpression.Condition(nullCheck, emptyString, toStringCall);
            }
            else
            {
                // Check whether this is an exchange variable
                // Use GetExchangeProperty which safely returns null for missing keys
                var getPropertyMethodInfo = typeof(ExpressionResolver).GetMethod(
                    nameof(GetExchangeProperty), 
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                var propertyCall = SysExpression.Call(
                    getPropertyMethodInfo,
                    exchangeParam,
                    SysExpression.Constant(expression));
                
                // Convert to string safely (null → empty string)
                var nullCheckProp = SysExpression.Equal(propertyCall, SysExpression.Constant(null, typeof(object)));
                var emptyStringProp = SysExpression.Constant(string.Empty);
                var toStringMethodProp = typeof(object).GetMethod("ToString", Type.EmptyTypes);
                var toStringCallProp = SysExpression.Call(propertyCall, toStringMethodProp!);
                
                return SysExpression.Condition(nullCheckProp, emptyStringProp, toStringCallProp);
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Error compiling template expression '{expression}': {ex.Message}");
            return SysExpression.Constant(expression);
        }
    }

    /// <summary>
    /// Compiles object property access
    /// </summary>
    private static SysExpression CompilePropertyAccess(SysExpression obj, string propertyPath)
    {
        DebugLog($"Compiling property access: '{propertyPath}'");
        var parts = propertyPath.Split('.');
        var current = obj;

        foreach (var part in parts)
        {
            DebugLog($"Processing path segment: '{part}'");
            var propertyInfo = current.Type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (propertyInfo != null)
            {
                current = SysExpression.Property(current, propertyInfo);
                DebugLog($"Found property: '{part}' of type {propertyInfo.PropertyType.Name}");
            }
            else
            {
                DebugLog($"Property not found: '{part}', returning empty string");
                // If property not found, return empty string
                return SysExpression.Constant(string.Empty);
            }
        }

        var toStringMethod = typeof(object).GetMethod(nameof(ToString));
        DebugLog($"Property access compilation completed: '{propertyPath}'");
        return SysExpression.Call(current, toStringMethod);
    }

    #endregion

    #region Resolver compilation

    /// <summary>
    /// Compiles a property resolver
    /// </summary>
    private static Func<object?, string, object?> CompilePropertyResolver(string expression)
    {
        DebugLog($"Compiling property resolver: '{expression}'");
        var objParam = SysExpression.Parameter(typeof(object), "obj");
        var pathParam = SysExpression.Parameter(typeof(string), "path");

        // For simplicity, use reflection in compiled code
        var resolveMethod = typeof(ExpressionResolver).GetMethod(nameof(ResolvePropertyPath), BindingFlags.NonPublic | BindingFlags.Static);
        var callExpression = SysExpression.Call(resolveMethod, objParam, pathParam);

        var lambda = SysExpression.Lambda<Func<object?, string, object?>>(callExpression, objParam, pathParam);
        DebugLog($"Property resolver compiled: '{expression}'");
        return lambda.Compile();
    }

    #endregion

    #region Logical expression compilation

    /// <summary>
    /// Compiles a logical expression
    /// </summary>
    private static Func<IExchange, bool> CompileLogicalExpression(string expression)
    {
        DebugLog($"Compiling logical expression: '{expression}'");
        
        var exchangeParam = SysExpression.Parameter(typeof(IExchange), "exchange");
        var body = CompileLogicalExpressionRecursive(expression, exchangeParam);
        var lambda = SysExpression.Lambda<Func<IExchange, bool>>(body, exchangeParam);
        
        var compiled = lambda.Compile();
        DebugLog($"Logical expression compiled: '{expression}'");
        return compiled;
    }
    
    /// <summary>
    /// Recursively compiles a logical expression
    /// </summary>
    private static SysExpression CompileLogicalExpressionRecursive(string expression, ParameterExpression exchangeParam)
    {
        DebugLog($"Recursive compilation: '{expression}'");

        // Handle parentheses
        if (expression.Trim().StartsWith("(") && expression.Trim().EndsWith(")"))
        {
            var innerExpression = expression.Trim().Substring(1, expression.Trim().Length - 2);
            DebugLog($"Processing parenthesized expression: '{innerExpression}'");
            return CompileLogicalExpressionRecursive(innerExpression, exchangeParam);
        }

        // For expressions that may contain logical operators (AND, OR, XOR) 
        // and complex comparisons, use CompileComplexComparison
        return CompileComplexComparison(expression, exchangeParam);
    }

    /// <summary>
    /// Compiles a compound logical expression with AND, OR, etc. operators
    /// </summary>
    private static SysExpression CompileComplexComparison(string expression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling compound expression: '{expression}'");
        
        expression = expression.Trim();
        
        // Handle parentheses
        if (expression.StartsWith("(") && expression.EndsWith(")"))
        {
            var innerExpression = expression.Substring(1, expression.Length - 2).Trim();
            DebugLog($"Processing parenthesized expression: '{innerExpression}'");
            return CompileComplexComparison(innerExpression, exchangeParam);
        }
        
        // Search for OR, AND, XOR operators in precedence order (low to high)
        // Use FindLogicalOperator for correct searching accounting for nested properties
        
        // Search for OR
        int orIndex = FindLogicalOperator(expression, "OR");
        if (orIndex >= 0)
        {
            DebugLog($"Found OR operator: left='{expression.Substring(0, orIndex)}', right='{expression.Substring(orIndex + 2).TrimStart()}'");
            
            var left = CompileComplexComparison(expression.Substring(0, orIndex).Trim(), exchangeParam);
            var right = CompileComplexComparison(expression.Substring(orIndex + 2).TrimStart(), exchangeParam);
            
            return SysExpression.OrElse(left, right);
        }
        
        // Search for AND
        int andIndex = FindLogicalOperator(expression, "AND");
        if (andIndex >= 0)
        {
            DebugLog($"Found AND operator: left='{expression.Substring(0, andIndex)}', right='{expression.Substring(andIndex + 3).TrimStart()}'");
            
            var left = CompileComplexComparison(expression.Substring(0, andIndex).Trim(), exchangeParam);
            var right = CompileComplexComparison(expression.Substring(andIndex + 3).TrimStart(), exchangeParam);
            
            return SysExpression.AndAlso(left, right);
        }
        
        // Search for XOR
        int xorIndex = FindLogicalOperator(expression, "XOR");
        if (xorIndex >= 0)
        {
            DebugLog($"Found XOR operator: left='{expression.Substring(0, xorIndex)}', right='{expression.Substring(xorIndex + 3).TrimStart()}'");
            
            var left = CompileComplexComparison(expression.Substring(0, xorIndex).Trim(), exchangeParam);
            var right = CompileComplexComparison(expression.Substring(xorIndex + 3).TrimStart(), exchangeParam);
            
            // XOR is implemented as (left OR right) AND NOT (left AND right)
            var leftOrRight = SysExpression.OrElse(left, right);
            var leftAndRight = SysExpression.AndAlso(left, right);
            var notLeftAndRight = SysExpression.Not(leftAndRight);
            
            return SysExpression.AndAlso(leftOrRight, notLeftAndRight);
        }
        
        // Handle NOT operator
        if (expression.StartsWith("NOT "))
        {
            DebugLog($"Found NOT operator: '{expression.Substring(4)}'");
            
            var innerExpr = CompileComplexComparison(expression.Substring(4), exchangeParam);
            return SysExpression.Not(innerExpr);
        }
        
        // Search for comparison operators (==, !=, >, <, >=, <=)
        foreach (var op in new[] { "==", "!=", ">=", "<=", ">", "<" })
        {
            int index = FindComparisonOperator(expression, op);
            if (index >= 0)
            {
                DebugLog($"Found comparison operator: '{op}'");
                string leftExpr = expression.Substring(0, index).Trim();
                string rightExpr = expression.Substring(index + op.Length).Trim();
                DebugLog($"Left side: '{leftExpr}', right side: '{rightExpr}'");
                
                // Check whether the left side contains a property, header, or body prefix
                bool isPropertyAccess = leftExpr.StartsWith("property.") || rightExpr.StartsWith("property.") ||
                                        leftExpr.StartsWith("header.") || rightExpr.StartsWith("header.") ||
                                        leftExpr.StartsWith("body.") || rightExpr.StartsWith("body.");
                
                if (isPropertyAccess)
                {
                    DebugLog("Detected property access, using CompileBinaryComparison");
                    // Use a specialized method for binary comparison compilation
                    return CompileBinaryComparison(leftExpr, op, rightExpr, exchangeParam);
                }
                else
                {
                    DebugLog("Regular comparison, compiling directly");
                    var leftExprValue = CompileValueGetter(leftExpr, exchangeParam);
                    var rightExprValue = CompileValueGetter(rightExpr, exchangeParam);
                    
                    return CreateComparisonExpression(leftExprValue, rightExprValue, op);
                }
            }
        }
        
        // If no comparison operators found, check if this is a property access
        if (expression.StartsWith("property.") || expression.StartsWith("header.") || expression.StartsWith("body."))
        {
            DebugLog($"Compiling property/header/body access: '{expression}'");
            var valueExpression = CompileNestedPropertyAccess(expression, exchangeParam);
            
            // Convert to bool
            return SysExpression.Call(
                typeof(ExpressionResolver).GetMethod(
                    nameof(ConvertToBoolExpression), 
                    BindingFlags.NonPublic | BindingFlags.Static),
                SysExpression.Convert(valueExpression, typeof(object)));
        }
        
        // If no comparison operators, this is just a value
        DebugLog($"Simple expression without operators: '{expression}'");
        
        // Only allow known identifiers and boolean/numeric literals in logical context.
        // Arbitrary strings like "~~~INVALID~~~" must not silently evaluate to true.
        var trimmedExpr = expression.Trim();
        if (!string.Equals(trimmedExpr, "true", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(trimmedExpr, "false", StringComparison.OrdinalIgnoreCase)
            && !trimmedExpr.StartsWith("body")
            && !trimmedExpr.StartsWith("header.")
            && !trimmedExpr.StartsWith("property.")
            && !trimmedExpr.StartsWith("contentType")
            && !double.TryParse(trimmedExpr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)
            && !trimmedExpr.StartsWith("jpath(")
            && !trimmedExpr.StartsWith("xpath("))
        {
            throw new ExpressionCompilationException(
                $"Unrecognized logical expression: '{expression}'. " +
                "Expected a comparison (e.g. 'property.x > 0'), a boolean literal, or a property/header/body accessor.");
        }
        
        var simpleValueExpression = CompileValueGetter(expression, exchangeParam);
        
        // Convert to bool
        return SysExpression.Call(
            typeof(ExpressionResolver).GetMethod(
                nameof(ConvertToBoolExpression), 
                BindingFlags.NonPublic | BindingFlags.Static),
            SysExpression.Convert(simpleValueExpression, typeof(object)));
    }

    /// <summary>
    /// Compiles a logical expression of the form "property.obj1.field1 operator property.obj2.field2"
    /// </summary>
    private static SysExpression CompileBinaryComparison(string leftExpr, string operatorStr, string rightExpr, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling binary comparison: '{leftExpr} {operatorStr} {rightExpr}'");
        
        // Compile the left and right sides of the expression
        var leftValueExpr = CompileValueGetter(leftExpr, exchangeParam);
        var rightValueExpr = CompileValueGetter(rightExpr, exchangeParam);
        
        // Get the comparison method based on the operator
        var compareMethod = typeof(ExpressionResolver).GetMethod(
            nameof(CompareExpressionValues),
            BindingFlags.NonPublic | BindingFlags.Static);
        
        // Create a comparison method call with values and the operator
        return SysExpression.Call(
            compareMethod,
            SysExpression.Convert(leftValueExpr, typeof(object)),
            SysExpression.Convert(rightValueExpr, typeof(object)),
            SysExpression.Constant(operatorStr));
    }

    #endregion

    #region Property access compilation

    /// <summary>
    /// Compiles nested property access (e.g. property.customer.Id)
    /// </summary>
    private static SysExpression CompileNestedPropertyAccess(string expression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling nested property access: '{expression}'");
        
        // Determine the access type (property, header, body)
        string prefix = null;
        string path = expression;
        
        if (expression.StartsWith("property."))
        {
            prefix = "property";
            path = expression.Substring(PROPERTY_PREFIX.Length);
        }
        else if (expression.StartsWith("header."))
        {
            prefix = "header";
            path = expression.Substring(HEADER_PREFIX.Length);
        }
        else if (expression.StartsWith("body."))
        {
            prefix = "body";
            path = expression.Substring(BODY_PREFIX.Length);
        }
        
        // Smart resolvers for property and header with dots (priority: literal name → nesting)
        if (prefix == "property" && path.Contains('.'))
        {
            DebugLog($"Using smart resolution for dotted property: '{path}'");
            
            var resolvePropertySmartMethod = typeof(ExpressionResolver).GetMethod(
                nameof(ResolvePropertySmart), 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            if (resolvePropertySmartMethod == null)
            {
                throw new ExpressionCompilationException($"Failed to find ResolvePropertySmart method for: {expression}");
            }
            
            return SysExpression.Call(
                resolvePropertySmartMethod,
                exchangeParam,
                SysExpression.Constant(path));
        }
        
        if (prefix == "header" && path.Contains('.'))
        {
            DebugLog($"Using smart resolution for dotted header: '{path}'");
            
            var resolveHeaderSmartMethod = typeof(ExpressionResolver).GetMethod(
                nameof(ResolveHeaderSmart), 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            if (resolveHeaderSmartMethod == null)
            {
                throw new ExpressionCompilationException($"Failed to find ResolveHeaderSmart method for: {expression}");
            }
            
            return SysExpression.Call(
                resolveHeaderSmartMethod,
                exchangeParam,
                SysExpression.Constant(path));
        }
        
        // If this is a nested path for body (property and header handled above)
        if (prefix == "body" && path.Contains('.'))
        {
            DebugLog($"Detected nested path for {prefix}: '{path}'");
            
            // Get the method for property path resolution
            var resolveMethodInfo = typeof(ExpressionResolver).GetMethod(
                nameof(ResolvePropertyPathWithExchange), 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            if (resolveMethodInfo == null)
            {
                throw new ExpressionCompilationException($"Failed to find method for nested path resolution: {expression}");
            }
            
            // Get the root object (body)
            DebugLog("Getting value from body");
            var rootObjExpr = SysExpression.Call(
                SysExpression.Property(exchangeParam, typeof(IExchange).GetProperty("In")),
                typeof(IMessage).GetMethod("getBody", Type.EmptyTypes));
            
            // Determine the remaining path after the first dot
            var dotIndex = path.IndexOf('.');
            var remainingPath = path.Substring(dotIndex + 1);
            DebugLog($"Remaining path: '{remainingPath}'");
            
            // Create a method call for resolving the remaining path segment
            return SysExpression.Call(
                resolveMethodInfo,
                rootObjExpr,
                SysExpression.Constant(remainingPath),
                exchangeParam);
        }
        
        // If this is a simple property access, use the standard method
        DebugLog($"Using standard method for property access: '{expression}'");
        return CompileValueGetter(expression, exchangeParam);
    }

    #endregion

    #region Value expression compilation

    /// <summary>
    /// Compiles an expression for value retrieval
    /// </summary>
    private static Func<IExchange, object?> CompileValueExpression(string expression)
    {
        DebugLog($"Compiling value expression: '{expression}'");
        
        try 
        {
            // Route expressions with operators, function calls, or special syntax through AST
            if (PostfixIncrementDecrementRegex.IsMatch(expression) || PrefixIncrementDecrementRegex.IsMatch(expression)
                || expression.Contains("??") || ContainsTernary(expression)
                || HasFunctionCalls(expression) || HasIndexAccess(expression)
                || expression.Contains(" AND ") || expression.Contains(" OR ") || expression.Contains(" XOR ")
                || expression.Contains(" == ") || expression.Contains(" != ")
                || expression.Contains(" > ") || expression.Contains(" < ")
                || expression.Contains(" >= ") || expression.Contains(" <= ")
                || expression.StartsWith("NOT ", StringComparison.OrdinalIgnoreCase)
                || expression.StartsWith("!", StringComparison.Ordinal)
                || expression.StartsWith("-", StringComparison.Ordinal)
                || expression.StartsWith("+", StringComparison.Ordinal))
            {
                DebugLog($"Detected operators/functions, using AST parser");
                return CompileExpressionWithAst(expression);
            }
            
            // For simple expressions (property lookups, literals), use legacy compilation
            var exchangeParam = SysExpression.Parameter(typeof(IExchange), "exchange");
            var valueExpr = CompileExpression(expression, exchangeParam);
            var lambda = SysExpression.Lambda<Func<IExchange, object?>>(valueExpr, exchangeParam);
            DebugLog($"Value expression compiled: '{expression}'");
            return lambda.Compile();
        }
        catch (Exception ex)
        {
            DebugLog($"Error compiling expression '{expression}', using AST parser as fallback: {ex.Message}");
            return CompileExpressionWithAst(expression);
        }
    }

    /// <summary>
    /// Compiles a value getter expression
    /// </summary>
    private static SysExpression CompileValueGetter(string expression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling value getter: '{expression}'");

        // Handle parenthesized expressions — recursive parsing
        if (expression.Trim().StartsWith("(") && expression.Trim().EndsWith(")"))
        {
            var trimmedExpression = expression.Trim();
            var innerExpression = trimmedExpression.Substring(1, trimmedExpression.Length - 2).Trim();
            DebugLog($"Detected parenthesized expression: '{innerExpression}'");
            
            // Check for operations inside parentheses
            if (HasOperations(innerExpression))
            {
                DebugLog($"Inner expression contains operations, compiling: '{innerExpression}'");
                // Recursively compile the inner expression as a sub-expression
                var result = CompileExpression(innerExpression, exchangeParam);
                DebugLog($"Parenthesized expression compilation result:");
                DebugPrintExpressionTree(result);
                return result;
            }
            else
            {
                // Simple parenthesized expression
                var result = CompileValueGetter(innerExpression, exchangeParam);
                DebugLog($"Simple parenthesized expression compilation result:");
                DebugPrintExpressionTree(result);
                return result;
            }
        }

        // Check for jpath function
        var jpathMatch = JPathFunctionRegex.Match(expression.Trim());
        if (jpathMatch.Success)
        {
            DebugLog($"Detected jpath function in expression, using CompileJPathExpression");
            // Use specialized method for compiling jpath expressions
            var result = CompileJPathExpression(expression.Trim(), exchangeParam);
            DebugLog($"JPath expression compilation result:");
            DebugPrintExpressionTree(result);
            return result;
        }

        // Check for xpath function
        var xpathMatchVal = XPathFunctionRegex.Match(expression.Trim());
        if (xpathMatchVal.Success)
        {
            DebugLog($"Detected xpath function in expression, using CompileXPathExpression");
            var result = CompileXPathExpression(expression.Trim(), exchangeParam);
            DebugLog($"XPath expression compilation result:");
            DebugPrintExpressionTree(result);
            return result;
        }

        // Check for prefix increment/decrement operations (++x, --x)
        var prefixMatch = PrefixIncrementDecrementRegex.Match(expression.Trim());
        if (prefixMatch.Success)
        {
            var op = prefixMatch.Groups[1].Value;
            var innerExpression = prefixMatch.Groups[2].Value.Trim();
            DebugLog($"Detected prefix operation: '{op}' for '{innerExpression}'");
            var result = CompilePrefixIncrementDecrement(op, innerExpression, exchangeParam);
            DebugLog($"Prefix operation compilation result:");
            DebugPrintExpressionTree(result);
            return result;
        }
        
        // Check for postfix increment/decrement operations (x++, x--)
        var postfixMatch = PostfixIncrementDecrementRegex.Match(expression.Trim());
        if (postfixMatch.Success)
        {
            var innerExpression = postfixMatch.Groups[1].Value.Trim();
            var op = postfixMatch.Groups[2].Value;
            DebugLog($"Detected postfix operation: '{op}' for '{innerExpression}'");
            
            // Important: postfix operations need special compilation handling
            var result = CompilePostfixIncrementDecrement(op, innerExpression, exchangeParam);
            DebugLog($"Postfix operation compilation result:");
            DebugPrintExpressionTree(result);
            return result;
        }

        // Check for unary operations (!x, +x, -x)
        var unaryMatch = UnaryOperationRegex.Match(expression.Trim());
        if (unaryMatch.Success)
        {
            var unaryOp = unaryMatch.Groups[1].Value;
            var innerExpression = unaryMatch.Groups[2].Value.Trim();
            DebugLog($"Detected unary operation: '{unaryOp}' for '{innerExpression}'");
            var result = CompileUnaryOperation(unaryOp, innerExpression, exchangeParam);
            DebugLog($"Unary operation compilation result:");
            DebugPrintExpressionTree(result);
            return result;
        }

        // First check for null literal
        if (string.Equals(expression, "null", StringComparison.OrdinalIgnoreCase))
        {
            DebugLog($"Detected null literal: '{expression}'");
            return SysExpression.Constant(null, typeof(object));
        }

        // Handle remaining literals
        var parsedValue = ParseLiteral(expression);
        if (!ReferenceEquals(parsedValue, expression)) // If the value was parsed (changed)
        {
            DebugLog($"Detected literal: '{expression}' -> {parsedValue}");
            return SysExpression.Constant(parsedValue, typeof(object));
        }

        // Handle body and header expressions
        if (expression.StartsWith("body."))
        {
            DebugLog($"Processing body expression: '{expression}'");
            var propertyPath = expression.Substring(BODY_PREFIX.Length);
            DebugLog($"Resolving body property via runtime reflection: '{propertyPath}'");
            
            // Use runtime reflection to access body properties
            var resolveMethodInfo = typeof(ExpressionResolver).GetMethod(
                nameof(ResolveBodyProperty), 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            return SysExpression.Call(
                resolveMethodInfo,
                exchangeParam,
                SysExpression.Constant(propertyPath));
        }

        if (expression == "body")
        {
            DebugLog($"Processing body expression (entire object): '{expression}'");
            var inProperty = SysExpression.Property(exchangeParam, nameof(IExchange.In));
            var getBodyMethod = typeof(IMessage).GetMethods()
                .FirstOrDefault(m => m.Name == "getBody" && !m.IsGenericMethod);
            var bodyValue = SysExpression.Call(inProperty, getBodyMethod!);
            return SysExpression.Convert(bodyValue, typeof(object));
        }

        if (expression == "contentType")
        {
            DebugLog($"Processing contentType expression");
            var inProperty = SysExpression.Property(exchangeParam, nameof(IExchange.In));
            var contentTypeProperty = SysExpression.Property(inProperty, nameof(IMessage.ContentType));
            return SysExpression.Convert(contentTypeProperty, typeof(object));
        }

        if (expression.StartsWith("header."))
        {
            var headerName = expression.Substring(HEADER_PREFIX.Length);
            DebugLog($"Processing header: '{headerName}'");
            
            // Check whether headerName contains dots indicating nested properties
            if (headerName.Contains('.'))
            {
                DebugLog($"Detected nested header: '{headerName}'");
                
                var resolveHeaderSmartMethod = typeof(ExpressionResolver).GetMethod(
                    nameof(ResolveHeaderSmart), 
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                return SysExpression.Call(
                    resolveHeaderSmartMethod,
                    exchangeParam,
                    SysExpression.Constant(headerName));
            }
            
            // Simple header — use ResolveHeaderSmart for safe TryGetValue
            var resolveMethodSimple = typeof(ExpressionResolver).GetMethod(
                nameof(ResolveHeaderSmart),
                BindingFlags.NonPublic | BindingFlags.Static);
            
            return SysExpression.Call(
                resolveMethodSimple,
                exchangeParam,
                SysExpression.Constant(headerName));
        }

        // Handle property. expressions
        if (expression.StartsWith("property."))
        {
            var propertyName = expression.Substring(PROPERTY_PREFIX.Length);
            DebugLog($"Processing property: '{propertyName}'");
            
            // Check whether propertyName contains dots indicating nested properties
            if (propertyName.Contains('.'))
            {
                DebugLog($"Detected nested property: '{propertyName}'");
                
                // Use ResolvePropertyPathWithExchange for complex path resolution
                var resolveMethodInfo = typeof(ExpressionResolver).GetMethod(
                    nameof(ResolvePropertyPathWithExchange), 
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                // Find the first path segment (before the first dot)
                var firstDotIndex = propertyName.IndexOf('.');
                var firstProperty = propertyName.Substring(0, firstDotIndex);
                var remainingPath = propertyName.Substring(firstDotIndex + 1);
                
                // Get the root object
                var getPropertyMethodInfo = typeof(ExpressionResolver).GetMethod(
                    nameof(GetExchangeProperty), 
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                var rootObj = SysExpression.Call(
                    getPropertyMethodInfo,
                    exchangeParam,
                    SysExpression.Constant(firstProperty));
                
                // Resolve the remaining path
                var propertyCall = SysExpression.Call(
                    resolveMethodInfo,
                    rootObj,
                    SysExpression.Constant(remainingPath),
                    exchangeParam);
                
                return SysExpression.Convert(propertyCall, typeof(object));
            }
            else
            {
                // Simple property
                var getPropertyMethodInfo = typeof(ExpressionResolver).GetMethod(
                    nameof(GetExchangeProperty), 
                    BindingFlags.NonPublic | BindingFlags.Static);
                
                var propertyCall = SysExpression.Call(
                    getPropertyMethodInfo,
                    exchangeParam,
                    SysExpression.Constant(propertyName));
                
                return SysExpression.Convert(propertyCall, typeof(object));
            }
        }
        
        // Check for postfix or prefix operations that may have been missed
        if (expression.Contains("++") || expression.Contains("--"))
        {
            DebugLog($"Expression contains ++ or --, using AST parser: '{expression}'");
            // Create a GetCompiledValueExpressionWithAst call
            var astMethod = typeof(ExpressionResolver).GetMethod(
                nameof(GetCompiledValueExpressionWithAst), 
                BindingFlags.Public | BindingFlags.Static);
            
            // Compile a call to that method, passing expression
            var astCompiled = SysExpression.Call(
                astMethod,
                SysExpression.Constant(expression));
            
            // Invoke the obtained delegate with exchangeParam
            return SysExpression.Call(
                astCompiled,
                typeof(Func<IExchange, object>).GetMethod("Invoke"),
                exchangeParam);
        }
        
        // By default, return a constant
        DebugLog($"Returning default constant: '{expression}'");
        return SysExpression.Constant(expression, typeof(object));
    }

    /// <summary>
    /// Compiles an expression for value computation
    /// </summary>
    /// <param name="expression">String representation of the expression</param>
    /// <param name="exchangeParam">The exchange parameter expression</param>
    /// <returns>A compiled expression tree</returns>
    private static SysExpression CompileExpression(string expression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling expression: '{expression}'");
        
        // Handle parentheses
        if (expression.Trim().StartsWith("(") && expression.Trim().EndsWith(")"))
        {
            var trimmedExpression = expression.Trim();
            // Verify these are outer parentheses, not part of an inner expression
            int depth = 0;
            bool hasOuterBrackets = true;
            
            for (int i = 0; i < trimmedExpression.Length - 1; i++)
            {
                if (trimmedExpression[i] == '(') depth++;
                else if (trimmedExpression[i] == ')') depth--;
                
                // If depth reaches 0 before the last parenthesis, these are not outer parentheses
                if (depth == 0 && i < trimmedExpression.Length - 1)
                {
                    hasOuterBrackets = false;
                    break;
                }
            }
            
            if (hasOuterBrackets)
            {
                var innerExpression = trimmedExpression.Substring(1, trimmedExpression.Length - 2);
                DebugLog($"Detected parenthesized expression: '{innerExpression}'");
                return CompileExpression(innerExpression, exchangeParam);
            }
        }
        
        // Check for low-priority operations (+ and -)
        var addIndex = FindLastOperatorOutsideBrackets(expression, '+');
        var subIndex = FindLastOperatorOutsideBrackets(expression, '-');
        
        // Select the last + or - operator outside parentheses
        int lowPriorityIndex = Math.Max(addIndex, subIndex);
        
        if (lowPriorityIndex > 0)
        {
            string op = expression[lowPriorityIndex].ToString();
            string left = expression.Substring(0, lowPriorityIndex).Trim();
            string right = expression.Substring(lowPriorityIndex + 1).Trim();
            
            DebugLog($"Detected low-priority binary operation: '{left}' {op} '{right}'");
            
            // Compile the left and right parts
            var leftExpr = CompileExpression(left, exchangeParam);
            var rightExpr = CompileExpression(right, exchangeParam);
            
            // Convert expressions to object?
            var leftObj = SysExpression.Convert(leftExpr, typeof(object));
            var rightObj = SysExpression.Convert(rightExpr, typeof(object));
            
            // Select the method for the operation
            var methodName = op == "+" ? "ApplyAddition" : "ApplySubtraction";
            var applyMethod = typeof(ExpressionResolver).GetMethod(methodName, 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            return SysExpression.Call(applyMethod, leftObj, rightObj);
        }
        
        // Check for high-priority operations (* and /)
        var mulIndex = FindLastOperatorOutsideBrackets(expression, '*');
        var divIndex = FindLastOperatorOutsideBrackets(expression, '/');
        
        // Select the last * or / operator outside parentheses
        int highPriorityIndex = Math.Max(mulIndex, divIndex);
        
        if (highPriorityIndex > 0)
        {
            string op = expression[highPriorityIndex].ToString();
            string left = expression.Substring(0, highPriorityIndex).Trim();
            string right = expression.Substring(highPriorityIndex + 1).Trim();
            
            DebugLog($"Detected high-priority binary operation: '{left}' {op} '{right}'");
            
            // Compile the left and right parts
            var leftExpr = CompileExpression(left, exchangeParam);
            var rightExpr = CompileExpression(right, exchangeParam);
            
            // Convert expressions to object?
            var leftObj = SysExpression.Convert(leftExpr, typeof(object));
            var rightObj = SysExpression.Convert(rightExpr, typeof(object));
            
            // Select the method for the operation
            var methodName = op == "*" ? "ApplyMultiplication" : "ApplyDivision";
            var applyMethod = typeof(ExpressionResolver).GetMethod(methodName, 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            return SysExpression.Call(applyMethod, leftObj, rightObj);
        }
        
        // If no operations found, simply compile the value
        return CompileValueGetter(expression, exchangeParam);
    }

    #endregion

    #region Operation compilation

    /// <summary>
    /// Compiles a unary operation
    /// </summary>
    private static SysExpression CompileUnaryOperation(string unaryOp, string innerExpression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling unary operation: '{unaryOp}' for '{innerExpression}'");
        
        // Check whether the expression contains a property, header, or body prefix
        if (innerExpression.StartsWith("property.") || innerExpression.StartsWith("header.") || innerExpression.Contains("."))
        {
            // First get the property value
            var valueExpr = CompileValueGetter(innerExpression, exchangeParam);
            
            // Then apply the unary operation to the retrieved value
            return unaryOp switch
            {
                "!" => CompileUnaryNot(valueExpr),
                "+" => CompileUnaryPlus(valueExpr),
                "-" => CompileUnaryMinus(valueExpr),
                _ => throw new NotSupportedException($"Unsupported unary operator: {unaryOp}")
            };
        }
        else
        {
            // For simple expressions without prefixes
            // Get the inner expression
            var valueExpr = CompileValueGetter(innerExpression, exchangeParam);
            
            return unaryOp switch
            {
                "!" => CompileUnaryNot(valueExpr),
                "+" => CompileUnaryPlus(valueExpr),
                "-" => CompileUnaryMinus(valueExpr),
                _ => throw new NotSupportedException($"Unsupported unary operator: {unaryOp}")
            };
        }
    }

    /// <summary>
    /// Compiles unary logical negation (!) for boolean values
    /// </summary>
    private static SysExpression CompileUnaryNot(SysExpression valueExpr)
    {
        DebugLog("Compiling unary logical negation (!)");
        
        // Simpler approach with direct method invocation
        var convertAndNotMethod = typeof(ExpressionResolver).GetMethod(nameof(ApplyUnaryNot), BindingFlags.NonPublic | BindingFlags.Static);
        return SysExpression.Call(convertAndNotMethod, SysExpression.Convert(valueExpr, typeof(object)));
    }

    /// <summary>
    /// Compiles unary plus (+) for numeric values and string/collection concatenation
    /// </summary>
    private static SysExpression CompileUnaryPlus(SysExpression valueExpr)
    {
        DebugLog("Compiling unary plus (+)");
        
        // Method for handling unary plus
        var unaryPlusMethod = typeof(ExpressionResolver).GetMethod(nameof(ApplyUnaryPlus), BindingFlags.NonPublic | BindingFlags.Static);
        return SysExpression.Call(unaryPlusMethod, SysExpression.Convert(valueExpr, typeof(object)));
    }

    /// <summary>
    /// Compiles unary minus (-) for numeric values
    /// </summary>
    private static SysExpression CompileUnaryMinus(SysExpression valueExpr)
    {
        DebugLog("Compiling unary minus (-)");
        
        // Method for handling unary minus
        var unaryMinusMethod = typeof(ExpressionResolver).GetMethod(nameof(ApplyUnaryMinus), BindingFlags.NonPublic | BindingFlags.Static);
        return SysExpression.Call(unaryMinusMethod, SysExpression.Convert(valueExpr, typeof(object)));
    }

    /// <summary>
    /// Compiles prefix increment/decrement (++x or --x)
    /// </summary>
    private static SysExpression CompilePrefixIncrementDecrement(string op, string innerExpression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling prefix {op} for '{innerExpression}'");
        
        // Do not strip property, header, or body prefixes since they are now handled
        // in the Apply* methods
        string actualPropertyName = innerExpression;
        DebugLog($"Using full property name: '{actualPropertyName}'");
        
        // Method for handling prefix increment/decrement
        var method = op == "++" 
            ? typeof(ExpressionResolver).GetMethod(nameof(ApplyPrefixIncrement), BindingFlags.NonPublic | BindingFlags.Static)
            : typeof(ExpressionResolver).GetMethod(nameof(ApplyPrefixDecrement), BindingFlags.NonPublic | BindingFlags.Static);
        
        return SysExpression.Call(method, exchangeParam, SysExpression.Constant(actualPropertyName));
    }
    
    /// <summary>
    /// Compiles postfix increment/decrement (x++ or x--)
    /// </summary>
    private static SysExpression CompilePostfixIncrementDecrement(string op, string innerExpression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling postfix {op} for '{innerExpression}'");
        
        // Do not strip property, header, or body prefixes since they are now handled
        // in the Apply* methods
        string actualPropertyName = innerExpression;
        DebugLog($"Using full property name: '{actualPropertyName}'");
        
        // Method for handling postfix increment/decrement
        var method = op == "++" 
            ? typeof(ExpressionResolver).GetMethod(nameof(ApplyPostfixIncrement), BindingFlags.NonPublic | BindingFlags.Static)
            : typeof(ExpressionResolver).GetMethod(nameof(ApplyPostfixDecrement), BindingFlags.NonPublic | BindingFlags.Static);
        
        return SysExpression.Call(method, exchangeParam, SysExpression.Constant(actualPropertyName));
    }

    /// <summary>
    /// Compiles a binary operation (x + y, x - y, x * y, x / y)
    /// </summary>
    private static SysExpression CompileBinaryOperation(string op, string leftExpression, string rightExpression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling binary operation: '{leftExpression}' {op} '{rightExpression}'");
        
        // Get the left and right expressions
        var leftExpr = CompileValueGetter(leftExpression, exchangeParam);
        DebugLog($"Binary operation left side:");
        DebugPrintExpressionTree(leftExpr);
        
        var rightExpr = CompileValueGetter(rightExpression, exchangeParam);
        DebugLog($"Binary operation right side:");
        DebugPrintExpressionTree(rightExpr);
        
        // Method for handling binary operations
        var method = op switch
        {
            "+" => typeof(ExpressionResolver).GetMethod(nameof(ApplyAddition), BindingFlags.NonPublic | BindingFlags.Static),
            "-" => typeof(ExpressionResolver).GetMethod(nameof(ApplySubtraction), BindingFlags.NonPublic | BindingFlags.Static),
            "*" => typeof(ExpressionResolver).GetMethod(nameof(ApplyMultiplication), BindingFlags.NonPublic | BindingFlags.Static),
            "/" => typeof(ExpressionResolver).GetMethod(nameof(ApplyDivision), BindingFlags.NonPublic | BindingFlags.Static),
            _ => throw new NotSupportedException($"Unsupported binary operation: {op}")
        };
        
        var result = SysExpression.Call(
            method, 
            SysExpression.Convert(leftExpr, typeof(object)), 
            SysExpression.Convert(rightExpr, typeof(object))
        );
        
        DebugLog($"Binary operation compilation result '{op}':");
        DebugPrintExpressionTree(result);
        
        return result;
    }

    /// <summary>
    /// Compiles a jpath expression with a dynamic path
    /// </summary>
    private static SysExpression CompileJPathExpression(string expression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling jpath expression: '{expression}'");
        
        // Extract the argument from the jpath(...) expression
        var match = JPathFunctionRegex.Match(expression);
        if (!match.Success)
        {
            DebugLog($"Invalid jpath expression format: '{expression}'");
            return SysExpression.Constant(null);
        }
        
        var pathArg = match.Groups[1].Value.Trim();
        DebugLog($"JPath argument: '{pathArg}'");
        
        // If the argument contains a concatenation operation ("+")
        if (pathArg.Contains("+"))
        {
            DebugLog($"Detected concatenation in jpath argument: '{pathArg}'");
            
            // Compile the concatenation expression
            var concatExpression = CompileExpression(pathArg, exchangeParam);
            
            // Convert the result to string for use in JsonPath
            var toStringMethod = typeof(object).GetMethod("ToString");
            var pathStringExpr = SysExpression.Call(
                SysExpression.Convert(concatExpression, typeof(object)),
                toStringMethod);
            
            // Create an ApplyJPath method call with a dynamic path
            var applyJPathMethod = typeof(ExpressionResolver).GetMethod("ApplyJPath", 
                BindingFlags.NonPublic | BindingFlags.Static);
                
            return SysExpression.Call(
                applyJPathMethod,
                exchangeParam,
                pathStringExpr);
        }
        // Check whether the argument is a string literal
        else if ((pathArg.StartsWith("'") && pathArg.EndsWith("'")) || 
            (pathArg.StartsWith("\"") && pathArg.EndsWith("\"")))
        {
            // This is a string literal, create a constant path
            var jsonPath = pathArg.Substring(1, pathArg.Length - 2);
            DebugLog($"Direct jpath path: '{jsonPath}'");
            
            // Create an ApplyJPath method call with a constant path
            var applyJPathMethod = typeof(ExpressionResolver).GetMethod("ApplyJPath", 
                BindingFlags.NonPublic | BindingFlags.Static);
                
            return SysExpression.Call(
                applyJPathMethod,
                exchangeParam,
                SysExpression.Constant(jsonPath));
        }
        else if (HasOperations(pathArg))
        {
            // If the argument contains other operations, compile it as an expression
            DebugLog($"JPath argument contains operations: '{pathArg}'");
            
            // Compile the argument as an expression
            var argExpression = CompileExpression(pathArg, exchangeParam);
            
            // Convert the expression result to string
            var toStringMethod = typeof(object).GetMethod("ToString");
            var pathStringExpr = SysExpression.Call(
                SysExpression.Convert(argExpression, typeof(object)),
                toStringMethod);
            
            // Create an ApplyJPath method call with a dynamic path
            var applyJPathMethod = typeof(ExpressionResolver).GetMethod("ApplyJPath", 
                BindingFlags.NonPublic | BindingFlags.Static);
                
            return SysExpression.Call(
                applyJPathMethod,
                exchangeParam,
                pathStringExpr);
        }
        else
        {
            // Regular variable or property
            DebugLog($"JPath argument is a variable: '{pathArg}'");
            
            // Get the variable value from the exchange
            var valueExpression = CompileValueGetter(pathArg, exchangeParam);
            
            // Convert the result to string
            var toStringMethod = typeof(object).GetMethod("ToString");
            var pathStringExpr = SysExpression.Call(
                SysExpression.Convert(valueExpression, typeof(object)),
                toStringMethod);
            
            // Create an ApplyJPath method call with a dynamic path
            var applyJPathMethod = typeof(ExpressionResolver).GetMethod("ApplyJPath", 
                BindingFlags.NonPublic | BindingFlags.Static);
                
            return SysExpression.Call(
                applyJPathMethod,
                exchangeParam,
                pathStringExpr);
        }
    }

    /// <summary>
    /// Compiles an xpath expression with a dynamic path.
    /// </summary>
    private static SysExpression CompileXPathExpression(string expression, ParameterExpression exchangeParam)
    {
        DebugLog($"Compiling xpath expression: '{expression}'");
        
        var match = XPathFunctionRegex.Match(expression);
        if (!match.Success)
        {
            DebugLog($"Invalid xpath expression format: '{expression}'");
            return SysExpression.Constant(null);
        }
        
        var pathArg = match.Groups[1].Value.Trim();
        DebugLog($"XPath argument: '{pathArg}'");
        
        var applyXPathMethod = typeof(ExpressionResolver).GetMethod("ApplyXPath", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        if (pathArg.Contains("+"))
        {
            DebugLog($"Detected concatenation in xpath argument: '{pathArg}'");
            var concatExpression = CompileExpression(pathArg, exchangeParam);
            var toStringMethod = typeof(object).GetMethod("ToString");
            var pathStringExpr = SysExpression.Call(
                SysExpression.Convert(concatExpression, typeof(object)),
                toStringMethod);
            
            return SysExpression.Call(applyXPathMethod, exchangeParam, pathStringExpr);
        }
        else if ((pathArg.StartsWith("'") && pathArg.EndsWith("'")) || 
            (pathArg.StartsWith("\"") && pathArg.EndsWith("\"")))
        {
            var xpathQuery = pathArg.Substring(1, pathArg.Length - 2);
            DebugLog($"Direct xpath path: '{xpathQuery}'");
            return SysExpression.Call(applyXPathMethod, exchangeParam, SysExpression.Constant(xpathQuery));
        }
        else if (IsXPathLiteral(pathArg))
        {
            // XPath paths like /root/child, //descendant, .//node, @attr contain '/' which
            // HasOperations misinterprets as arithmetic division. Treat them as literal paths.
            DebugLog($"XPath literal path: '{pathArg}'");
            return SysExpression.Call(applyXPathMethod, exchangeParam, SysExpression.Constant(pathArg));
        }
        else if (HasOperations(pathArg))
        {
            DebugLog($"XPath argument contains operations: '{pathArg}'");
            var argExpression = CompileExpression(pathArg, exchangeParam);
            var toStringMethod = typeof(object).GetMethod("ToString");
            var pathStringExpr = SysExpression.Call(
                SysExpression.Convert(argExpression, typeof(object)),
                toStringMethod);
            
            return SysExpression.Call(applyXPathMethod, exchangeParam, pathStringExpr);
        }
        else
        {
            DebugLog($"XPath argument is a variable: '{pathArg}'");
            var valueExpression = CompileValueGetter(pathArg, exchangeParam);
            var toStringMethod = typeof(object).GetMethod("ToString");
            var pathStringExpr = SysExpression.Call(
                SysExpression.Convert(valueExpression, typeof(object)),
                toStringMethod);
            
            return SysExpression.Call(applyXPathMethod, exchangeParam, pathStringExpr);
        }
    }

    /// <summary>
    /// Creates a comparison expression
    /// </summary>
    private static SysExpression CreateComparisonExpression(SysExpression left, SysExpression right, string operatorType)
    {
        DebugLog($"Creating comparison expression with operator: '{operatorType}'");
        // Cast to object for universal comparison
        var leftObj = SysExpression.Convert(left, typeof(object));
        var rightObj = SysExpression.Convert(right, typeof(object));

        var compareMethod = typeof(ExpressionResolver).GetMethod(nameof(CompareValues), BindingFlags.NonPublic | BindingFlags.Static);
        var compareCall = SysExpression.Call(compareMethod, leftObj, rightObj, SysExpression.Constant(operatorType));

        return compareCall;
    }

    #endregion

    #region Operator search helper methods

    /// <summary>
    /// Finds the position of a logical operator accounting for parentheses
    /// </summary>
    private static int FindLogicalOperator(string expression, string operatorName)
    {
        var depth = 0;
        var i = 0;
        
        while (i <= expression.Length - operatorName.Length)
        {
            if (expression[i] == '(')
            {
                depth++;
            }
            else if (expression[i] == ')')
            {
                depth--;
            }
            else if (depth == 0)
            {
                // Check if the operator is at this position
                if (i + operatorName.Length <= expression.Length)
                {
                    var substring = expression.Substring(i, operatorName.Length);
                    if (string.Equals(substring, operatorName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Verify this is a standalone word (not part of another word)
                        var isWordBoundary = (i == 0 || !char.IsLetterOrDigit(expression[i - 1])) &&
                                           (i + operatorName.Length == expression.Length || !char.IsLetterOrDigit(expression[i + operatorName.Length]));
                        
                        if (isWordBoundary)
                        {
                            return i;
                        }
                    }
                }
            }
            i++;
        }
        
        return -1;
    }

    /// <summary>
    /// Finds the index of the first comparison operator in the string
    /// </summary>
    private static int FindFirstOperator(string expression)
    {
        string[] operators = { "==", "!=", ">=", "<=", ">", "<", "&&", "||", " AND ", " OR ", " XOR " };
        
        int minIndex = int.MaxValue;
        foreach (var op in operators)
        {
            int index = expression.IndexOf(op);
            if (index >= 0 && index < minIndex)
            {
                minIndex = index;
            }
        }
        
        return minIndex == int.MaxValue ? -1 : minIndex;
    }

    /// <summary>
    /// Finds the last occurrence of an operator outside parentheses in the expression
    /// </summary>
    private static int FindLastOperatorOutsideBrackets(string expression, char op)
    {
        int depth = 0;
        bool inQuotes = false;
        char quoteChar = '\0';
        
        // Search left to right but return the last occurrence
        int lastIndex = -1;
        
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            
            // Handle quotes
            if ((c == '\'' || c == '"') && (i == 0 || expression[i-1] != '\\'))
            {
                if (!inQuotes)
                {
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                }
                continue;
            }
            
            // Skip characters inside quotes
            if (inQuotes) continue;
            
            // Account for parentheses
            if (c == '(') depth++;
            else if (c == ')') depth--;
            
            // Search for operator outside parentheses
            if (depth == 0 && c == op)
            {
                // Verify this is not a unary operator
                if ((op == '+' || op == '-') && 
                    (i == 0 || "+-*/(.".Contains(expression[i-1])))
                {
                    // This is a unary operator, skip
                    continue;
                }
                
                lastIndex = i;
            }
        }
        
        return lastIndex;
    }

    /// <summary>
    /// Finds a comparison operator in the expression, accounting for potential nested properties with dots
    /// </summary>
    private static int FindComparisonOperator(string expression, string op)
    {
        int bracketLevel = 0;
        bool inString = false;
        char stringChar = '"';
        
        for (int i = 0; i < expression.Length - (op.Length - 1); i++)
        {
            // Skip string literal contents
            if ((expression[i] == '"' || expression[i] == '\'') && (i == 0 || expression[i-1] != '\\'))
            {
                if (!inString) 
                {
                    inString = true;
                    stringChar = expression[i];
                }
                else if (expression[i] == stringChar)
                {
                    inString = false;
                }
                continue;
            }
            
            if (inString) continue;
            
            // Account for parenthesis nesting level
            if (expression[i] == '(') 
            {
                bracketLevel++;
                continue;
            }
            if (expression[i] == ')') 
            {
                bracketLevel--;
                continue;
            }
            
            // Search for operator only at the top level (outside parentheses)
            if (bracketLevel == 0)
            {
                if (i + op.Length <= expression.Length && expression.Substring(i, op.Length) == op)
                {
                    // Ensure this is not part of a property name (e.g. property.customer.Id)
                    bool isPartOfProperty = false;
                    
                    if (i > 0 && expression[i-1] == '.')
                        isPartOfProperty = true;
                    
                    if (i + op.Length < expression.Length && expression[i + op.Length] == '.')
                        isPartOfProperty = true;
                    
                    if (!isPartOfProperty)
                        return i;
                }
            }
        }
        
        return -1;
    }

    /// <summary>
    /// Checks whether the expression is a string literal
    /// </summary>
    private static bool IsStringLiteral(string expression)
        => (expression.StartsWith("'") && expression.EndsWith("'")) || 
           (expression.StartsWith("\"") && expression.EndsWith("\""));

    /// <summary>
    /// Checks for increment/decrement operations
    /// </summary>
    private static bool HasIncrementDecrementOps(string expression)
        => PostfixIncrementDecrementRegex.IsMatch(expression) || 
           PrefixIncrementDecrementRegex.IsMatch(expression);

    /// <summary>
    /// Checks for standalone function calls (e.g. upper(...), concat(...)), 
    /// but NOT method calls on properties (e.g. property.text.toLower()).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex FunctionCallPattern = 
        new(@"(?<![.\w])[a-zA-Z_]\w*\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static bool HasFunctionCalls(string expression)
        => FunctionCallPattern.IsMatch(expression);

    /// <summary>
    /// Checks for index access
    /// </summary>
    private static bool HasIndexAccess(string expression)
        => expression.Contains("[") && expression.Contains("]");

    /// <summary>
    /// Checks for special prefixes
    /// </summary>
    private static bool HasSpecialPrefixes(string expression)
        => expression.Contains("property.") ||
           expression.Contains("header.") ||
           expression.Contains("body") ||
           expression.Contains("jpath") ||
           expression.Contains(".") ||
           expression.Contains("logical");

    /// <summary>
    /// Checks for binary operators outside quoted strings
    /// </summary>
    private static bool HasBinaryOperatorsInExpression(string expression)
    {
        bool inQuotes = false;
        char quoteChar = '\0';
        int bracketDepth = 0;
        
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            
            // Handle quotes
            if ((c == '\'' || c == '"') && (i == 0 || expression[i-1] != '\\'))
            {
                if (!inQuotes)
                {
                    inQuotes = true;
                    quoteChar = c;
                    continue;
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                    continue;
                }
            }
            // If not inside quotes, check for operators and parentheses
            else if (!inQuotes)
            {
                // Account for parentheses for nesting level tracking
                if (c == '(')
                {
                    bracketDepth++;
                    return true; // Presence of parentheses already indicates expression complexity
                }
                else if (c == ')')
                {
                    bracketDepth--;
                    return true; // Presence of parentheses already indicates expression complexity
                }
                // Check for null-coalescing operator
                else if (c == '?' && i + 1 < expression.Length && expression[i + 1] == '?')
                {
                    return true;
                }
                // Check for ternary operator (single ? not followed by ?)
                else if (c == '?' && (i + 1 >= expression.Length || expression[i + 1] != '?'))
                {
                    return true;
                }
                // Check for binary operators
                else if (c == '+' || c == '-' || c == '*' || c == '/')
                {
                    // For unary operations (e.g. +1 or -2), verify this is not a unary operator
                    if (c == '+' || c == '-')
                    {
                        // If this is the first character or follows another operator, it is unary
                        if (i == 0 || 
                            expression[i-1] == '+' || 
                            expression[i-1] == '-' || 
                            expression[i-1] == '*' || 
                            expression[i-1] == '/' || 
                            expression[i-1] == '(' || 
                            expression[i-1] == '[' ||
                            expression[i-1] == ',')
                        {
                            // This is a unary operator, continue
                            continue;
                        }

                        // Check increment/decrement operations (++ and --)
                        if (i < expression.Length - 1 && (c == '+' || c == '-') && expression[i+1] == c)
                        {
                            DebugLog($"HasOperations: Detected increment/decrement operation in '{expression}'");
                            return true; // This is an increment or decrement operation
                        }
                    }
                    
                    return true; // This is a binary operator
                }
            }
        }
        
        return false;
    }

    /// <summary>
    /// Checks whether the expression contains a ternary operator (single <c>?</c> not part of <c>??</c>).
    /// </summary>
    private static bool ContainsTernary(string expression)
    {
        for (int i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '?')
            {
                // Skip ?? (null-coalescing)
                if (i + 1 < expression.Length && expression[i + 1] == '?')
                {
                    i++; // skip second ?
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks whether the string contains operations
    /// </summary>
    private static bool HasOperations(string expression)
    {
        if (string.IsNullOrEmpty(expression))
            return false;
            
        if (IsStringLiteral(expression))
            return false;
        
        if (HasIncrementDecrementOps(expression))
        {
            DebugLog($"HasOperations: Detected increment/decrement operation in '{expression}'");
            return true;
        }

        
        if (HasBinaryOperatorsInExpression(expression))
            return true;
        
        if (HasFunctionCalls(expression))
            return true;
        
        if (HasIndexAccess(expression))
            return true;
            
        return HasSpecialPrefixes(expression);
    }

    /// <summary>
    /// Determines whether a string is an XPath literal path rather than a variable/expression.
    /// XPath paths use '/' as a path separator, which would be misdetected as arithmetic division.
    /// </summary>
    private static bool IsXPathLiteral(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // Absolute paths: /root/child, //descendant
        if (value.StartsWith("/"))
            return true;

        // Relative paths with self axis: ./child, .//descendant
        if (value.StartsWith("./"))
            return true;

        // Attribute selectors: @name, @*
        if (value.StartsWith("@"))
            return true;

        // XPath axis expressions: child::, descendant::, ancestor::, etc.
        if (value.Contains("::"))
            return true;

        return false;
    }

    #endregion
}

