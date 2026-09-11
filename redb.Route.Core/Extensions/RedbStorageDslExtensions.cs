using System.Reflection;
using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Core.Serialization;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.RedbCore.Extensions;

/// <summary>
/// Declarative storage verbs over <see cref="IRedbService"/> — the string/Type-parameterized
/// counterparts of the lambda verbs in <see cref="RedbRouteExtensions"/>, designed for the XML
/// markup (a lambda cannot live in markup) and equally usable from C#.
/// <para>
/// Every verb resolves the service per exchange through
/// <see cref="RedbRouteExtensions.GetRedbService(IRouteContext, string, IExchange?)"/> — the same
/// scoped model the lambda verbs use — and participates in an ambient redb transaction opened by
/// <c>BeginRedbTransaction()</c> like any other redb call on that service.
/// </para>
/// </summary>
public static class RedbStorageDslExtensions
{
    // ── Get (typed) ──────────────────────────────────────────────────

    /// <summary>
    /// Loads a <c>RedbObject&lt;TProps&gt;</c> by id and stores it into the target
    /// (body by default).
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="propsType">The props CLR type (markup: resolved via the bean type resolver).</param>
    /// <param name="idExpression">Expression producing the object id (e.g. <c>${header.orderId}</c>).</param>
    /// <param name="depth">Materialization depth for nested objects.</param>
    /// <param name="storage">Named <see cref="IRedbService"/>; null/empty — the default one.</param>
    /// <param name="target">Where the result goes: <c>body</c> (default), <c>header:Name</c>, <c>property:Name</c>.</param>
    public static IRouteDefinition RedbGet(
        this IRouteDefinition route,
        Type propsType,
        string idExpression,
        int depth = 10,
        string? storage = null,
        string? target = null)
    {
        ArgumentNullException.ThrowIfNull(propsType);
        ArgumentException.ThrowIfNullOrEmpty(idExpression);

        // Reflection once at definition time, a compiled delegate per message (Ф2 §3.5).
        var load = (Func<IRedbService, long, int, Task<object?>>)LoadHelperDef
            .MakeGenericMethod(propsType)
            .CreateDelegate(typeof(Func<IRedbService, long, int, Task<object?>>));
        var idExpr = new StringExpression(idExpression);
        var store = ParseTarget(target);

        return route.Process(async (exchange, ct) =>
        {
            var redb = Resolve(route, exchange, storage);
            var id = idExpr.Evaluate<long>(exchange);
            store(exchange, await load(redb, id, depth));
        });
    }

    // ── Get (raw JSON, no CLR type) ──────────────────────────────────

    /// <summary>
    /// Loads the whole object as raw JSON by id — the in-database materializer
    /// (<c>get_object_json</c>) serializes the full tree; no props CLR type is needed, so a
    /// pure-markup module without any assembly can read redb objects. The JSON string goes to
    /// the target (body by default); a missing object stores null (or throws, per the redb
    /// configuration's ThrowOnObjectNotFound).
    /// </summary>
    public static IRouteDefinition RedbGetJson(
        this IRouteDefinition route,
        string idExpression,
        int depth = 10,
        string? storage = null,
        string? target = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(idExpression);

        var idExpr = new StringExpression(idExpression);
        var store = ParseTarget(target);

        return route.Process(async (exchange, ct) =>
        {
            var redb = Resolve(route, exchange, storage);
            var id = idExpr.Evaluate<long>(exchange);
            store(exchange, await redb.LoadJsonAsync(id, depth));
        });
    }

    // ── Save ─────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the exchange body to redb storage and puts the saved object's id into the exchange
    /// body's object (the body keeps the saved <see cref="IRedbObject"/>; a JSON body is
    /// materialized first). Body cases:
    /// <list type="bullet">
    /// <item>an <see cref="IRedbObject"/> — saved as is (<paramref name="propsType"/> optional);</item>
    /// <item>a JSON <c>string</c> — deserialized to <c>RedbObject&lt;TProps&gt;</c> via
    /// <paramref name="propsType"/> (required for this case), then saved.</item>
    /// </list>
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="propsType">The props CLR type; required only when the body is JSON.</param>
    /// <param name="byUnique">Save through the declared unique keys (<c>SaveByUniqueAsync</c>) —
    /// requires <paramref name="propsType"/>.</param>
    /// <param name="storage">Named <see cref="IRedbService"/>; null/empty — the default one.</param>
    public static IRouteDefinition RedbSave(
        this IRouteDefinition route,
        Type? propsType = null,
        bool byUnique = false,
        string? storage = null)
    {
        if (byUnique && propsType is null)
            throw new ArgumentException(
                "RedbSave(byUnique: true) needs the props type — SaveByUniqueAsync is typed.",
                nameof(propsType));

        var saveByUnique = byUnique
            ? (Func<IRedbService, IRedbObject, Task<long>>)SaveByUniqueHelperDef
                .MakeGenericMethod(propsType!)
                .CreateDelegate(typeof(Func<IRedbService, IRedbObject, Task<long>>))
            : null;

        return route.Process(async (exchange, ct) =>
        {
            var redb = Resolve(route, exchange, storage);
            var obj = MaterializeBody(exchange, propsType);

            if (saveByUnique is not null)
                await saveByUnique(redb, obj);
            else
                await redb.SaveAsync(obj);

            exchange.In.Body = obj; // the saved object (with its id) is the step's result
        });
    }

