using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using redb.Route.Controllers.Attributes;

namespace redb.Route.Controllers;

/// <summary>
/// Route table entry holding controller type, method info, and compiled template segments.
/// </summary>
public sealed record ControllerAction(
    Type ControllerType,
    MethodInfo Method,
    HttpMethodType HttpMethod,
    string Template,
    string[] Segments);

/// <summary>
/// Scans assemblies for <see cref="RedbController"/> subclasses, builds a route lookup table,
/// and resolves incoming (method, path) pairs to controller actions.
/// Thread-safe. Lookup is O(n) on segments but typically fast given small route counts.
/// </summary>
public sealed class ControllerRegistry
{
    private readonly ConcurrentBag<ControllerAction> _actions = new();
    private static readonly Regex TemplateParamRegex = new(@"\{(\w+)\}", RegexOptions.Compiled);

    /// <summary>Returns all registered controller actions.</summary>
    public IReadOnlyCollection<ControllerAction> Actions => _actions.ToArray();

    /// <summary>
    /// Scans an assembly for all non-abstract <see cref="RedbController"/> subclasses
    /// and registers their attributed actions.
    /// </summary>
    /// <param name="assembly">Assembly to scan.</param>
    /// <returns>Number of actions registered from this assembly.</returns>
    public int RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var count = 0;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(RedbController)))
                continue;

            count += RegisterController(type);
        }

        return count;
    }

    /// <summary>
    /// Registers a single controller type and its attributed actions.
    /// </summary>
    /// <param name="controllerType">Controller type to register.</param>
    /// <returns>Number of actions registered.</returns>
    public int RegisterController(Type controllerType)
    {
        ArgumentNullException.ThrowIfNull(controllerType);
        var routeAttr = controllerType.GetCustomAttribute<Attributes.RouteAttribute>();
        var basePath = routeAttr?.Template ?? controllerType.Name.Replace("Controller", "").ToLowerInvariant();
        var count = 0;

        foreach (var method in controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var httpAttr = method.GetCustomAttribute<HttpMethodAttribute>();
            if (httpAttr is null)
                continue;

            var template = string.IsNullOrEmpty(httpAttr.Template)
                ? basePath
                : $"{basePath}/{httpAttr.Template}";

            var segments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);

            _actions.Add(new ControllerAction(controllerType, method, httpAttr.Method, template, segments));
            count++;
        }

        return count;
    }

    /// <summary>
    /// Matches an incoming request path and HTTP method against the route table.
    /// Extracts path parameters from template placeholders.
    /// </summary>
    /// <param name="httpMethod">HTTP method (GET, POST, etc.).</param>
    /// <param name="path">Request path (e.g. "modules/123").</param>
    /// <param name="routeParams">Extracted route parameters (e.g. {id} → "123").</param>
    /// <returns>Matched action or null if no match.</returns>
    public ControllerAction? Resolve(HttpMethodType httpMethod, string path, out Dictionary<string, string> routeParams)
    {
        routeParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Collect all matches, then pick the most specific. Specificity = fewer
        // placeholder segments (literals win over {param}) so that e.g.
        //   DELETE /me/sessions/current
        // wins over
        //   DELETE /me/sessions/{sessionId}
        // The underlying store is a ConcurrentBag whose enumeration order is undefined,
        // so without this explicit ranking collisions would be non-deterministic.
        ControllerAction? best = null;
        Dictionary<string, string>? bestParams = null;
        var bestPlaceholderCount = int.MaxValue;

        foreach (var action in _actions)
        {
            if (action.HttpMethod != httpMethod)
                continue;

            if (action.Segments.Length != pathSegments.Length)
                continue;

            var matched = true;
            var placeholderCount = 0;
            var candidateParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < action.Segments.Length; i++)
            {
                var seg = action.Segments[i];
                var match = TemplateParamRegex.Match(seg);

                if (match.Success && match.Value == seg)
                {
                    // Entire segment is a parameter placeholder
                    candidateParams[match.Groups[1].Value] = pathSegments[i];
                    placeholderCount++;
                }
                else if (!seg.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched && placeholderCount < bestPlaceholderCount)
            {
                best = action;
                bestParams = candidateParams;
                bestPlaceholderCount = placeholderCount;
                if (placeholderCount == 0)
                    break; // exact literal match — cannot be beaten
            }
        }

        if (best is not null)
        {
            routeParams = bestParams!;
            return best;
        }

        return null;
    }

    /// <summary>
    /// Resolves using string method name (case-insensitive) instead of enum.
    /// Parses "GET", "POST", etc. to <see cref="HttpMethodType"/>.
    /// </summary>
    public ControllerAction? Resolve(string httpMethod, string path, out Dictionary<string, string> routeParams)
    {
        if (!Enum.TryParse<HttpMethodType>(httpMethod, ignoreCase: true, out var method))
        {
            routeParams = new Dictionary<string, string>();
            return null;
        }

        return Resolve(method, path, out routeParams);
    }
}
