using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Default implementation of IExchange.
/// In is always present, Out is lazy (null by default for InOnly routes).
/// </summary>
public class Exchange : IExchange
{
    private readonly Dictionary<string, object?> _properties = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _stopped;
    private IServiceScope? _scope;
    private IServiceScopeFactory? _scopeFactory;
    private bool _ownsScope = true;
    private int _scopesReleased; // 0 = not released, 1 = released (Interlocked)

    // Body ownership, as a Camel unit of work: the exchange that owns a body disposes it, once, when it is done. A copy
    // shares its origin's body by reference, so it disposes only a body no ancestor carries or lent it; one an ancestor
    // carries (the aggregated result, a merged-back body) goes on that ancestor's account instead.
    private Exchange? _origin;
    private HashSet<object>? _lent;            // disposable bodies this exchange lent to its copies or took over from them
    private readonly object _bodiesLock = new();
    private bool _bodiesReleased;               // under _bodiesLock

    /// <inheritdoc />
    public IMessage In { get; set; }

    /// <inheritdoc />
    public IMessage? Out { get; set; }

    /// <inheritdoc />
    public bool HasOut => Out != null;

    /// <inheritdoc />
    public ExchangePattern Pattern { get; set; } = ExchangePattern.InOnly;

    /// <inheritdoc />
    public IDictionary<string, object?> Properties => _properties;

    /// <inheritdoc />
    public Exception? Exception { get; set; }

    /// <inheritdoc />
    public bool ExceptionHandled { get; set; }