    // ── Query ────────────────────────────────────────────────────────

    /// <summary>
    /// A server-side props query: the <paramref name="where"/> condition string is translated
    /// onto redb LINQ by <see cref="Query.RedbQueryTranslator"/> (props paths, comparisons,
    /// AND/OR/NOT, contains/startsWith/endsWith; value-side subtrees — headers, functions,
    /// date arithmetic — fold per message into constants; the untranslatable refuses at route
    /// build). The result list of <c>RedbObject&lt;TProps&gt;</c> goes to the target.
    /// Order of application: where → filter spec → orderBy → skip → take.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="propsType">The props CLR type to query.</param>
    /// <param name="where">Condition string in the route language; null — no condition.</param>
    /// <param name="orderBy">Props property path to order by; null — storage order.</param>
    /// <param name="descending">Order direction for <paramref name="orderBy"/>.</param>
    /// <param name="take">Row limit.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="filter">Registry name of an <see cref="IRedbQuerySpec{TProps}"/> — the
    /// full-LINQ escape hatch for what the string cannot say.</param>
    /// <param name="storage">Named <see cref="IRedbService"/>; null/empty — the default one.</param>
    /// <param name="target">Where the list goes: <c>body</c> (default), <c>header:Name</c>, <c>property:Name</c>.</param>
    public static IRouteDefinition RedbQuery(
        this IRouteDefinition route,
        Type propsType,
        string? where = null,
        string? orderBy = null,
        bool descending = false,
        int? take = null,
        int? skip = null,
        string? filter = null,
        string? storage = null,
        string? target = null)
    {
        ArgumentNullException.ThrowIfNull(propsType);
        if (where is null && filter is null && orderBy is null && take is null && skip is null)
            throw new ArgumentException(
                "RedbQuery without where/filter/orderBy/take/skip would be a full unbounded scan — say what you want.",
                nameof(where));

        // Everything translatable is translated NOW — a broken condition fails the route build.
        var wherePlan = where is null ? null : Query.RedbQueryTranslator.TranslateWhere(propsType, where);
        var orderPlan = orderBy is null ? null : Query.RedbQueryTranslator.TranslateOrderBy(propsType, orderBy);
        var store = ParseTarget(target);
        var execute = (Func<IRedbService, IExchange, IRouteContext, Task<object?>>)QueryHelperDef
            .MakeGenericMethod(propsType)
            .CreateDelegate(typeof(Func<IRedbService, IExchange, IRouteContext, Task<object?>>), new QueryPlan
            {
                Where = wherePlan,
                OrderBy = orderPlan,
                Descending = descending,
                Take = take,
                Skip = skip,
                FilterName = filter,
            });

        return route.Process(async (exchange, ct) =>
        {
            var redb = Resolve(route, exchange, storage);
            var context = route.GetContext()
                ?? throw new InvalidOperationException("RouteContext is not available.");
            store(exchange, await execute(redb, exchange, context));
        });
    }

    // ── Delete ───────────────────────────────────────────────────────

