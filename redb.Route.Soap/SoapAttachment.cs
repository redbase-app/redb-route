namespace redb.Route.Soap;

/// <summary>
/// A binary attachment carried alongside the SOAP body via MTOM/XOP (<c>multipart/related</c> on the wire).
/// Routes read and write attachments on the <see cref="SoapHeaders.Attachments"/> header plane — a side
/// plane distinct from the body — mirroring Apache Camel's <c>AttachmentMessage</c>, where attachments are a
/// separate accessor rather than the message body. Reference them from the body with an XOP include:
/// <c>&lt;xop:Include xmlns:xop="http://www.w3.org/2004/08/xop/include" href="cid:{ContentId}"/&gt;</c>.
/// </summary>
/// <param name="ContentId">MIME Content-ID (without the angle brackets), referenced from the body as <c>cid:{ContentId}</c>.</param>
/// <param name="ContentType">MIME type of the binary part, e.g. <c>application/octet-stream</c> or <c>image/png</c>.</param>
/// <param name="Content">The raw bytes.</param>
public sealed record SoapAttachment(string ContentId, string ContentType, byte[] Content);
