using System.Collections.Concurrent;

namespace redb.Route.Llm.Mcp;

/// <summary>
/// Registry of <see cref="IMcpClient"/> singletons keyed by server name.
/// Populated by <see cref="McpDiscoveryService"/> on host startup; queried by
/// <see cref="McpProducer"/> at <c>tools/call</c> time.
/// </summary>
public interface IMcpRegistry
{
    /// <summary>Adds a client to the registry. Replaces any existing entry with the same name.</summary>
    void Register(IMcpClient client);

    /// <summary>Returns the client for the given server name, or null when none is registered.</summary>
    IMcpClient? GetClient(string serverName);

    /// <summary>Snapshot of all clients currently in the registry.</summary>
    IReadOnlyCollection<IMcpClient> All();

    /// <summary>Removes a client from the registry by name. Returns the removed client, or null.</summary>
    IMcpClient? Remove(string serverName);
}

/// <summary>Default in-memory thread-safe <see cref="IMcpRegistry"/>.</summary>
public sealed class McpRegistry : IMcpRegistry
{
    private readonly ConcurrentDictionary<string, IMcpClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(IMcpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients[client.ServerName] = client;
    }

    /// <inheritdoc />
    public IMcpClient? GetClient(string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return _clients.TryGetValue(serverName, out var client) ? client : null;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<IMcpClient> All() => [.. _clients.Values];

    /// <inheritdoc />
    public IMcpClient? Remove(string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return _clients.TryRemove(serverName, out var client) ? client : null;
    }
}