    /// <summary>
    /// Deletes an object by id (no props type needed). The body is left untouched; the outcome
    /// (true — deleted, false — not found) goes into the <c>redbDeleted</c> header.
    /// </summary>
    public static IRouteDefinition RedbDelete(
        this IRouteDefinition route,
        string idExpression,
        string? storage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(idExpression);

        var idExpr = new StringExpression(idExpression);

        return route.Process(async (exchange, ct) =>
        {
            var redb = Resolve(route, exchange, storage);
            var id = idExpr.Evaluate<long>(exchange);
            exchange.In.Headers["redbDeleted"] = await redb.DeleteAsync(id);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static IRedbService Resolve(IRouteDefinition route, IExchange exchange, string? storage)
        => string.IsNullOrEmpty(storage)
            ? RedbRouteExtensions.ResolveRedbService(route, exchange)
            : (route.GetContext() ?? throw new InvalidOperationException(
                    "RouteContext is not available. Ensure the route builder has been configured."))
                .GetRedbService(storage, exchange);

    private static readonly MethodInfo LoadHelperDef = typeof(RedbStorageDslExtensions)
        .GetMethod(nameof(LoadHelper), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static async Task<object?> LoadHelper<TProps>(IRedbService redb, long id, int depth)
        where TProps : class, new()
        => await redb.LoadAsync<TProps>(id, depth);

    private static readonly MethodInfo SaveByUniqueHelperDef = typeof(RedbStorageDslExtensions)
        .GetMethod(nameof(SaveByUniqueHelper), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Task<long> SaveByUniqueHelper<TProps>(IRedbService redb, IRedbObject obj)
        where TProps : class, new()
        => redb.SaveByUniqueAsync((RedbObject<TProps>)obj);

    private static readonly MethodInfo QueryHelperDef = typeof(QueryPlan)
        .GetMethod(nameof(QueryPlan.ExecuteAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// One query's translated pieces; <see cref="ExecuteAsync{TProps}"/> is the typed island
    /// every untyped part flows into (closed per step via CreateDelegate over this instance).
    /// </summary>
    private sealed class QueryPlan
    {
        public Func<IExchange, System.Linq.Expressions.LambdaExpression>? Where;
        public System.Linq.Expressions.LambdaExpression? OrderBy;
        public bool Descending;
        public int? Take;
        public int? Skip;
        public string? FilterName;
        private object? _spec;
        private MethodInfo? _orderMethod;

        internal async Task<object?> ExecuteAsync<TProps>(
            IRedbService redb, IExchange exchange, IRouteContext context) where TProps : class, new()
        {
            var query = redb.Query<TProps>();
            if (Where is not null)
                query = query.Where((System.Linq.Expressions.Expression<Func<TProps, bool>>)Where(exchange));
            if (FilterName is not null)
            {
                var spec = _spec ??= context.GetFromRegistry<object>(FilterName)
                    ?? throw new InvalidOperationException(
                        $"filter '{FilterName}' is not in the context registry.");
                if (spec is not Query.IRedbQuerySpec<TProps> typedSpec)
                    throw new InvalidOperationException(
                        $"filter '{FilterName}' is {spec.GetType().Name}, not IRedbQuerySpec<{typeof(TProps).Name}>.");
                query = typedSpec.Apply(query);
            }
            if (OrderBy is not null)
            {
                // The selector is typed Func<TProps, TKey> with the member's own key type (the
                // provider's ordering parser reads a plain property access, not a boxing
                // Convert), so the generic OrderBy is closed once over that key type.
                _orderMethod ??= typeof(redb.Core.Query.IRedbQueryable<TProps>)
                    .GetMethod(Descending ? "OrderByDescending" : "OrderBy")!
                    .MakeGenericMethod(OrderBy.ReturnType);
                query = (redb.Core.Query.IRedbQueryable<TProps>)_orderMethod.Invoke(query, [OrderBy])!;
            }
            if (Skip is { } skip)
                query = query.Skip(skip);
            if (Take is { } take)
                query = query.Take(take);
            return await query.ToListAsync().ConfigureAwait(false);
        }
    }

    private static readonly SystemTextJsonRedbSerializer JsonSerializer = new();

    private static IRedbObject MaterializeBody(IExchange exchange, Type? propsType)
    {
        var body = exchange.In.Body;
        switch (body)
        {
            case IRedbObject redbObject:
                return redbObject;
            case string json when propsType is not null:
                return JsonSerializer.DeserializeRedbDynamic(json, propsType)
                    ?? throw new InvalidOperationException(
                        $"RedbSave: the body JSON did not deserialize to RedbObject<{propsType.Name}>.");
            case string:
                throw new InvalidOperationException(
                    "RedbSave: the body is a JSON string but no props type was given — "
                    + "pass the type (markup: type=\"...\") so the object can be materialized.");
            default:
                throw new InvalidOperationException(
                    $"RedbSave: unsupported body {body?.GetType().Name ?? "null"} — "
                    + "expected an IRedbObject or a JSON string.");
        }
    }

    /// <summary>
    /// Parses <c>body</c> / <c>header:Name</c> / <c>property:Name</c> at DEFINITION time, so a
    /// typo fails the route build rather than the first message.
    /// </summary>
    private static Action<IExchange, object?> ParseTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target == "body")
            return static (exchange, value) => exchange.In.Body = value;

        if (target.StartsWith("header:", StringComparison.Ordinal) && target.Length > "header:".Length)
        {
            var name = target["header:".Length..];
            return (exchange, value) => exchange.In.Headers[name] = value;
        }

        if (target.StartsWith("property:", StringComparison.Ordinal) && target.Length > "property:".Length)
        {
            var name = target["property:".Length..];
            return (exchange, value) => exchange.Properties[name] = value;
        }

        throw new ArgumentException(
            $"Unknown target '{target}' — expected 'body', 'header:Name' or 'property:Name'.",
            nameof(target));
    }
}
