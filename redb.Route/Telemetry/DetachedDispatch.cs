using System.Diagnostics;
using System.Transactions;

namespace redb.Route.Telemetry;

/// <summary>
/// Support for "detached" background dispatch — pipeline work that keeps running after the
/// originating <c>Process</c> call has already returned (WireTap fire-and-forget, Debounce flush).
/// <para>
/// A detached branch runs on a thread-pool continuation that, by default, inherits the caller's
/// <see cref="System.Threading.ExecutionContext"/>. That flow leaks two ambient values that are
/// unsafe once the originating call has unwound:
/// </para>
/// <list type="bullet">
/// <item><see cref="Transaction.Current"/> — an ambient <see cref="TransactionScope"/> that has
/// very likely already completed/disposed by the time the branch runs (the route uses
/// <see cref="TransactionScopeAsyncFlowOption"/>, so the ambient transaction flows). Producer/DB
/// code in the branch would auto-enlist and throw "the current TransactionScope is already
/// complete".</item>
/// <item><see cref="Activity.Current"/> — the originating span, also already stopped, so telemetry
/// the branch emits would be parented to a span that has ended (a child that post-dates its
/// parent).</item>
/// </list>
/// <para>
/// <see cref="Capture"/> is called on the originating thread; <see cref="Enter"/> is called at the
/// top of the branch body. Together they suppress the leaked transaction and re-root the trace as a
/// fresh span <em>linked</em> to the originator — correlation is preserved without the broken parent
/// lifecycle. Non-transaction AsyncLocal state (user/auth context) is intentionally left flowing,
/// since a detached branch such as an audit usually needs it.
/// </para>
/// </summary>
internal static class DetachedDispatch
{
    /// <summary>Trace + transaction state captured on the originating thread at dispatch time.</summary>
    internal readonly struct Origin
    {
        /// <summary>Trace context of the span active at dispatch time (default if tracing is off).</summary>
        internal ActivityContext TraceLink { get; }

        /// <summary>Whether an ambient <see cref="Transaction.Current"/> was present at dispatch time.</summary>
        internal bool HadAmbientTransaction { get; }

        internal Origin(ActivityContext traceLink, bool hadAmbientTransaction)
        {
            TraceLink = traceLink;
            HadAmbientTransaction = hadAmbientTransaction;
        }
    }

    /// <summary>
    /// Captures the ambient trace context and transaction presence. Call this on the ORIGINATING
    /// thread, before the <c>Task.Run</c> / <c>ContinueWith</c> that starts the detached branch.
    /// </summary>
    internal static Origin Capture()
        => new(Activity.Current?.Context ?? default, Transaction.Current is not null);

    /// <summary>
    /// Opens a detached execution frame INSIDE the branch body: suppresses any leaked ambient
    /// transaction and starts a root span linked to the originator. Dispose at the end of the branch.
    /// </summary>
    /// <param name="origin">State captured via <see cref="Capture"/> on the originating thread.</param>
    /// <param name="operationName">Span name for the detached branch (e.g. "redb.route.wiretap").</param>
    internal static Frame Enter(Origin origin, string operationName)
    {
        // Suppress the leaked ambient transaction (only pay for the scope when one actually leaked):
        // the detached branch's producer/DB code must not enlist in a TransactionScope that has
        // already completed on the originating thread.
        TransactionScope? suppress = origin.HadAmbientTransaction
            ? new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled)
            : null;

        // Re-root the trace as a NEW root span linked to the originating trace, so the branch is not
        // an implicit child of an already-stopped parent while correlation is still preserved.
        // NOTE: StartActivity falls back to Activity.Current when parentContext is default(ActivityContext)
        // — passing `default` does NOT force a root. So null out the leaked ambient Activity first; the
        // started span then has no parent and carries the originating trace only via the ActivityLink.
        // When there is no captured context (tracing disabled, or no listener) this is a no-op.
        Activity? activity = null;
        if (origin.TraceLink != default)
        {
            var savedCurrent = Activity.Current;
            Activity.Current = null;
            activity = RouteActivitySource.Source.StartActivity(
                operationName,
                ActivityKind.Internal,
                parentContext: default,
                tags: null,
                links: new[] { new ActivityLink(origin.TraceLink) });

            // No listener sampled it → restore the ambient we cleared (nothing to re-root anyway).
            if (activity is null)
                Activity.Current = savedCurrent;
        }

        return new Frame(suppress, activity);
    }

    /// <summary>Disposable frame returned by <see cref="Enter"/>. Restores ambient state at branch end.</summary>
    internal readonly struct Frame : IDisposable
    {
        private readonly TransactionScope? _suppress;
        private readonly Activity? _activity;

        internal Frame(TransactionScope? suppress, Activity? activity)
        {
            _suppress = suppress;
            _activity = activity;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _activity?.Dispose();
            if (_suppress is not null)
            {
                _suppress.Complete();
                _suppress.Dispose();
            }
        }
    }
}
