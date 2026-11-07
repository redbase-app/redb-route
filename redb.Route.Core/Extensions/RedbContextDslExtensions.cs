using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.RedbCore.Repositories;

namespace redb.Route.RedbCore.Extensions;

/// <summary>
/// Context-level redb bridge: scheme synchronization at context startup and named idempotent
/// repositories in the context registry — the pieces a packaged module declares in
/// <c>context.xml</c> (they run before any route starts, on the engine's fail-fast
/// OnContextStarting hook) and a C# host calls directly.
/// </summary>
public static class RedbContextDslExtensions
{
    /// <summary>
    /// Synchronizes the redb scheme for <paramref name="propsType"/> when the context starts —
    /// before any route accepts traffic, failing the start when the scheme cannot be brought up
    /// (the engine's OnContextStarting semantics).
    /// </summary>
    /// <param name="context">Route context.</param>
    /// <param name="propsType">The props CLR type whose scheme to synchronize.</param>
    /// <param name="storage">Named <see cref="IRedbService"/>; null/empty — the default one.</param>
    public static IRouteContext SyncRedbScheme(this IRouteContext context, Type propsType, string? storage = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(propsType);

        // Reflection once at registration; the listener invokes a prepared delegate.
        var sync = (Func<IRedbService, Task>)SyncHelperDef
            .MakeGenericMethod(propsType)
            .CreateDelegate(typeof(Func<IRedbService, Task>));

        context.AddLifecycleListener(new StartupAction(async ctx =>
        {
            var redb = string.IsNullOrEmpty(storage)
                ? ctx.GetRedbService()
                : ctx.GetRedbService(storage);
            await sync(redb).ConfigureAwait(false);
        }));
        return context;
    }

    /// <summary>
    /// Registers a <see cref="RedbIdempotentRepository"/> in the context registry under
    /// <paramref name="name"/>, as is — the name
    /// <c>IdempotentConsumer(keyExpression, repositoryName)</c> and the markup's
    /// <c>&lt;idempotentConsumer repository="…"&gt;</c> look up. Built lazily when the context
    /// starts (the repository needs the context's service provider for per-exchange scoping).
    /// </summary>
    /// <param name="context">Route context.</param>
    /// <param name="name">Registry name of the repository.</param>
    /// <param name="ttl">Idempotency-entry lifetime; null — keep forever.</param>
    /// <param name="processorName">Logical processor name partitioning the entries; default — the registry name.</param>
    /// <param name="storage">
    /// The named redb instance the entries are kept in (<c>context.RegisterRedbService</c>, or a Tsak <c>Redb</c>
    /// section); null for the context's default redb, as <see cref="SyncRedbScheme"/> resolves it.
    /// </param>
    public static IRouteContext AddRedbIdempotentRepository(
        this IRouteContext context, string name, TimeSpan? ttl = null, string? processorName = null,
        string? storage = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);

        context.AddLifecycleListener(new StartupAction(async ctx =>
        {
            var options = new RedbIdempotentOptions
            {
                ProcessorName = processorName ?? name,
                Ttl = ttl,
            };

            if (string.IsNullOrEmpty(storage))
            {
                // The context's default redb: a scope of the context's service provider per operation.
                var scopeFactory = ctx.GetServiceProvider()?.GetService<IServiceScopeFactory>()
                    ?? throw new InvalidOperationException(
                        $"AddRedbIdempotentRepository('{name}'): the context has no service provider — "
                        + "the repository resolves a scoped IRedbService per exchange and needs DI.");
                ctx.AddIdempotentRepository(name, new RedbIdempotentRepository(scopeFactory, options));
                return;
            }

            // A named instance: a scope of it per operation, from the same source the redb steps of that storage use.
            Func<RedbScope> openScope = () => ctx.CreateRedbScope(storage);

            // Opened once at start, so a storage name that resolves to nothing fails the start, not the first message.
            await using (openScope().ConfigureAwait(false)) { }

            ctx.AddIdempotentRepository(name, new RedbIdempotentRepository(openScope, options));
        }));
        return context;
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static readonly MethodInfo SyncHelperDef = typeof(RedbContextDslExtensions)
        .GetMethod(nameof(SyncHelper), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Task SyncHelper<TProps>(IRedbService redb) where TProps : class
        => redb.SyncSchemeAsync<TProps>();

    /// <summary>A lifecycle listener wrapping one startup action (fail-fast by the hook's contract).</summary>
    private sealed class StartupAction(Func<IRouteContext, Task> action) : IRouteLifecycleListener
    {
        public Task OnContextStarting(IRouteContext context, CancellationToken ct) => action(context);
    }
}
