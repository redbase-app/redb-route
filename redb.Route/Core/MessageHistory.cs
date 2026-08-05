using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// One entry in an exchange's message history: which route/node processed it and how long that took
/// (Apache Camel <c>org.apache.camel.MessageHistory</c>).
/// </summary>
public sealed record MessageHistoryEntry(string RouteId, string NodeId, string Label, double ElapsedMs);

/// <summary>
/// Access and formatting for the per-exchange message history (Apache Camel Message History EIP).
/// <para>
/// When enabled (globally via <c>RouteEngineOptions.EnableMessageHistory</c> or per route via
/// <c>.MessageHistory()</c>), each node is wrapped so it appends a <see cref="MessageHistoryEntry"/>
/// as the exchange passes through, stored on the exchange under <see cref="PropertyKey"/> in a
/// thread-safe queue (Split/Multicast append concurrently). The trail is dumped on failure.
/// </para>
/// </summary>
public static class MessageHistory
{
    /// <summary>Exchange property key holding the message history (Camel: <c>Exchange.MESSAGE_HISTORY</c>).</summary>
    public const string PropertyKey = "CamelMessageHistory";

    /// <summary>Returns the recorded history for the exchange (empty if none / disabled).</summary>
    public static IReadOnlyList<MessageHistoryEntry> GetEntries(IExchange exchange)
        => exchange.Properties.TryGetValue(PropertyKey, out var raw) && raw is ConcurrentQueue<MessageHistoryEntry> q
            ? q.ToArray()
            : Array.Empty<MessageHistoryEntry>();

    /// <summary>Appends an entry, creating the queue on first use.</summary>
    internal static void Append(IExchange exchange, MessageHistoryEntry entry)
    {
        // The route's first node runs single-threaded, so the queue exists before any parallel
        // (Split/Multicast) section appends to it concurrently — hence a plain check-then-add here and
        // a thread-safe ConcurrentQueue for the appends.
        if (exchange.Properties.TryGetValue(PropertyKey, out var raw) && raw is ConcurrentQueue<MessageHistoryEntry> q)
        {
            q.Enqueue(entry);
            return;
        }

        var created = new ConcurrentQueue<MessageHistoryEntry>();
        created.Enqueue(entry);
        exchange.Properties[PropertyKey] = created;
    }

    /// <summary>
    /// Renders the history as a table (columns: RouteId · Id · Processor · Elapsed/ms), the way Camel
    /// prints it when an exchange fails. Returns an empty string when there is no history.
    /// </summary>
    public static string Format(IExchange exchange)
    {
        var entries = GetEntries(exchange);
        if (entries.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Message History (routeId · id · processor · elapsed/ms):");
        foreach (var e in entries)
            sb.AppendLine($"  {e.RouteId} · {e.NodeId} · {e.Label} · {e.ElapsedMs:F3}");
        return sb.ToString().TrimEnd();
    }
}
