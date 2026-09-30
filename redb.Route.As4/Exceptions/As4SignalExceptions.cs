namespace redb.Route.As4;

/// <summary>
/// The partner answered our message with an ebMS error of severity <c>failure</c>. Carries the partner's
/// code and texts as sent; a route handles it with <c>OnException&lt;As4ErrorSignalException&gt;()</c>.
/// </summary>
public sealed class As4ErrorSignalException : Exception
{
    /// <summary>Creates the exception from the partner's error.</summary>
    public As4ErrorSignalException(string messageId, string errorCode, string? shortDescription, string? description, string? detail)
        : base($"The partner refused AS4 message '{messageId}' with {errorCode} {shortDescription}: {description ?? detail}")
    {
        MessageId = messageId;
        ErrorCode = errorCode;
        ShortDescription = shortDescription;
        Description = description;
        ErrorDetail = detail;
    }

    /// <summary>Our message the error refers to.</summary>
    public string MessageId { get; }

    /// <summary>The ebMS error code, e.g. <c>EBMS:0010</c>.</summary>
    public string ErrorCode { get; }

    /// <summary><c>eb:Error/@shortDescription</c>.</summary>
    public string? ShortDescription { get; }

    /// <summary><c>eb:Error/eb:Description</c>.</summary>
    public string? Description { get; }

    /// <summary><c>eb:Error/eb:ErrorDetail</c>.</summary>
    public string? ErrorDetail { get; }
}

/// <summary>
/// No receipt proves that the partner received our message: none arrived (<see cref="As4ErrorCode.MissingReceipt"/>),
/// or the one that did is not valid for it (<see cref="As4ErrorCode.InvalidReceipt"/>). This is the exception a route
/// redelivers on — <c>OnException&lt;As4ReceiptException&gt;().MaximumRedeliveries(n)</c> — and the message id stays
/// the same on every attempt, so the partner sees a resend, not a new message.
/// </summary>
public sealed class As4ReceiptException : As4ProcessingException
{
    /// <summary>Creates the exception.</summary>
    public As4ReceiptException(As4ErrorCode code, string messageId, string detail, Exception? innerException = null)
        : base(code, $"message '{messageId}': {detail}", innerException) => MessageId = messageId;

    /// <summary>Our message the receipt was expected for.</summary>
    public string MessageId { get; }
}
