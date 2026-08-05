using IBM.XMS;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.IbmMq;

/// <summary>
/// Deferred acknowledgement for the XMS (event-driven) receive path: commit/rollback of a
/// transacted XMS <see cref="ISession"/> (JMS <c>SESSION_TRANSACTED</c>), the XMS analogue of
/// <see cref="IbmMqAckAction"/> (which wraps MQCMIT/MQBACK on an IBM.WMQ connection).
/// <para>
/// <b>Session affinity.</b> A transacted JMS session commits/rolls back on the <i>same</i> session
/// that delivered the message. Because the XMS engine runs the whole route synchronously inside the
/// listener callback, both the route's transaction unit-of-work and the engine's own settle call
/// execute on that session's dispatch thread — so this action always commits the session that
/// delivered the message. (JMS permits <c>commit</c>/<c>rollback</c> inside <c>onMessage</c>; only
/// creating/closing the session there is forbidden.)
/// </para>
/// <para>
/// <b>Idempotent.</b> Whichever settles first wins and the other is a no-op: a route-level
/// <c>.Transaction()</c> block (via <c>TransactedProcessor</c>) settles the action as part of the
/// route unit-of-work; if the route has no transaction block, the engine settles it directly after
/// the route returns. This makes transacted listener mode correct with or without a route
/// transaction, and prevents a double commit/rollback when both paths are present.
/// </para>
/// </summary>
internal sealed class IbmMqXmsAckAction : ITransactedAction
{
    private readonly ISession _session;
    private readonly ILogger? _logger;
    private int _settled; // 0 = pending, 1 = committed/rolled back

    public IbmMqXmsAckAction(ISession session, ILogger? logger)
    {
        _session = session;
        _logger = logger;
    }

    /// <summary>True once the session has been committed or rolled back through this action.</summary>
    public bool Settled => Volatile.Read(ref _settled) != 0;

    /// <inheritdoc />
    public Task Commit(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0)
            return Task.CompletedTask; // already settled — no-op

        _session.Commit();
        _logger?.LogDebug("IBM MQ XMS ack committed (session.Commit)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Rollback(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0)
            return Task.CompletedTask; // already settled — no-op

        _session.Rollback();
        _logger?.LogDebug("IBM MQ XMS nack (session.Rollback)");
        return Task.CompletedTask;
    }
}
