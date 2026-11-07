namespace redb.Route.As4;

/// <summary>
/// The business side of one leg of an agreement: which <c>eb:Service</c> and <c>eb:Action</c> a user
/// message on that leg carries. The request leg lives on <see cref="As4Partner"/> itself; the reply leg of a
/// Two-Way / Push-and-Push exchange (required by eDelivery AS4 1.16) is <see cref="As4Partner.ReplyLeg"/>.
/// </summary>
public sealed class As4Leg
{
    /// <summary><c>eb:CollaborationInfo/eb:Service</c>.</summary>
    public string? Service { get; set; }

    /// <summary><c>eb:Service/@type</c>; omitted when null.</summary>
    public string? ServiceType { get; set; }

    /// <summary><c>eb:CollaborationInfo/eb:Action</c>.</summary>
    public string? Action { get; set; }

    /// <summary>Throws when the leg cannot describe a user message.</summary>
    /// <param name="owner">Name of the agreement and leg, for the error text.</param>
    internal void Validate(string owner)
    {
        if (string.IsNullOrWhiteSpace(Service))
            throw new InvalidOperationException($"{owner}: Service is required.");
        if (string.IsNullOrWhiteSpace(Action))
            throw new InvalidOperationException($"{owner}: Action is required.");
    }
}
