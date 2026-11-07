namespace redb.Route.Tcp;

/// <summary>
/// Message framing mode for TCP communication.
/// Determines how messages are delimited on the wire.
/// </summary>
public enum TcpFraming
{
    /// <summary>Raw mode: entire read buffer is one message. No framing applied.</summary>
    Raw,

    /// <summary>Text-line mode: messages delimited by a configurable delimiter (default: newline).</summary>
    TextLine,

    /// <summary>Length-prefixed mode: 4-byte big-endian length header followed by payload bytes.</summary>
    LengthPrefixed
}
