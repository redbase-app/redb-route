namespace SerialNumbers.Core.Infrastructure;

/// <summary>
/// A well-formed message of a type this module has no route for (a production information file,
/// for example). Not a failure of the hub: the file is archived and parked for later.
/// </summary>
public sealed class UnsupportedMessageTypeException : Exception
{
    public UnsupportedMessageTypeException()
        : base("The message type has no route in this module.")
    {
    }

    public UnsupportedMessageTypeException(string message)
        : base(message)
    {
    }
}
