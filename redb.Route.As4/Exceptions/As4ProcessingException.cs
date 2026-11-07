namespace redb.Route.As4;

/// <summary>
/// A received AS4 message cannot be processed for a reason ebMS names with a code. The receiver answers it with
/// an <c>eb:Error</c> carrying <see cref="Code"/> and its fixed partner text; <see cref="Exception.Message"/>
/// — the detail — is ours and goes to the log only (BR-4).
/// </summary>
public class As4ProcessingException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The ebMS error code to answer with.</param>
    /// <param name="detail">What exactly was wrong, for our log; never sent to the partner.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public As4ProcessingException(As4ErrorCode code, string detail, Exception? innerException = null)
        : base($"{code.Code} {code.ShortDescription}: {detail}", innerException)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Detail = detail;
    }

    /// <summary>The ebMS error code.</summary>
    public As4ErrorCode Code { get; }

    /// <summary>What exactly was wrong, for our log.</summary>
    public string Detail { get; }
}
