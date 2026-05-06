using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using redb.Route.Abstractions;
using redb.Route.Expressions.Ast;
using SysExpression = System.Linq.Expressions.Expression;

namespace redb.Route.Expressions;

/// <summary>
/// Expression resolver with compiled lambda caching for performance optimization.
/// </summary>
public static partial class ExpressionResolver
{
    /// <summary>
    /// Delegate for expressions that resolve a value from an <see cref="IExchange"/>.
    /// </summary>
    private delegate object? ExpressionDelegate(IExchange exchange);
    
    // Caches for compiled expressions
    private static readonly ConcurrentDictionary<string, Func<IExchange, string>> _templateCache = new();
    private static readonly ConcurrentDictionary<string, Func<object?, string, object?>> _propertyResolverCache = new();
    private static readonly ConcurrentDictionary<string, Func<IExchange, bool>> _logicalExpressionCache = new();
    private static readonly ConcurrentDictionary<string, Func<IExchange, object?>> _valueExpressionCache = new();

    /// <summary>
    /// Builds a cache key from an optional context ID and expression.
    /// When contextId is provided, returns "{contextId}:{expression}"; otherwise returns the expression as-is.
    /// </summary>
    private static string BuildCacheKey(string? contextId, string expression)
        => contextId is null ? expression : $"{contextId}:{expression}";

