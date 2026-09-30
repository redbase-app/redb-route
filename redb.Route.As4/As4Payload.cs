namespace redb.Route.As4;

/// <summary>
/// One payload of a received AS4 message with more than one. A message with a single payload — the common
/// case — has it as the exchange body itself (<c>byte[]</c>, or a <see cref="Stream"/> with <c>streamBody=true</c>)
/// and its media type as <c>Message.ContentType</c>; a message with several has a list of these as the body, for a
/// route to <c>.Split()</c>.
/// </summary>
/// <param name="ContentId">Content-ID of the MIME part, or null for a payload carried in the SOAP body.</param>
/// <param name="ContentType">The original (decompressed) media type, from the <c>MimeType</c> part property.</param>
/// <param name="Content">
/// The decrypted, decompressed content: a readable, seekable stream, in memory or spooled to a temporary file when
/// large. The exchange owns it and closes it when it ends (<c>ExchangeResources.ReleaseWithExchange</c>); read it within
/// the route, not after.
/// </param>
/// <param name="Properties">The <c>eb:PartProperties</c>, by name.</param>
public sealed record As4Payload(string? ContentId, string ContentType, Stream Content, IReadOnlyDictionary<string, string> Properties);
