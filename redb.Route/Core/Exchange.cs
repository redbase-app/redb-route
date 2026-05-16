using Microsoft.Extensions.DependencyInjection;
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
    {
        if (!_properties.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T typed)
            return typed;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    /// <inheritdoc />
    public void Stop() => _stopped = true;

    /// <inheritdoc />
    public IExchange Clone()
    {
        var clone = new Exchange((Message)In.Clone())
        {
            Pattern = Pattern,
            RouteId = RouteId,
            Exception = Exception,
            ExceptionHandled = ExceptionHandled
        };
        // preserve the same ExchangeId for clones
        clone.ExchangeId = ExchangeId;

        if (Out != null)
            clone.Out = Out.Clone();

        foreach (var kvp in _properties)
        {
            // Named redb scopes are per-exchange; child will create its own on first access.
            if (kvp.Key.StartsWith("__redb_scope:", StringComparison.Ordinal))
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
    public IExchange CreateChild(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var child = new Exchange(message)
        {
            Pattern = Pattern,
            RouteId = RouteId
        };

        foreach (var kvp in _properties)
        {
            // Named redb scopes are per-exchange; child will create its own on first access.
            if (kvp.Key.StartsWith("__redb_scope:", StringComparison.Ordinal))
                continue;
            child._properties[kvp.Key] = kvp.Value;
        }

        if (_scopeFactory != null)
        {
            child._scopeFactory = _scopeFactory;
            child._scope = _scopeFactory.CreateScope();
        }

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
            _ownsScope = false,
            _scope = _scope,
            _scopeFactory = _scopeFactory
        };

        foreach (var kvp in _properties)
        {
            if (kvp.Key.StartsWith("__redb_scope:", StringComparison.Ordinal))
                continue;
            child._properties[kvp.Key] = kvp.Value;
        }

        return child;
    }

    /// <inheritdoc />
    public IExchange CloneLinked()
    {
        var clone = new Exchange((Message)In.Clone())
        {
            Pattern = Pattern,
            RouteId = RouteId,
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

        foreach (var kvp in _properties)
        {
            if (kvp.Key.StartsWith("__redb_scope:", StringComparison.Ordinal))
                continue;
            clone._properties[kvp.Key] = kvp.Value;
        }

        return clone;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseScopes()
    {
        if (Interlocked.CompareExchange(ref _scopesReleased, 1, 0) != 0) return;

        // Dispose named IRedbService scopes cached by RedbRouteExtensions
        // and remove them from Properties so ApplyAggregation won't copy disposed scopes to parent.
        var namedKeys = _properties.Keys
            .Where(k => k.StartsWith("__redb_scope:", StringComparison.Ordinal))
            .ToList();

        foreach (var key in namedKeys)
        {
            if (_properties.TryGetValue(key, out var val) && val is IServiceScope namedScope)
            {
                if (namedScope is IAsyncDisposable asyncScope)
                    await asyncScope.DisposeAsync().ConfigureAwait(false);
                else
                    namedScope.Dispose();
            }
            _properties.Remove(key);
        }

        if (_ownsScope)
        {
            if (_scope is IAsyncDisposable ad)
                await ad.DisposeAsync().ConfigureAwait(false);
            else
                _scope?.Dispose();
            _scope = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Cleanup body resources (Stream, StreamCache, etc.)
        await DisposeBodyIfNeeded(In.Body).ConfigureAwait(false);
        if (Out is not null)
            await DisposeBodyIfNeeded(Out.Body).ConfigureAwait(false);

        // Release DI scopes if not already released via ReleaseScopes()
        await ReleaseScopes().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    private static async ValueTask DisposeBodyIfNeeded(object? body)
    {
        if (body is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (body is IDisposable disposable)
            disposable.Dispose();
    }
}
