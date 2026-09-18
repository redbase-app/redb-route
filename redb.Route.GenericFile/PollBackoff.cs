namespace redb.Route.GenericFile;

/// <summary>Outcome of a single poll, feeding the backoff counters.</summary>
public enum PollOutcome
{
    /// <summary>The poll itself threw (e.g. the server is unreachable, no directory permission).</summary>
    Error,

    /// <summary>The poll completed but created no exchange (empty directory, or every file was filtered out).</summary>
    Idle,

    /// <summary>The poll created at least one exchange.</summary>
    Success
}

/// <summary>Result of processing a single polled file, aggregated into the poll tally.</summary>
public enum FileProcessResult
{
    /// <summary>No exchange was created (age / done-file / read-lock / idempotent duplicate / unreadable).</summary>
    Skipped,

    /// <summary>An exchange was created and the route completed without an unhandled failure.</summary>
    Processed,

    /// <summary>An exchange was created but the route left an unhandled failure on it.</summary>
    Failed
}

/// <summary>Per-poll tally: how many exchanges were created and how many of those failed unhandled.</summary>
public readonly record struct PollTally(int Created, int Failed);

/// <summary>Set when a poll's outcome has just armed the skip countdown — for a single log line.</summary>
public readonly record struct BackoffTrigger(bool ByError, int Skips);

/// <summary>
/// Apache Camel <c>ScheduledPollConsumer</c>-style poll backoff: after a threshold number of consecutive
/// idle or error polls, skip the next <c>multiplier</c> polls, then reset and resume. Pure and timer-free —
/// the caller drives the loop and applies the delay.
/// <para>
/// Counters follow Camel: an error poll increments the error count and clears idle; an idle poll increments
/// idle; a successful poll clears both. When a configured threshold is met the countdown is armed to
/// <c>multiplier</c>; the countdown is consumed one skip at a time and resets the counters on its last unit.
/// </para>
/// </summary>
public sealed class PollBackoff
{
    private readonly int _multiplier;
    private readonly int _idleThreshold;
    private readonly int _errorThreshold;

    private int _idle;
    private int _error;
    private int _countdown;

    /// <summary>Creates a backoff with the given multiplier and idle/error thresholds (0 = that part disabled).</summary>
    public PollBackoff(int multiplier, int idleThreshold, int errorThreshold)
    {
        _multiplier = multiplier;
        _idleThreshold = idleThreshold;
        _errorThreshold = errorThreshold;
    }

    /// <summary>True when a multiplier and at least one threshold are configured (otherwise a no-op).</summary>
    public bool Enabled => _multiplier > 0 && (_idleThreshold > 0 || _errorThreshold > 0);

    /// <summary>
    /// Classifies a completed (non-throwing) poll from its tally. No exchange → <see cref="PollOutcome.Idle"/>.
    /// When <paramref name="backoffOnFailedExchanges"/> is set and every created exchange failed unhandled →
    /// <see cref="PollOutcome.Error"/> (the Camel-superset behaviour); otherwise → <see cref="PollOutcome.Success"/>.
    /// A poll whose own call threw is classified <see cref="PollOutcome.Error"/> by the caller, not here.
    /// </summary>
    public static PollOutcome Classify(PollTally tally, bool backoffOnFailedExchanges)
    {
        if (tally.Created == 0)
            return PollOutcome.Idle;
        if (backoffOnFailedExchanges && tally.Failed == tally.Created)
            return PollOutcome.Error;
        return PollOutcome.Success;
    }

    /// <summary>
    /// Called before each cycle. Returns true when this cycle must be skipped (no poll). Consumes one unit
    /// of the armed countdown; on the last skip it resets the counters so polling resumes on the next cycle.
    /// </summary>
    public bool ShouldSkip()
    {
        if (_countdown <= 0)
            return false;

        _countdown--;
        if (_countdown == 0)
        {
            _idle = 0;
            _error = 0;
        }
        return true;
    }

    /// <summary>
    /// Records the outcome of an actual poll. Returns a <see cref="BackoffTrigger"/> when this outcome just
    /// armed the skip countdown (so the caller can log one line), otherwise null.
    /// </summary>
    public BackoffTrigger? Record(PollOutcome outcome)
    {
        if (!Enabled)
            return null;

        switch (outcome)
        {
            case PollOutcome.Error:
                _error++;
                _idle = 0;
                break;
            case PollOutcome.Idle:
                _idle++;
                break;
            case PollOutcome.Success:
                _idle = 0;
                _error = 0;
                break;
        }

        var errorHit = _errorThreshold > 0 && _error >= _errorThreshold;
        var idleHit = _idleThreshold > 0 && _idle >= _idleThreshold;
        if (errorHit || idleHit)
        {
            _countdown = _multiplier;
            return new BackoffTrigger(errorHit, _multiplier);
        }

        return null;
    }
}
