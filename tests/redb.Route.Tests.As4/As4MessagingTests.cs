using System.Text;
using System.Xml;
using redb.Route.As4;
using redb.Route.As4.Compression;
using redb.Route.As4.Messaging;
using redb.Route.As4.Mime;
using redb.Route.Core;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф2: the ebMS header model — written and read back, read from real Holodeck output, and refused with
/// EBMS:0009 and a path for every structural violation; AS4 compression; payload/part consistency.
/// </summary>
public class As4MessagingTests
{
    private static readonly redb.Route.Configuration.StreamCacheOptions Spool = new();

    private static UserMessage SampleUserMessage(params PartInfo[] parts) => new(
        new MessageInfo(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), "m-1@redb.test", null),
        new Party([new PartyId("urn:redb:sender", "urn:oasis:names:tc:ebcore:partyid-type:unregistered")], As4Partner.InitiatorRole),
        new Party([new PartyId("urn:redb:receiver", null)], As4Partner.ResponderRole),
        new CollaborationInfo(new AgreementRef("urn:agreement", null, "pm-1"), new Service("urn:service", "urn:types"), "Submit", "c-1"),
        [new Property("originalSender", "C1", null), new Property("finalRecipient", "C4", null)],
        parts,
        null);

    // ── Round trip ───────────────────────────────────────────────────────────

    [Fact]
    public void UserMessage_WrittenAndRead_IsTheSame()
    {
        var original = SampleUserMessage(new PartInfo("cid:p1@redb.test",
            [new Property("MimeType", "text/xml", null), new Property("CompressionType", "application/gzip", null)]));

        var read = MessagingReader.Read(Reparse(MessagingWriter.CreateEnvelope(original)));

        read.SignalMessages.Should().BeEmpty();
        read.UserMessages.Should().ContainSingle().Which.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void ErrorSignal_WrittenAndRead_IsTheSame()
    {
        var error = new EbmsError("EBMS:0004", As4ErrorSeverity.Failure, "Content", "ebms", "m-1@redb.test", "Other",
            "The message could not be processed.", "ref: 42");
        var signal = new SignalMessage(new MessageInfo(DateTimeOffset.UtcNow, "s-1@redb.test", "m-1@redb.test"), null, [error], null);

        var read = MessagingReader.Read(Reparse(MessagingWriter.CreateEnvelope(signal)));

        var errors = read.SignalMessages.Should().ContainSingle().Which.Errors;
        errors.Should().ContainSingle().Which.Should().BeEquivalentTo(error);
    }

    [Fact]
    public void Envelope_DeclaresItsNamespaces_OnTheRoot()
    {
        var doc = MessagingWriter.CreateEnvelope(SampleUserMessage());

        doc.DocumentElement!.GetAttribute("xmlns:soapenv").Should().Be(EbmsNamespaces.Soap12);
        doc.DocumentElement.GetAttribute("xmlns:eb").Should().Be(EbmsNamespaces.Eb);
    }

    [Fact]
    public void MessageIdFactory_UsesTheGivenHost_AndIsUnique()
    {
        var a = MessageIdFactory.New("ap.example");
        var b = MessageIdFactory.New("ap.example");

        a.Should().EndWith("@ap.example").And.NotBe(b);
        a.Should().NotContain("<").And.NotContain(">");
    }

    // ── Holodeck output ──────────────────────────────────────────────────────

    [Fact]
    public void HolodeckError_WithOddPrefixes_IsRead()
    {
        // Holodeck 8.1.1 answer to an unknown agreement (probe 2026-09-23): eb3 prefix, mustUnderstand in a
        // namespace declared under the prefix "mustUnderstand", the reason in eb:ErrorDetail.
        const string xml = """
            <soapenv:Envelope xmlns:soapenv="http://www.w3.org/2003/05/soap-envelope" xmlns:eb3="http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/"><soapenv:Header><eb3:Messaging xmlns:mustUnderstand="http://www.w3.org/2003/05/soap-envelope" mustUnderstand:mustUnderstand="true"><eb3:SignalMessage><eb3:MessageInfo><eb3:Timestamp>2026-09-23T20:31:10.574Z</eb3:Timestamp><eb3:MessageId>d049e8f4@3062a5da4fb4</eb3:MessageId><eb3:RefToMessageId>probe-1@redb</eb3:RefToMessageId></eb3:MessageInfo><eb3:Error errorCode="EBMS:0010" severity="failure" origin="ebms" category="Processing" refToMessageInError="probe-1@redb" shortDescription="ProcessingModeMismatch"><eb3:ErrorDetail>Can not process message because no P-Mode was found for the message!</eb3:ErrorDetail></eb3:Error></eb3:SignalMessage></eb3:Messaging></soapenv:Header><soapenv:Body/></soapenv:Envelope>
            """;

        var signal = MessagingReader.Read(Load(xml)).SignalMessages.Single();

        signal.MessageInfo.RefToMessageId.Should().Be("probe-1@redb");
        var error = signal.Errors.Single();
        error.ErrorCode.Should().Be("EBMS:0010");
        error.Severity.Should().Be(As4ErrorSeverity.Failure);
        error.ErrorDetail.Should().StartWith("Can not process message");
        As4ErrorCode.Find(error.ErrorCode).Should().Be(As4ErrorCode.ProcessingModeMismatch);
    }

    [As4StandFact(@"captures\push-signed-encrypted-gzip")]
    public void HolodeckRequestAndReceipt_AreRead()
    {
        var (requestBody, requestType) = As4Stand.Capture("push-signed-encrypted-gzip", "request");
        var request = MessagingReader.Read(SwaMessage.Read(requestType, requestBody, 1024 * 1024).Envelope);
        var (responseBody, responseType) = As4Stand.Capture("push-signed-encrypted-gzip", "response");
        var response = MessagingReader.Read(SwaMessage.Read(responseType, responseBody, 1024 * 1024).Envelope);

        var user = request.UserMessages.Single();
        user.From.PartyIds.Single().Value.Should().Be("org:holodeckb2b:example:company:A");
        user.CollaborationInfo.Action.Should().Be("StoreMessage");
        user.PropertyValue("originalSender").Should().NotBeNull();
        user.PayloadInfo.Single().PropertyValue("CompressionType").Should().Be("application/gzip");

        var receipt = response.SignalMessages.Single();
        receipt.IsReceipt.Should().BeTrue();
        receipt.MessageInfo.RefToMessageId.Should().Be(user.MessageInfo.MessageId);
        receipt.ReceiptContent!.Single().LocalName.Should().Be("NonRepudiationInformation");
    }

    // ── Structural violations → EBMS:0009 ────────────────────────────────────

    [Theory]
    [InlineData("<eb:Action>Submit</eb:Action>", "", "*Action is missing*")]
    [InlineData("<eb:ConversationId>c-1</eb:ConversationId>", "<eb:ConversationId>c-1</eb:ConversationId><eb:ConversationId>c-2</eb:ConversationId>", "*ConversationId appears more than once*")]
    [InlineData("<eb:Timestamp>2026-09-25T12:00:00.000Z</eb:Timestamp>", "<eb:Timestamp>yesterday</eb:Timestamp>", "*Timestamp*not an xsd:dateTime*")]
    [InlineData("<eb:MessageId>m-1@redb.test</eb:MessageId>", "<eb:MessageId> </eb:MessageId>", "*MessageId*is empty*")]
    [InlineData("<eb:Action>Submit</eb:Action>", "<eb:Action>Submit</eb:Action><eb:Extra/>", "*unexpected element*Extra*")]
    [InlineData("<eb:Property name=\"finalRecipient\">C4</eb:Property>", "<eb:Property name=\"originalSender\">again</eb:Property>", "*property 'originalSender' appears more than once*")]
    public void MalformedHeader_IsInvalidHeader_WithThePath(string find, string replace, string message)
    {
        var xml = MessagingWriter.CreateEnvelope(SampleUserMessage()).OuterXml;
        xml.Should().Contain(find, "the fixture must contain the element the case edits");

        var act = () => MessagingReader.Read(Load(xml.Replace(find, replace)));

        act.Should().Throw<As4ProcessingException>()
            .Where(e => e.Code == As4ErrorCode.InvalidHeader)
            .WithMessage(message);
    }

    [Fact]
    public void TwoUserMessages_AreInvalid()
    {
        var xml = MessagingWriter.CreateEnvelope(SampleUserMessage()).OuterXml;
        var user = xml[xml.IndexOf("<eb:UserMessage", StringComparison.Ordinal)..(xml.IndexOf("</eb:UserMessage>", StringComparison.Ordinal) + "</eb:UserMessage>".Length)];

        var act = () => MessagingReader.Read(Load(xml.Replace(user, user + user)));

        act.Should().Throw<As4ProcessingException>().WithMessage("*more than one user message*");
    }

    [Fact]
    public void MessagingOutsideTheHeader_IsNotTheHeader()
    {
        var xml = MessagingWriter.CreateEnvelope(SampleUserMessage()).OuterXml;
        var start = xml.IndexOf("<eb:Messaging", StringComparison.Ordinal);
        var end = xml.IndexOf("</eb:Messaging>", StringComparison.Ordinal) + "</eb:Messaging>".Length;
        var moved = xml.Remove(start, end - start).Replace("<soapenv:Body />", "<soapenv:Body>" + xml[start..end] + "</soapenv:Body>");

        var act = () => MessagingReader.Read(Load(moved));

        act.Should().Throw<As4ProcessingException>().WithMessage("*Messaging is missing*");
    }

    // ── Compression ──────────────────────────────────────────────────────────

    [Fact]
    public void Compression_RoundTrip_RestoresContentAndType()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 10_000));
        var part = new SwaPart("p1", "text/xml", payload);

        var properties = As4Compression.Compress(part, Spool);
        part.ContentType.Should().Be("application/gzip");
        part.Length.Should().BeLessThan(payload.Length);

        As4Compression.Decompress(part, new PartInfo("cid:p1", properties), maxDecompressedBytes: 1_000_000, Spool).Should().BeTrue();
        part.ToArray().Should().Equal(payload);
        part.ContentType.Should().Be("text/xml");
    }

    [Fact]
    public void Decompression_BeyondTheLimit_IsDecompressionFailure()
    {
        var part = new SwaPart("p1", "text/plain", new byte[5_000_000]);   // zeros compress to almost nothing
        var properties = As4Compression.Compress(part, Spool);

        var act = () => As4Compression.Decompress(part, new PartInfo("cid:p1", properties), maxDecompressedBytes: 1_000_000, Spool);

        act.Should().Throw<As4ProcessingException>().Where(e => e.Code == As4ErrorCode.DecompressionFailure);
    }

    [Fact]
    public void Decompression_OfNotGzip_IsDecompressionFailure()
    {
        var part = new SwaPart("p1", "application/gzip", [1, 2, 3, 4, 5]);
        var info = new PartInfo("cid:p1", [new Property("MimeType", "text/xml", null), new Property("CompressionType", "application/gzip", null)]);

        var act = () => As4Compression.Decompress(part, info, 1_000_000, Spool);

        act.Should().Throw<As4ProcessingException>().Where(e => e.Code == As4ErrorCode.DecompressionFailure);
    }

    [Fact]
    public void Decompression_WithoutOriginalMimeType_IsValueInconsistent()
    {
        var part = new SwaPart("p1", "text/xml", [1]);
        As4Compression.Compress(part, Spool);

        var act = () => As4Compression.Decompress(part, new PartInfo("cid:p1", [new Property("CompressionType", "application/gzip", null)]), 1_000_000, Spool);

        act.Should().Throw<As4ProcessingException>().Where(e => e.Code == As4ErrorCode.ValueInconsistent);
    }

    [Fact]
    public void UncompressedPart_IsLeftAlone()
    {
        var part = new SwaPart("p1", "text/xml", [1, 2]);

        As4Compression.Decompress(part, new PartInfo("cid:p1", []), 1_000_000, Spool).Should().BeFalse();
        part.ToArray().Should().Equal(1, 2);
    }

    // ── Payload consistency → EBMS:0007 / 0011 ───────────────────────────────

    [Fact]
    public void Consistency_PairsEveryReferenceWithItsPart()
    {
        var swa = SwaMessage.Create(MessagingWriter.CreateEnvelope(SampleUserMessage()));
        swa.AddPart("p1@redb.test", "application/gzip", [1]);

        var pairs = PayloadConsistency.Match(SampleUserMessage(new PartInfo("cid:p1@redb.test", [])), swa);

        pairs.Should().ContainSingle().Which.Part!.ContentId.Should().Be("p1@redb.test");
    }

    [Fact]
    public void Consistency_ReferenceWithoutPart_IsMimeInconsistency()
    {
        var swa = SwaMessage.Create(MessagingWriter.CreateEnvelope(SampleUserMessage()));

        var act = () => PayloadConsistency.Match(SampleUserMessage(new PartInfo("cid:missing@redb.test", [])), swa);

        act.Should().Throw<As4ProcessingException>().Where(e => e.Code == As4ErrorCode.MimeInconsistency).WithMessage("*missing@redb.test*");
    }

    [Fact]
    public void Consistency_UnreferencedPart_IsMimeInconsistency()
    {
        var swa = SwaMessage.Create(MessagingWriter.CreateEnvelope(SampleUserMessage()));
        swa.AddPart("stray@redb.test", "application/octet-stream", [1]);

        var act = () => PayloadConsistency.Match(SampleUserMessage(), swa);

        act.Should().Throw<As4ProcessingException>().Where(e => e.Code == As4ErrorCode.MimeInconsistency).WithMessage("*stray@redb.test*");
    }

    [Fact]
    public void Consistency_ExternalReference_IsExternalPayloadError()
    {
        var swa = SwaMessage.Create(MessagingWriter.CreateEnvelope(SampleUserMessage()));

        var act = () => PayloadConsistency.Match(SampleUserMessage(new PartInfo("https://example.org/payload", [])), swa);

        act.Should().Throw<As4ProcessingException>().Where(e => e.Code == As4ErrorCode.ExternalPayloadError);
    }

    [Fact]
    public void Consistency_TooManyParts_IsRefused()
    {
        var swa = SwaMessage.Create(MessagingWriter.CreateEnvelope(SampleUserMessage()));
        swa.AddPart("a", "application/octet-stream", [1]);
        swa.AddPart("b", "application/octet-stream", [1]);

        var act = () => PayloadConsistency.Match(SampleUserMessage(new PartInfo("cid:a", []), new PartInfo("cid:b", [])), swa, maxParts: 1);

        act.Should().Throw<As4ProcessingException>().WithMessage("*at most 1*");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static XmlDocument Reparse(XmlDocument doc) => Load(doc.OuterXml);

    private static XmlDocument Load(string xml) => SafeXml.LoadDocument(Encoding.UTF8.GetBytes(xml));
}
