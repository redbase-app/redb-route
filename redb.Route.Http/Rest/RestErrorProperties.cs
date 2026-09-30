namespace redb.Route.Http.Rest;

/// <summary>
/// Exchange properties the REST request step sets before it hands a refused request to the error
/// handler named by <see cref="RestOptions.ErrorHandler"/>. The handler writes the body; the status
/// code is already in <see cref="HttpHeaders.ResponseCode"/> and may be changed by it.
/// </summary>
public static class RestErrorProperties
{
    /// <summary>Status code of the refusal (<c>int</c>): 400, 406 or 415.</summary>
    public const string Code = "redbRest.ErrorCode";

    /// <summary>Human-readable reason (<c>string</c>); the default body is this text.</summary>
    public const string Reason = "redbRest.ErrorReason";

    /// <summary>Name of the offending parameter (<c>string</c>); set for 400 only.</summary>
    public const string Parameter = "redbRest.ErrorParameter";
}
