using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Where routing picks up after a failure an <c>OnException ... Continued()</c> handler suppressed (Apache Camel's
/// <c>continued(true)</c>).
/// <para>
/// Camel attaches its error handler to every step, so a continued failure returns to the pipeline that was running it
/// and the pipeline moves on to the next step by itself. Here the handler wraps the whole route, and by the time it runs
/// the stack that knew where the failure happened is gone: every pipeline the exception unwinds through records the step
/// it stopped at — innermost first — and the handler replays the rest of them in that order.
/// </para>
/// <para>
/// The points belong to one failure: another failure starts its own list, so a set nobody replayed cannot be taken for
/// somebody else's. Work that must not be replayed — a transacted block that rolled back, a <c>DoTry</c> body whose
/// failure was caught — is forgotten by the step that swallowed it.
/// </para>
/// </summary>
internal sealed class ResumePoints
{
    /// <summary>Exchange property holding the points. Owned by the exchange: copies do not inherit it.</summary>
    internal const string PropertyKey = "__redb_resume";

    private readonly List<(PipelineProcessor Pipeline, int NextIndex)> _points = [];

    private ResumePoints(Exception failure) => Failure = failure;

    private Exception Failure { get; }

    /// <summary>Remembers that <paramref name="pipeline"/> stopped at <paramref name="nextIndex"/> minus one.</summary>
    internal static void Record(IExchange exchange, Exception failure, PipelineProcessor pipeline, int nextIndex)
    {
        var points = Of(exchange, failure);
        if (points is null)
        {
            points = new ResumePoints(failure);
            exchange.Properties[PropertyKey] = points;
        }
        points._points.Add((pipeline, nextIndex));
    }

    /// <summary>
    /// Forgets what has been recorded for this failure so far: the steps it covers are not to be replayed. A transacted
    /// block that rolled back and a caught <c>DoTry</c> body use it — what they did is undone or deliberately abandoned.
    /// </summary>
    internal static void Forget(IExchange exchange, Exception failure) => Of(exchange, failure)?._points.Clear();

    /// <summary>
    /// Replays the remaining steps of every pipeline the failure unwound through, innermost first. A step that fails
    /// again throws from here, and the caller — the route's exception handler — matches it against its handlers anew.
    /// </summary>
    internal static async Task ResumeAsync(IExchange exchange, Exception failure, CancellationToken ct)
    {
        if (Of(exchange, failure) is not { } points)
            return;

        exchange.Properties.Remove(PropertyKey);
        foreach (var (pipeline, nextIndex) in points._points)
        {
            if (exchange.IsStopped)
                break;
            await pipeline.ProcessFrom(exchange, nextIndex, ct).ConfigureAwait(false);
        }
    }

    private static ResumePoints? Of(IExchange exchange, Exception failure) =>
        exchange.Properties.TryGetValue(PropertyKey, out var value)
        && value is ResumePoints points && ReferenceEquals(points.Failure, failure)
            ? points
            : null;
}
