namespace redb.Route.As4;

/// <summary>
/// One payload of an AS4 message.
/// <para>
/// On receipt: a message with a single payload — the common case — has it as the exchange body itself
/// (<c>byte[]</c>, or a <see cref="Stream"/> with <c>streamBody=true</c>) and its media type as
/// <c>Message.ContentType</c>; a message with several has a list of these as the body, for a route to
/// <c>.Split()</c>. With <c>payloadList=true</c> the body is always that list.
/// </para>
/// <para>
/// On sending: a body that is a list of these is sent as that many payloads, the <c>eb:PartInfo</c> elements in the
/// order of the list (the first is the leading business document); an empty list sends a message without payloads.
/// </para>
/// </summary>
/// <param name="ContentId">
/// Content-ID of the MIME part, without angle brackets. On receipt null for a payload carried in the SOAP body; on
/// sending null lets the connector generate one.
/// </param>
/// <param name="ContentType">The original (uncompressed) media type: the <c>MimeType</c> part property.</param>
/// <param name="Content">
/// On receipt the decrypted, decompressed content: a readable, seekable stream, in memory or spooled to a temporary
/// file when large. The exchange owns it and closes it when it ends (<c>ExchangeResources.ReleaseWithExchange</c>); read it within
/// the route, not after. On sending it is copied and left open; a seekable one is read from its start.
/// </param>
/// <param name="Properties">
/// The <c>eb:PartProperties</c>, by name. On sending they are written next to <c>MimeType</c> and
/// <c>CompressionType</c>, which the connector sets itself: <c>CharacterSet</c> for an XML payload, for one.
/// </param>
public sealed record As4Payload(string? ContentId, string ContentType, Stream Content, IReadOnlyDictionary<string, string> Properties);