    // Regular expressions for parsing
    private static readonly Regex TemplateRegex = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex LogicalExpressionRegex = new(@"^(.+?)\s*(==|!=|>=|<=|>|<)\s*(.+?)(?:\s+(AND|OR|XOR)\s+(.+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LogicalFunctionRegex = new(@"logical\((.+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnaryOperationRegex = new(@"^([!+\-])(.+)$", RegexOptions.Compiled);
    
    // Regular expression for prefix increment/decrement
    private static readonly Regex PrefixIncrementDecrementRegex = new(@"^(\+\+|--)(.+)$", RegexOptions.Compiled);
    
    // Regular expression for postfix increment/decrement
    private static readonly Regex PostfixIncrementDecrementRegex = new(@"^(.+)(\+\+|--)$", RegexOptions.Compiled);
    
    // Regular expression for binary operations
    private static readonly Regex BinaryOperationRegex = new(@"^(.+?)\s*([+\-])\s*(.+)$", RegexOptions.Compiled);
    
    // Regular expression for high-priority binary operations
    private static readonly Regex BinaryHighPriorityOperationRegex = new(@"^(.+?)\s*([*/])\s*(.+)$", RegexOptions.Compiled);

    // Regular expression for jpath function — lazy quantifier to avoid greedy capture across nested calls
    private static readonly Regex JPathFunctionRegex = new(@"jpath\((.*?)\)", RegexOptions.Compiled);

    // Regular expression for xpath function — lazy quantifier to avoid greedy capture across nested calls
    private static readonly Regex XPathFunctionRegex = new(@"xpath\((.*?)\)", RegexOptions.Compiled);

    // Prefixes for property types
    private const string PROPERTY_PREFIX = "property.";
    private const string HEADER_PREFIX = "header.";
    private const string BODY_PREFIX = "body.";
    private const string EXCEPTION_PREFIX = "exception.";

    // Debug flag
    private static bool _debugEnabled = false;

    // Optional logger for expression resolution warnings
    private static ILogger? _logger;

    /// <summary>
    /// Sets an optional logger for expression resolution warnings (missing headers, properties, etc.).
    /// </summary>
    public static void SetLogger(ILogger? logger) => _logger = logger;

    /// <summary>
    /// Sets an optional logger factory; creates a logger named "redb.Route.Expressions".
    /// </summary>
    public static void SetLoggerFactory(ILoggerFactory? factory)
        => _logger = factory?.CreateLogger("redb.Route.Expressions");

    // Counters for caching metrics
    private static long _templateHits = 0;
    private static long _templateMisses = 0;
    private static long _propertyResolverHits = 0;
    private static long _propertyResolverMisses = 0;
    private static long _logicalExpressionHits = 0;
    private static long _logicalExpressionMisses = 0;
    private static long _valueExpressionHits = 0;
    private static long _valueExpressionMisses = 0;

    #region Debug methods

    /// <summary>
    /// Enables or disables debug output.
    /// </summary>
    /// <param name="enabled"><c>true</c> to enable debug logging; <c>false</c> to disable it.</param>
    public static void SetDebugMode(bool enabled)
    {
        _debugEnabled = enabled;
    }

    /// <summary>
    /// Writes a debug message to the console when debug mode is enabled.
    /// </summary>
    /// <param name="message">The message to output.</param>
    public static void DebugLog(string message)
    {
        if (_debugEnabled)
        {
            Console.WriteLine($"[ExpressionResolver DEBUG] {message}");
        }
    }

    /// <summary>
    /// Prints an expression tree to the console with indentation reflecting nesting depth.
    /// </summary>
    /// <param name="expression">The expression to visualize.</param>
    /// <param name="indent">Current indentation prefix.</param>
    /// <param name="isRoot">Whether this is the root expression.</param>
    public static void DebugPrintExpressionTree(SysExpression expression, string indent = "", bool isRoot = true)
    {
        if (!_debugEnabled || expression == null)
            return;
            
        var nodeType = expression.NodeType.ToString();
        var expressionType = expression.Type.Name;
        
        Console.WriteLine($"[ExprTree] {indent}{(isRoot ? "▶ " : "├─")} {nodeType} ({expressionType})");
        
        // Increase indent for child nodes
        var childIndent = indent + (isRoot ? "   " : "│  ");
        
        // Process child expressions depending on the type
        switch (expression)
        {
            case MethodCallExpression methodCall:
                Console.WriteLine($"[ExprTree] {childIndent}├─ Method: {methodCall.Method.Name}");
                Console.WriteLine($"[ExprTree] {childIndent}├─ Object:");
                if (methodCall.Object != null)
                    DebugPrintExpressionTree(methodCall.Object, childIndent + "│  ", false);
                
                if (methodCall.Arguments.Count > 0)
                {
                    Console.WriteLine($"[ExprTree] {childIndent}├─ Arguments ({methodCall.Arguments.Count}):");
                    for (int i = 0; i < methodCall.Arguments.Count; i++)
                    {
                        Console.WriteLine($"[ExprTree] {childIndent}│  ├─ Arg[{i}]:");
                        DebugPrintExpressionTree(methodCall.Arguments[i], childIndent + "│  │  ", false);
                    }
                }
                break;
                
            case BinaryExpression binary:
                Console.WriteLine($"[ExprTree] {childIndent}├─ Left:");
                DebugPrintExpressionTree(binary.Left, childIndent + "│  ", false);
                Console.WriteLine($"[ExprTree] {childIndent}├─ Right:");
                DebugPrintExpressionTree(binary.Right, childIndent + "│  ", false);
                break;
                
            case UnaryExpression unary:
                Console.WriteLine($"[ExprTree] {childIndent}├─ Operand:");
                DebugPrintExpressionTree(unary.Operand, childIndent + "│  ", false);
                break;
                
            case System.Linq.Expressions.ConstantExpression constant:
                var value = constant.Value?.ToString() ?? "null";
                Console.WriteLine($"[ExprTree] {childIndent}├─ Value: {value}");
                break;
                
            case NewExpression newExpr:
                Console.WriteLine($"[ExprTree] {childIndent}├─ Constructor: {newExpr.Constructor.DeclaringType.Name}");
                if (newExpr.Arguments.Count > 0)
                {
                    Console.WriteLine($"[ExprTree] {childIndent}├─ Arguments ({newExpr.Arguments.Count}):");
                    for (int i = 0; i < newExpr.Arguments.Count; i++)
                    {
                        Console.WriteLine($"[ExprTree] {childIndent}│  ├─ Arg[{i}]:");
                        DebugPrintExpressionTree(newExpr.Arguments[i], childIndent + "│  │  ", false);
                    }
                }
                break;
                
            case LambdaExpression lambda:
                Console.WriteLine($"[ExprTree] {childIndent}├─ Parameters ({lambda.Parameters.Count}):");
                for (int i = 0; i < lambda.Parameters.Count; i++)
                {
                    var param = lambda.Parameters[i];
                    Console.WriteLine($"[ExprTree] {childIndent}│  ├─ Param[{i}]: {param.Name} ({param.Type.Name})");
                }
                Console.WriteLine($"[ExprTree] {childIndent}├─ Body:");
                DebugPrintExpressionTree(lambda.Body, childIndent + "│  ", false);
                break;
                
            case MemberExpression member:
                Console.WriteLine($"[ExprTree] {childIndent}├─ Member: {member.Member.Name}");
                Console.WriteLine($"[ExprTree] {childIndent}├─ Expression:");
                DebugPrintExpressionTree(member.Expression, childIndent + "│  ", false);
                break;
                
            case BlockExpression block:
                Console.WriteLine($"[ExprTree] {childIndent}├─ Variables ({block.Variables.Count}):");
                for (int i = 0; i < block.Variables.Count; i++)
                {
                    var variable = block.Variables[i];
                    Console.WriteLine($"[ExprTree] {childIndent}│  ├─ Var[{i}]: {variable.Name} ({variable.Type.Name})");
                }
                Console.WriteLine($"[ExprTree] {childIndent}├─ Expressions ({block.Expressions.Count}):");
                for (int i = 0; i < block.Expressions.Count; i++)
                {
                    Console.WriteLine($"[ExprTree] {childIndent}│  ├─ Expr[{i}]:");
                    DebugPrintExpressionTree(block.Expressions[i], childIndent + "│  │  ", false);
                }
                break;
                
            case ConditionalExpression conditional:
                Console.WriteLine($"[ExprTree] {childIndent}├─ Test:");
                DebugPrintExpressionTree(conditional.Test, childIndent + "│  ", false);
                Console.WriteLine($"[ExprTree] {childIndent}├─ IfTrue:");
                DebugPrintExpressionTree(conditional.IfTrue, childIndent + "│  ", false);
                Console.WriteLine($"[ExprTree] {childIndent}├─ IfFalse:");
                DebugPrintExpressionTree(conditional.IfFalse, childIndent + "│  ", false);
                break;
        }
    }

    #endregion

    #region Public methods for obtaining compiled delegates

    /// <summary>
    /// Gets a compiled delegate for processing a template string.
    /// </summary>
    /// <param name="template">The template string containing <c>${...}</c> placeholders.</param>
    /// <param name="contextId">Optional context identifier for cache isolation. When provided, the cache key is scoped to this context.</param>
    /// <returns>A compiled function that resolves the template against an <see cref="IExchange"/>.</returns>
    public static Func<IExchange, string> GetCompiledTemplate(string template, string? contextId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(template, nameof(template));
        
        var cacheKey = BuildCacheKey(contextId, template);
        DebugLog($"Requesting compiled template: '{template}' (key: '{cacheKey}')");
        
        if (_templateCache.TryGetValue(cacheKey, out var cached))
        {
            System.Threading.Interlocked.Increment(ref _templateHits);
            DebugLog($" Template found in cache: '{cacheKey}'");
            return cached;
        }
        
        System.Threading.Interlocked.Increment(ref _templateMisses);
        DebugLog($" Compiling new template: '{template}'");
        var compiled = _templateCache.GetOrAdd(cacheKey, _ => CompileTemplate(template));
        DebugLog($" Template compiled and added to cache: '{cacheKey}'");
        
        return compiled;
    }

    /// <summary>
    /// Gets a compiled delegate for resolving properties.
    /// </summary>
    /// <param name="expression">The property expression string.</param>
    /// <param name="contextId">Optional context identifier for cache isolation.</param>
    /// <returns>A compiled function that resolves a property value from a source object and expression.</returns>
    public static Func<object?, string, object?> GetCompiledPropertyResolver(string expression, string? contextId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        
        var cacheKey = BuildCacheKey(contextId, expression);
        DebugLog($"Requesting compiled property resolver: '{expression}' (key: '{cacheKey}')");
        
        if (_propertyResolverCache.TryGetValue(cacheKey, out var cached))
        {
            System.Threading.Interlocked.Increment(ref _propertyResolverHits);
            DebugLog($" Property resolver found in cache: '{cacheKey}'");
            return cached;
        }
        
        System.Threading.Interlocked.Increment(ref _propertyResolverMisses);
        DebugLog($" Compiling new property resolver: '{expression}'");
        var compiled = _propertyResolverCache.GetOrAdd(cacheKey, _ => CompilePropertyResolver(expression));
        DebugLog($" Property resolver compiled and added to cache: '{cacheKey}'");
        
        return compiled;
    }

    /// <summary>
    /// Gets a compiled delegate for logical expressions.
    /// </summary>
    /// <param name="expression">The logical expression string (e.g. <c>"header.status == 200"</c>).</param>
    /// <param name="contextId">Optional context identifier for cache isolation.</param>
    /// <returns>A compiled predicate that evaluates the logical expression against an <see cref="IExchange"/>.</returns>
    public static Func<IExchange, bool> GetCompiledLogicalExpression(string expression, string? contextId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        
        var cacheKey = BuildCacheKey(contextId, expression);
        DebugLog($"Requesting compiled logical expression: '{expression}' (key: '{cacheKey}')");
        
        if (_logicalExpressionCache.TryGetValue(cacheKey, out var cached))
        {
            System.Threading.Interlocked.Increment(ref _logicalExpressionHits);
            DebugLog($" Logical expression found in cache: '{cacheKey}'");
            return cached;
        }
        
        System.Threading.Interlocked.Increment(ref _logicalExpressionMisses);
        DebugLog($" Compiling new logical expression: '{expression}'");
        var compiled = _logicalExpressionCache.GetOrAdd(cacheKey, _ => CompileLogicalExpression(expression));
        DebugLog($" Logical expression compiled and added to cache: '{cacheKey}'");
        
        return compiled;
    }

    /// <summary>
    /// Gets a compiled delegate for obtaining values from an exchange.
    /// </summary>
    /// <param name="expression">The value expression string.</param>
    /// <param name="contextId">Optional context identifier for cache isolation.</param>
    /// <returns>A compiled function that extracts a value from an <see cref="IExchange"/>.</returns>
    public static Func<IExchange, object?> GetCompiledValueExpression(string expression, string? contextId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        
        DebugLog($"Requesting compiled value expression: '{expression}'");
        
        // For expressions with postfix operations, always use the AST parser
        if (PostfixIncrementDecrementRegex.IsMatch(expression) || PrefixIncrementDecrementRegex.IsMatch(expression))
        {
            DebugLog($"Increment/decrement operation detected, using AST parser: '{expression}'");
            return GetCompiledValueExpressionWithAst(expression, contextId);
        }
        
        var cacheKey = BuildCacheKey(contextId, expression);
        if (_valueExpressionCache.TryGetValue(cacheKey, out var cached))
        {
            System.Threading.Interlocked.Increment(ref _valueExpressionHits);
            DebugLog($" Value expression found in cache: '{cacheKey}'");
            return cached;
        }
        
        System.Threading.Interlocked.Increment(ref _valueExpressionMisses);
        DebugLog($" Compiling new value expression: '{expression}'");
        var compiled = _valueExpressionCache.GetOrAdd(cacheKey, _ => CompileValueExpression(expression));
        DebugLog($" Value expression compiled and added to cache: '{cacheKey}'");
        
        return compiled;
    }

    /// <summary>
    /// Gets a compiled delegate for obtaining values using the AST parser.
    /// </summary>
    /// <param name="expression">The value expression string.</param>
    /// <param name="contextId">Optional context identifier for cache isolation.</param>
    /// <returns>A compiled function that extracts a value from an <see cref="IExchange"/> via AST compilation.</returns>
    public static Func<IExchange, object?> GetCompiledValueExpressionWithAst(string expression, string? contextId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        
        var cacheKey = BuildCacheKey(contextId, "ast:" + expression);
        DebugLog($"Requesting compiled value expression with AST: '{expression}' (key: '{cacheKey}')");
        
        if (_valueExpressionCache.TryGetValue(cacheKey, out var cached))
        {
            System.Threading.Interlocked.Increment(ref _valueExpressionHits);
            DebugLog($" Value expression with AST found in cache: '{cacheKey}'");
            return cached;
        }
        
        System.Threading.Interlocked.Increment(ref _valueExpressionMisses);
        DebugLog($" Compiling new value expression with AST: '{expression}'");
        var compiled = _valueExpressionCache.GetOrAdd(cacheKey, _ => CompileExpressionWithAst(expression));
        DebugLog($" Value expression with AST compiled and added to cache: '{cacheKey}'");
        
        return compiled;
    }

    #endregion

    #region Core public methods

    /// <summary>
    /// Processes a template using a cached compiled delegate.
    /// </summary>
    /// <param name="template">The template string containing <c>${...}</c> placeholders.</param>
    /// <param name="exchange">The exchange to resolve values from.</param>
    /// <param name="contextId">Optional context identifier for cache isolation. If <c>null</c>, uses <c>exchange.RouteId</c> when available.</param>
    /// <returns>The resolved template string, or the original template if processing fails.</returns>
    public static string ProcessTemplate(string template, IExchange exchange, string? contextId = null)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));
        
        DebugLog($"Processing template: '{template}'");
        
        if (string.IsNullOrEmpty(template)) 
            return template;
        
        contextId ??= exchange.RouteId;
        
        var compiledTemplate = GetCompiledTemplate(template, contextId);
        
        try
        {
            // Apply the template to get the result
            var result = compiledTemplate(exchange);
            DebugLog($"Template processing result: '{result}'");
            return result;
        }
        catch (Exception ex)
        {
            // Runtime resolution error — log and rethrow so ErrorHandler can handle it
            _logger?.LogWarning(ex, "Expression resolution error in template '{Template}'", template);
            DebugLog($"Template processing error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Resolves a template with type preservation for single expressions.
    /// If the template is a single <c>${expr}</c> with no surrounding text,
    /// resolves via <see cref="ResolveExpression"/> preserving the CLR type (Guid, DateTimeOffset, int, etc.).
    /// Composite templates (e.g. <c>prefix-${x}-suffix</c>) fall back to <see cref="ProcessTemplate"/> returning a string.
    /// </summary>
    public static object? ResolveTypedOrTemplate(string template, IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));

        if (string.IsNullOrEmpty(template))
            return null;

        if (template.StartsWith("${") && template.EndsWith("}") && template.IndexOf('}') == template.Length - 1)
        {
            var expr = template[2..^1];
            return ResolveExpression(expr, exchange);
        }

        return ProcessTemplate(template, exchange);
    }

    /// <summary>
    /// Evaluates a logical expression using a cached compiled delegate.
    /// </summary>
    /// <param name="expression">The logical expression string.</param>
    /// <param name="exchange">The exchange to evaluate against.</param>
    /// <param name="contextId">Optional context identifier for cache isolation. If <c>null</c>, uses <c>exchange.RouteId</c> when available.</param>
    /// <returns><c>true</c> if the expression evaluates to true; otherwise <c>false</c>.</returns>
    public static bool EvaluateLogicalExpression(string expression, IExchange exchange, string? contextId = null)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        
        contextId ??= exchange.RouteId;
        
        DebugLog($"Evaluating logical expression: '{expression}'");
        var compiledExpression = GetCompiledLogicalExpression(expression, contextId);
        var result = compiledExpression(exchange);
        DebugLog($"Logical expression result: {result}");
        return result;
    }

