namespace redb.Route.Core;

/// <summary>
/// The one rule that tells a cancellation from a failure, and the one place an owned timeout is named as
/// one. Shared by error handling and by every component that runs under a deadline.
/// <para>
/// A cancellation is what the caller's <see cref="CancellationToken"/> asked for. An
/// <see cref="OperationCanceledException"/> raised while that token is still live is NOT a cancellation: it
/// is a timeout (an HttpClient deadline, a component's own guard) or an internal abort, and error handling
/// must see it like any other failure. Keying the pass-through on the exception type alone was the bug: an
/// HTTP producer's timeout is a <see cref="TaskCanceledException"/>, so TryCatch/Catch, OnException, Retry
/// and DeadLetterChannel all treated it as cancellation and skipped it, and the exchange died whole.
/// </para>
/// <para>
/// Error handling normalizes such an exception to <see cref="TimeoutException"/> (the original kept as
/// <see cref="Exception.InnerException"/>) so a route can name it — <c>Catch&lt;TimeoutException&gt;</c>,
/// <c>OnException&lt;TimeoutException&gt;</c>, <c>&lt;catch exceptions="System.TimeoutException"&gt;</c> —
/// instead of the symptom type <see cref="TaskCanceledException"/>. A genuine cancellation is never
/// normalized and never handled.
/// </para>
/// </summary>
public static class RouteCancellation
{
    /// <summary>
    /// True when the exception is the cancellation <paramref name="ct"/> asked for. Everything else —
    /// including an <see cref="OperationCanceledException"/> raised while <paramref name="ct"/> is still
    /// live — is a failure error handling must see.
    /// </summary>
    public static bool IsCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>
    /// True when the exception is an <see cref="OperationCanceledException"/> that is NOT the caller's
    /// cancellation: a timeout or an internal abort that error handling must treat as a failure.
    /// </summary>
    public static bool IsOwnedTimeout(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && !ct.IsCancellationRequested;

    /// <summary>
    /// True when <paramref name="exceptionType"/> is <see cref="OperationCanceledException"/> or a subtype
    /// (<see cref="TaskCanceledException"/>). Error handling never handles such a type — a genuine
    /// cancellation is filtered out and a timeout arrives as <see cref="TimeoutException"/> — so a
    /// <c>Catch&lt;T&gt;</c> / <c>OnException&lt;T&gt;</c> on one is a branch that can never fire.
    /// </summary>
    public static bool IsCancellationType(Type exceptionType)
        => typeof(OperationCanceledException).IsAssignableFrom(exceptionType);

    /// <summary>
    /// Names an owned timeout as one for error handling: an <see cref="OperationCanceledException"/> raised
    /// while <paramref name="ct"/> is still live becomes a <see cref="TimeoutException"/> carrying the
    /// original as <see cref="Exception.InnerException"/>. Anything else, a genuine cancellation included, is
    /// returned unchanged. The result is what to act on — assign to <see cref="IExchange.Exception"/>, match
    /// handlers against, or rethrow.
    /// </summary>
    /// <param name="ex">The caught exception.</param>
    /// <param name="ct">The token the operation ran under — the caller's.</param>
    /// <param name="message">Message for the produced <see cref="TimeoutException"/>; a generic one by default.</param>
    public static Exception Normalize(Exception ex, CancellationToken ct, string? message = null)
        => IsOwnedTimeout(ex, ct)
            ? new TimeoutException(
                message ?? "The operation timed out: it was cancelled by a deadline, not by the caller's token.", ex)
            : ex;
}
