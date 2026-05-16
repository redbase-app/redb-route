using System.Collections.Concurrent;
using System.Threading.Channels;
using redb.Route.Abstractions;

namespace redb.Route.Components;

/// <summary>
/// Shared cross-context registry for direct-vm and vm components.
/// Registered as a singleton in DI — all RouteContexts share the same instance,
/// enabling cross-context communication without static state.
/// </summary>
public class SharedVmRegistry
{
    // ── direct-vm: synchronous cross-context processors ──

    private readonly ConcurrentDictionary<string, IProcessor> _processors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a processor for a direct-vm endpoint name.</summary>
    /// <returns>true if registered; false if another consumer already holds this name.</returns>
    public bool TryRegisterProcessor(string name, IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(processor);
        return _processors.TryAdd(name, processor);
    }

    /// <summary>Unregisters the processor for a direct-vm endpoint name (only if same instance).</summary>
    public bool TryUnregisterProcessor(string name, IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(processor);
        return _processors.TryRemove(new KeyValuePair<string, IProcessor>(name, processor));
    }

    /// <summary>Gets the processor for a direct-vm endpoint name, or null if none registered.</summary>
    public IProcessor? GetProcessor(string name)
    {
        return _processors.TryGetValue(name, out var p) ? p : null;
    }

    // ── vm: asynchronous cross-context channels ──

    private readonly ConcurrentDictionary<string, Channel<IExchange>> _channels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or creates the shared channel for a vm endpoint name.</summary>
    public Channel<IExchange> GetOrCreateChannel(string name, int boundedSize = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _channels.GetOrAdd(name, _ => boundedSize > 0
            ? Channel.CreateBounded<IExchange>(new BoundedChannelOptions(boundedSize)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            })
            : Channel.CreateUnbounded<IExchange>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            }));
    }

    /// <summary>Tries to remove a channel (e.g. on last consumer stop). Returns the removed channel if any.</summary>
    public bool TryRemoveChannel(string name, out Channel<IExchange>? channel)
    {
        var result = _channels.TryRemove(name, out var ch);
        channel = ch;
        return result;
    }
}