    /// <summary>
    /// Compiles a logical expression into a predicate for use in <c>LogicalPredicate</c>.
    /// </summary>
    /// <param name="expression">The logical expression string.</param>
    /// <param name="contextId">Optional context identifier for cache isolation.</param>
    /// <returns>A compiled predicate function.</returns>
    public static Func<IExchange, bool> CompileLogicalPredicate(string expression, string? contextId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression, nameof(expression));
        
        DebugLog($"Compiling logical predicate: '{expression}'");
        
        // Use existing logical expression compilation method
        var compiledExpression = GetCompiledLogicalExpression(expression, contextId);
        
        DebugLog($"Logical predicate compiled successfully: '{expression}'");
        return compiledExpression;
    }

    #endregion

    #region Cache management

    /// <summary>
    /// Clears all caches and resets metric counters.
    /// </summary>
    public static void ClearAllCaches()
    {
        _templateCache.Clear();
        _propertyResolverCache.Clear();
        _logicalExpressionCache.Clear();
        _valueExpressionCache.Clear();
        
        // Reset counters
        System.Threading.Interlocked.Exchange(ref _templateHits, 0);
        System.Threading.Interlocked.Exchange(ref _templateMisses, 0);
        System.Threading.Interlocked.Exchange(ref _propertyResolverHits, 0);
        System.Threading.Interlocked.Exchange(ref _propertyResolverMisses, 0);
        System.Threading.Interlocked.Exchange(ref _logicalExpressionHits, 0);
        System.Threading.Interlocked.Exchange(ref _logicalExpressionMisses, 0);
        System.Threading.Interlocked.Exchange(ref _valueExpressionHits, 0);
        System.Threading.Interlocked.Exchange(ref _valueExpressionMisses, 0);
    }

    /// <summary>
    /// Clears the template cache.
    /// </summary>
    public static void ClearTemplateCache()
    {
        _templateCache.Clear();
    }

    /// <summary>
    /// Clears the logical expression cache.
    /// </summary>
    public static void ClearLogicalExpressionCache()
    {
        _logicalExpressionCache.Clear();
    }

    /// <summary>
    /// Clears the property resolver cache.
    /// </summary>
    public static void ClearPropertyResolverCache()
    {
        _propertyResolverCache.Clear();
    }

    /// <summary>
    /// Clears the value expression cache.
    /// </summary>
    public static void ClearValueExpressionCache()
    {
        _valueExpressionCache.Clear();
    }

    /// <summary>
    /// Clears all cache entries belonging to the specified context.
    /// Removes entries whose keys start with <c>"{contextId}:"</c> from all caches.
    /// </summary>
    /// <param name="contextId">The context identifier whose cached entries should be removed.</param>
    public static void ClearCachesForContext(string contextId)
    {
        ArgumentException.ThrowIfNullOrEmpty(contextId, nameof(contextId));
        
        var prefix = contextId + ":";
        RemoveByPrefix(_templateCache, prefix);
        RemoveByPrefix(_propertyResolverCache, prefix);
        RemoveByPrefix(_logicalExpressionCache, prefix);
        RemoveByPrefix(_valueExpressionCache, prefix);
    }

    private static void RemoveByPrefix<TValue>(ConcurrentDictionary<string, TValue> cache, string prefix)
    {
        foreach (var key in cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                cache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Returns cache statistics with performance metrics.
    /// </summary>
    /// <returns>A <see cref="CacheStatistics"/> record containing counts, hits, and misses for each cache.</returns>
    public static CacheStatistics GetCacheStatistics()
    {
        return new CacheStatistics
        {
            TemplateCount = _templateCache.Count,
            TemplateHits = System.Threading.Interlocked.Read(ref _templateHits),
            TemplateMisses = System.Threading.Interlocked.Read(ref _templateMisses),
            
            PropertyResolverCount = _propertyResolverCache.Count,
            PropertyResolverHits = System.Threading.Interlocked.Read(ref _propertyResolverHits),
            PropertyResolverMisses = System.Threading.Interlocked.Read(ref _propertyResolverMisses),
            
            LogicalExpressionCount = _logicalExpressionCache.Count,
            LogicalExpressionHits = System.Threading.Interlocked.Read(ref _logicalExpressionHits),
            LogicalExpressionMisses = System.Threading.Interlocked.Read(ref _logicalExpressionMisses),
            
            ValueExpressionCount = _valueExpressionCache.Count,
            ValueExpressionHits = System.Threading.Interlocked.Read(ref _valueExpressionHits),
            ValueExpressionMisses = System.Threading.Interlocked.Read(ref _valueExpressionMisses)
        };
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Sets the console output encoding to UTF-8.
    /// </summary>
    public static void SetUtf8Output()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        DebugLog("Console output encoding set to UTF-8");
    }

    /// <summary>
    /// Initializes the <see cref="ExpressionResolver"/> environment with UTF-8 output and optional debug mode.
    /// </summary>
    /// <param name="enableDebug"><c>true</c> to enable debug logging; defaults to <c>true</c>.</param>
    public static void Initialize(bool enableDebug = true)
    {
        SetDebugMode(enableDebug);
        SetUtf8Output();
        DebugLog("ExpressionResolver initialized");
    }

    #endregion
}

/// <summary>
/// Cache statistics with performance metrics for each expression cache.
/// </summary>
public record CacheStatistics
{
    /// <summary>Number of entries in the template cache.</summary>
    public int TemplateCount { get; init; }

    /// <summary>Number of template cache hits.</summary>
    public long TemplateHits { get; init; }

    /// <summary>Number of template cache misses.</summary>
    public long TemplateMisses { get; init; }

    /// <summary>Template cache hit rate as a percentage.</summary>
    public double TemplateHitRate => TemplateHits + TemplateMisses > 0 
        ? (double)TemplateHits / (TemplateHits + TemplateMisses) * 100 
        : 0;
    
    /// <summary>Number of entries in the property resolver cache.</summary>
    public int PropertyResolverCount { get; init; }

    /// <summary>Number of property resolver cache hits.</summary>
    public long PropertyResolverHits { get; init; }

    /// <summary>Number of property resolver cache misses.</summary>
    public long PropertyResolverMisses { get; init; }

    /// <summary>Property resolver cache hit rate as a percentage.</summary>
    public double PropertyResolverHitRate => PropertyResolverHits + PropertyResolverMisses > 0 
        ? (double)PropertyResolverHits / (PropertyResolverHits + PropertyResolverMisses) * 100 
        : 0;
    
    /// <summary>Number of entries in the logical expression cache.</summary>
    public int LogicalExpressionCount { get; init; }

    /// <summary>Number of logical expression cache hits.</summary>
    public long LogicalExpressionHits { get; init; }

    /// <summary>Number of logical expression cache misses.</summary>
    public long LogicalExpressionMisses { get; init; }

    /// <summary>Logical expression cache hit rate as a percentage.</summary>
    public double LogicalExpressionHitRate => LogicalExpressionHits + LogicalExpressionMisses > 0 
        ? (double)LogicalExpressionHits / (LogicalExpressionHits + LogicalExpressionMisses) * 100 
        : 0;
    
    /// <summary>Number of entries in the value expression cache.</summary>
    public int ValueExpressionCount { get; init; }

    /// <summary>Number of value expression cache hits.</summary>
    public long ValueExpressionHits { get; init; }

    /// <summary>Number of value expression cache misses.</summary>
    public long ValueExpressionMisses { get; init; }

    /// <summary>Value expression cache hit rate as a percentage.</summary>
    public double ValueExpressionHitRate => ValueExpressionHits + ValueExpressionMisses > 0 
        ? (double)ValueExpressionHits / (ValueExpressionHits + ValueExpressionMisses) * 100 
        : 0;
    
    /// <summary>Total number of entries across all caches.</summary>
    public int TotalCount => TemplateCount + PropertyResolverCount + LogicalExpressionCount + ValueExpressionCount;

    /// <summary>Total number of cache hits across all caches.</summary>
    public long TotalHits => TemplateHits + PropertyResolverHits + LogicalExpressionHits + ValueExpressionHits;

    /// <summary>Total number of cache misses across all caches.</summary>
    public long TotalMisses => TemplateMisses + PropertyResolverMisses + LogicalExpressionMisses + ValueExpressionMisses;

    /// <summary>Overall cache hit rate as a percentage.</summary>
    public double TotalHitRate => TotalHits + TotalMisses > 0 
        ? (double)TotalHits / (TotalHits + TotalMisses) * 100 
        : 0;
}

/// <summary>
/// Exception thrown when an expression fails to compile.
/// </summary>
public class ExpressionCompilationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExpressionCompilationException"/> class with a message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public ExpressionCompilationException(string message) : base(message)
    {
    }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ExpressionCompilationException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception that caused this failure.</param>
    public ExpressionCompilationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

