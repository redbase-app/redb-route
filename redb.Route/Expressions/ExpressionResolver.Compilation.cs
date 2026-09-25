using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using redb.Route.Abstractions;
using SysExpression = System.Linq.Expressions.Expression;

namespace redb.Route.Expressions;

/// <summary>
/// Partial class ExpressionResolver — Expression Tree compilation methods
/// </summary>
public static partial class ExpressionResolver
{
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
                // Whitespace around an operator carries no meaning here either: an explicit
                // expression ("Expr(...)") is an expression whatever its spacing. Until
                // 2026-08-28 this was the last dialect that required single spaces.
                || ContainsComparisonOrWordLogicOperator(expression)
                // Route-XML Ф1.5: modulo is new, so no legacy path ever learned it — route it to
                // the AST. Deliberately narrow (only '%', literals masked) so no existing form
                // changes its compilation path.
                || ContainsModuloOperator(expression)
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
        catch (Exception ex) when (ex is not ExpressionSandboxViolationException)
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
            
            // A dot in the name is ambiguous: a property literally called "omni.PollInterval" or a
            // member of a property called "omni". ResolvePropertySmart answers it the one way the
            // whole language answers it — literal name first, nested path second — which is what the
            // template form and every dotted header have always done. Going straight to nested
            // access here made the bare form the only place a namespaced property read as empty.
            if (propertyName.Contains('.'))
            {
                DebugLog($"Detected nested property: '{propertyName}'");

                var resolvePropertySmartMethod = typeof(ExpressionResolver).GetMethod(
                    nameof(ResolvePropertySmart),
                    BindingFlags.NonPublic | BindingFlags.Static);

                var propertyCall = SysExpression.Call(
                    resolvePropertySmartMethod,
                    exchangeParam,
                    SysExpression.Constant(propertyName));

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


    #endregion

    #region Operator search helper methods



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
    /// Comparison and word-logic operators, looked for outside quoted literals.
    /// Whitespace between tokens carries no meaning, so detection must not depend on it:
    /// <c>a&gt;1</c>, <c>a &gt; 1</c> and a tab- or newline-separated form are one expression.
    /// Word operators are matched the way the tokenizer matches them, case-insensitively.
    /// Arithmetic is deliberately absent: the value path already handles + - * / without
    /// requiring whitespace, and a bare literal must not be mistaken for a subtraction.
    /// </summary>
    private static readonly Regex ComparisonOrWordLogicRegex =
        new(@"==|!=|>=|<=|>|<|\b(AND|OR|XOR)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reports whether the expression contains a comparison or word-logic operator that is not
    /// part of a quoted literal. Used at the condition boundary to decide whether a string is a
    /// boolean expression, which has to be parsed, or a value to be read for truthiness.
    /// </summary>
    /// <param name="expression">The expression to inspect.</param>
    /// <returns><c>true</c> when an operator is present outside every quoted literal.</returns>
    internal static bool ContainsComparisonOrWordLogicOperator(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return ComparisonOrWordLogicRegex.IsMatch(MaskQuotedLiterals(expression));
    }

    /// <summary>
    /// Reports whether the expression contains the modulo operator outside every quoted literal
    /// (Route-XML Ф1.5). Narrow on purpose: the value dialect routes '%' to the AST through this,
    /// and nothing else, so existing forms keep their compilation path.
    /// </summary>
    private static bool ContainsModuloOperator(string expression)
        => MaskQuotedLiterals(expression).Contains('%');

    /// <summary>
    /// Replaces every quoted literal, quotes included, with a run of filler characters of the same
    /// length, so an operator inside a literal cannot be seen by a scan over the result. Literals
    /// are delimited the same way the tokenizer delimits them: a matching quote character, with a
    /// backslash escaping the next character, and an unterminated literal running to the end.
    /// </summary>
    private static string MaskQuotedLiterals(string expression)
    {
        if (expression.IndexOf('\'') < 0 && expression.IndexOf('"') < 0)
            return expression;

        const char filler = '#';
        var masked = new StringBuilder(expression.Length);
        var position = 0;

        while (position < expression.Length)
        {
            var current = expression[position];
            if (current is not ('\'' or '"'))
            {
                masked.Append(current);
                position++;
                continue;
            }

            var quote = current;
            masked.Append(filler);
            position++;

            while (position < expression.Length && expression[position] != quote)
            {
                if (expression[position] == '\\' && position + 1 < expression.Length)
                {
                    masked.Append(filler);
                    position++;
                }

                masked.Append(filler);
                position++;
            }

            if (position < expression.Length)
            {
                masked.Append(filler);
                position++;
            }
        }

        return masked.ToString();
    }

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
                else if (c == '+' || c == '-' || c == '*' || c == '/' || c == '%')
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
                            expression[i-1] == '%' ||
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