    /// <inheritdoc />
    public string? RouteId { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Set by the route wrapper on entry and restored on exit; the setter is internal so only the
    /// pipeline stamps it.
    /// </remarks>
    public IRouteContext? Context { get; internal set; }

    /// <inheritdoc />
    public string ExchangeId { get; private set; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public bool IsStopped => _stopped;

    /// <inheritdoc />
    public IServiceProvider? ServiceProvider => _scope?.ServiceProvider;

    /// <summary>Creates an exchange with a new empty In message.</summary>
    public Exchange() : this(new Message()) { }

    /// <summary>Creates an exchange with the specified In message.</summary>
    /// <param name="inMessage">The primary message.</param>
    public Exchange(IMessage inMessage)
    {
        In = inMessage ?? throw new ArgumentNullException(nameof(inMessage));
    }

    /// <summary>Creates an exchange with DI scope support.</summary>
    /// <param name="inMessage">The primary message.</param>
    /// <param name="scopeFactory">Factory for creating the scoped service provider.</param>
    public Exchange(IMessage inMessage, IServiceScopeFactory scopeFactory) : this(inMessage)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _scope = _scopeFactory.CreateScope();
    }

    /// <summary>Creates an exchange, optionally with a per-exchange DI scope.</summary>
    public static Exchange Create(IMessage message, IServiceScopeFactory? scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(message);
        return scopeFactory != null ? new Exchange(message, scopeFactory) : new Exchange(message);
    }

    /// <inheritdoc />
    public T? GetProperty<T>(string key)
        => _properties.TryGetValue(key, out var value) ? TypedValue.Convert<T>(value, $"property '{key}'") : default;

    /// <inheritdoc />
    public void Stop() => _stopped = true;

    /// <inheritdoc />
    public IExchange Clone()
    {
        var clone = new Exchange((Message)In.Clone())
        {
            Pattern = Pattern,
            RouteId = RouteId,
            Context = Context,
            Exception = Exception,
            ExceptionHandled = ExceptionHandled
        };
        // preserve the same ExchangeId for clones
        clone.ExchangeId = ExchangeId;

        if (Out != null)
            clone.Out = Out.Clone();
        LendBodiesTo(clone);

        foreach (var kvp in _properties)
        {
            // Named redb scopes are per-exchange; child will create its own on first access.
            if (ExchangeResources.IsOwnedByExchange(kvp.Key))
                continue;
            clone._properties[kvp.Key] = kvp.Value;
        }

        if (_scopeFactory != null)
        {
            clone._scopeFactory = _scopeFactory;
            clone._scope = _scopeFactory.CreateScope();
        }

        return clone;
    }

    /// <inheritdoc />
    public IExchange Snapshot()
    {
        // Same shape as Clone but the message body is deep-copied (In.Snapshot / Out.Snapshot),
        // so the captured state is frozen against later in-place mutation of the payload.
        var snapshot = new Exchange((Message)In.Snapshot())
        {
            Pattern = Pattern,
            RouteId = RouteId,
            Context = Context,
            Exception = Exception,
            ExceptionHandled = ExceptionHandled
        };
        snapshot.ExchangeId = ExchangeId;

        if (Out != null)
            snapshot.Out = Out.Snapshot();
        LendBodiesTo(snapshot);

        foreach (var kvp in _properties)
        {
            // Named redb scopes are per-exchange; the snapshot creates its own on first access.
            if (ExchangeResources.IsOwnedByExchange(kvp.Key))
                continue;
            // Property values are shared (route metadata, not payload) — see Snapshot doc.
            snapshot._properties[kvp.Key] = kvp.Value;
        }

        // NO DI scope is created here. A checkpoint snapshot is dormant data captured on every
        // marker pass and usually never replayed — minting a scope per capture would leak one per
        // message (the exact class of leak the 3.3.4 connection-leak work closed). The factory
        // reference is kept so a replay can mint (and dispose) a scope on demand; a snapshot that is
        // never replayed owns no scope and leaks nothing.
        snapshot._scopeFactory = _scopeFactory;
        snapshot._ownsScope = false;   // _scope stays null

        return snapshot;
    }

    /// <inheritdoc />
    public IExchange CreateChild(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var child = new Exchange(message)
        {
            Pattern = Pattern,
            RouteId = RouteId,
            Context = Context
        };

        foreach (var kvp in _properties)
        {
            // Named redb scopes are per-exchange; child will create its own on first access.
            if (ExchangeResources.IsOwnedByExchange(kvp.Key))
                continue;
            child._properties[kvp.Key] = kvp.Value;
        }

        if (_scopeFactory != null)
        {
            child._scopeFactory = _scopeFactory;
            child._scope = _scopeFactory.CreateScope();
        }

        // Its message is its own; a body it hands back to this exchange stays on this exchange's account.
        child._origin = this;
        return child;
    }

    /// <inheritdoc />
    public IExchange CreateLinkedChild(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var child = new Exchange(message)
        {
            Pattern = Pattern,
            RouteId = RouteId,
            Context = Context,
            _ownsScope = false,
            _scope = _scope,
            _scopeFactory = _scopeFactory
        };

        foreach (var kvp in _properties)
        {
            if (ExchangeResources.IsOwnedByExchange(kvp.Key))
                continue;
            child._properties[kvp.Key] = kvp.Value;
        }

        child._origin = this;
        return child;
    }

    /// <inheritdoc />
    public IExchange CloneLinked()
    {
        var clone = new Exchange((Message)In.Clone())
        {
            Pattern = Pattern,
            RouteId = RouteId,
            Context = Context,
            Exception = Exception,
            ExceptionHandled = ExceptionHandled,
            _ownsScope = false,
            _scope = _scope,
            _scopeFactory = _scopeFactory
        };
        // preserve the same ExchangeId for clones
        clone.ExchangeId = ExchangeId;

        if (Out != null)
            clone.Out = Out.Clone();
        LendBodiesTo(clone);

        foreach (var kvp in _properties)
        {
            if (ExchangeResources.IsOwnedByExchange(kvp.Key))
                continue;
            clone._properties[kvp.Key] = kvp.Value;
        }

        return clone;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseScopes()
    {
        // In-progress guard, not a one-shot latch. This method is public API and downstream code
        // calls it manually in error handlers; a latch spent on that call would leave any scope
        // cached on the exchange AFTERWARDS — an error handler's tail touching redb again does
        // exactly that — invisible to DisposeAsync forever. Re-arming after each sweep keeps every
        // guarantee the latch gave (concurrent calls don't double-sweep, nothing is disposed
        // twice — entries leave the property bag as they are released and the owned scope nulls
        // out) without the mine.
        if (Interlocked.CompareExchange(ref _scopesReleased, 1, 0) != 0) return;

        try
        {
            await ReleaseScopesCore().ConfigureAwait(false);
        }
        finally
        {
            // The re-arm lives in a finally so that a throw mid-sweep cannot leave the guard
            // stuck — that would be this method's old defect made permanent.
            Interlocked.Exchange(ref _scopesReleased, 0);
        }
    }

    private async ValueTask ReleaseScopesCore()
    {
        // Grab a logger up front — ILoggerFactory is a singleton, so it survives disposing the scope
        // it's resolved from. A dispose fault below (e.g. a broken DB transaction that throws when
        // disposed) must be observable — logged, not silently eaten — and must NOT abort disposal of
        // the remaining scopes: each disposal is isolated in its own try so one throw can't strand
        // sibling scopes (which would re-introduce the very connection leak this guards against).
        ILogger? logger = null;
        // An exchange without a DI scope (a ProducerTemplate, a test) still has its context to log through.
        try
        {
            logger = (_scope?.ServiceProvider?.GetService<ILoggerFactory>() ?? Context?.GetService<ILoggerFactory>())
                ?.CreateLogger("redb.Route.Exchange");
        }
        catch { /* logger is best-effort; disposal proceeds regardless */ }

        // An exchange that entered no route of its own (a Split or Multicast branch, the copy .Threads() continues on, one
        // processed outside any route) ends its unit of work here, with the outcome it carries, while its scopes are still
        // alive. A unit of work a route opened is that route's to end — a release in the middle of it leaves it alone.
        if (_properties.TryGetValue(ExchangeUnitOfWork.PropertyKey, out var unitOfWorkValue)
            && unitOfWorkValue is ExchangeUnitOfWork { OwnedByRoute: false } unitOfWork)
            await unitOfWork.End(this, this.EndedInFailure(), logger).ConfigureAwait(false);

        // Release resources registered through ExchangeResources (a streamed query result holding its connection) first:
        // most recent first, while the DI scopes that may own their factories are still alive. Each release is isolated,
        // like the scopes below, so one failure cannot strand the others.
        var resourceKeys = _properties.Keys
            .Where(k => k.StartsWith(ExchangeResources.PropertyPrefix, StringComparison.Ordinal))
            .OrderByDescending(k => k, StringComparer.Ordinal)
            .ToList();

        foreach (var key in resourceKeys)
        {
            try
            {
                if (_properties.TryGetValue(key, out var val) && val is IAsyncDisposable resource)
                    await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Releasing resource '{ResourceKey}' failed on exchange {ExchangeId} (route '{RouteId}'); remaining resources and scopes are still released.", key, ExchangeId, RouteId);
            }
            finally
            {
                _properties.Remove(key);
            }
        }

        // Dispose named IRedbService scopes cached by RedbRouteExtensions
        // and remove them from Properties, so a repeated release has nothing to dispose twice. (The aggregation merge-back
        // of Multicast / Splitter / Scatter-Gather / Loop skips owned keys — ExchangeResources.IsOwnedByExchange.)
        var namedKeys = _properties.Keys
            .Where(k => k.StartsWith("__redb_scope:", StringComparison.Ordinal))
            .ToList();

        foreach (var key in namedKeys)
        {
            try
            {
                if (_properties.TryGetValue(key, out var val) && val is IServiceScope namedScope)
                {
                    if (namedScope is IAsyncDisposable asyncScope)
                        await asyncScope.DisposeAsync().ConfigureAwait(false);
                    else
                        namedScope.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Disposing cached IRedbService scope '{ScopeKey}' failed on exchange {ExchangeId} (route '{RouteId}'); remaining scopes are still released.", key, ExchangeId, RouteId);
            }
            finally
            {
                _properties.Remove(key);
            }
        }

        if (_ownsScope)
        {
            try
            {
                if (_scope is IAsyncDisposable ad)
                    await ad.DisposeAsync().ConfigureAwait(false);
                else
                    _scope?.Dispose();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Disposing the per-exchange DI scope failed on exchange {ExchangeId} (route '{RouteId}').", ExchangeId, RouteId);
            }
            finally
            {
                _scope = null;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The bodies this exchange owns (Stream, StreamCache, etc.): once, and not one an ancestor still carries.
        await ReleaseBodies().ConfigureAwait(false);

        // Release DI scopes if not already released via ReleaseScopes()
        await ReleaseScopes().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Hands the bodies of this exchange over to <paramref name="successor"/>, a copy that carries on with them after this
    /// exchange is done (<c>.Threads()</c>): this exchange then disposes none, the successor disposes them as their owner.
    /// </summary>
    internal void HandOverBodiesTo(Exchange successor)
    {
        HashSet<object> bodies;
        lock (_bodiesLock)
        {
            bodies = OwnedBodies();
            _lent = null;
            _bodiesReleased = true;
        }
        lock (successor._bodiesLock)
        {
            successor._origin = null;
            successor._bodiesReleased = false;
            foreach (var body in bodies)
                (successor._lent ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(body);
        }
    }

    /// <summary>
    /// A copy that carries on with this exchange after its caller is done with it (an aggregation group): it takes the
    /// bodies over and keeps the DI scope factory, without a scope of its own until <see cref="EnsureOwnScope"/>.
    /// </summary>
    internal Exchange TakeOver()
    {
        var copy = (Exchange)Clone();
        copy._scopeFactory = _scopeFactory;
        HandOverBodiesTo(copy);
        return copy;
    }

    /// <summary>Gives the exchange a DI scope of its own when it has none and a factory to make one.</summary>
    internal void EnsureOwnScope()
    {
        if (_scope is not null || _scopeFactory is null)
            return;
        _scope = _scopeFactory.CreateScope();
        _ownsScope = true;
    }

    private static bool IsDisposable(object? body) => body is IAsyncDisposable or IDisposable;

    // Called under _bodiesLock: the current disposable bodies and the ones on this exchange's account.
    private HashSet<object> OwnedBodies()
    {
        var bodies = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (IsDisposable(In.Body)) bodies.Add(In.Body!);
        if (IsDisposable(Out?.Body)) bodies.Add(Out!.Body!);
        if (_lent is not null) bodies.UnionWith(_lent);
        return bodies;
    }

    // A copy shares this exchange's bodies by reference: they stay on this exchange's account.
    private void LendBodiesTo(Exchange copy)
    {
        copy._origin = this;
        lock (_bodiesLock)
        {
            if (_bodiesReleased) return;
            if (IsDisposable(In.Body)) (_lent ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(In.Body!);
            if (IsDisposable(Out?.Body)) (_lent ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(Out!.Body!);
        }
    }

    // Takes a body on this exchange's account when it carries it or lent it and has not been released yet.
    private bool TryKeep(object body)
    {
        lock (_bodiesLock)
        {
            if (_bodiesReleased) return false;
            if (!(ReferenceEquals(In.Body, body) || ReferenceEquals(Out?.Body, body) || _lent?.Contains(body) == true))
                return false;
            (_lent ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(body);
            return true;
        }
    }

    private async ValueTask ReleaseBodies()
    {
        HashSet<object> bodies;
        lock (_bodiesLock)
        {
            if (_bodiesReleased) return;
            _bodiesReleased = true;
            bodies = OwnedBodies();
            _lent = null;
        }

        foreach (var body in bodies)
        {
            // A body an ancestor still carries or lent is the ancestor's to dispose (a copy's result aggregated into it,
            // a body merged back, the body this copy shares with it).
            var kept = false;
            for (var ancestor = _origin; ancestor is not null && !kept; ancestor = ancestor._origin)
                kept = ancestor.TryKeep(body);
            if (kept)
                continue;

            try
            {
                if (body is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    ((IDisposable)body).Dispose();
            }
            catch (Exception ex)
            {
                // Like the scopes: one body failing to dispose must not strand the others or the DI scopes after them.
                ResolveLogger()?.LogError(ex, "Disposing a message body ({BodyType}) failed on exchange {ExchangeId} (route '{RouteId}'); remaining bodies and scopes are still released.",
                    body.GetType().Name, ExchangeId, RouteId);
            }
        }
    }

    private ILogger? ResolveLogger()
    {
        try
        {
            return (_scope?.ServiceProvider?.GetService<ILoggerFactory>() ?? Context?.GetService<ILoggerFactory>())
                ?.CreateLogger("redb.Route.Exchange");
        }
        catch (ObjectDisposedException)
        {
            return null; // the scope is gone; nothing left to log through
        }
    }
}
