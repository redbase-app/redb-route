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
    public void RepeatedPartProperty_IsInvalidHeader()
    {
        // A part property named twice reaches no route: the payload list keys part properties by name.
        var xml = MessagingWriter.CreateEnvelope(SampleUserMessage(new PartInfo("cid:p1@redb.test",
            [new Property("MimeType", "text/xml", null), new Property("CharacterSet", "UTF-8", null)]))).OuterXml;
        const string find = "<eb:Property name=\"CharacterSet\">UTF-8</eb:Property>";
        xml.Should().Contain(find);

        var act = () => MessagingReader.Read(Load(xml.Replace(find, find + "<eb:Property name=\"CharacterSet\" type=\"other\">ISO-8859-1</eb:Property>")));

        act.Should().Throw<As4ProcessingException>()
            .Where(e => e.Code == As4ErrorCode.InvalidHeader)
            .WithMessage("*PartProperties*property 'CharacterSet' appears more than once*");
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

    [Theory]
    [InlineData("half")]            // stops in the middle of the deflate stream
    [InlineData("no trailer")]      // everything but the CRC-32 and the length
    [InlineData("half a trailer")]
    [InlineData("header only")]
    [InlineData("nothing")]
    [InlineData("garbage after the header")]
    public void Decompression_OfTruncatedOrDamagedGzip_IsDecompressionFailure(string damage)
    {
        // GZipStream inflates each of these without an exception, to a part of the document or to nothing.
        var whole = As4TestMessages.Gzip(System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<invoice><id>42</id></invoice>", 40))));
        var bytes = damage switch
        {
            "half" => whole[..(whole.Length / 2)],
            "no trailer" => whole[..^8],
            "half a trailer" => whole[..^4],
            "header only" => whole[..10],
            "nothing" => [],
            _ => [.. whole[..10], .. Enumerable.Repeat((byte)0x5A, whole.Length - 10)],
        };
        var part = new SwaPart("p1", "application/gzip", bytes);
        var info = new PartInfo("cid:p1", [new Property("MimeType", "text/xml", null), new Property("CompressionType", "application/gzip", null)]);

        var act = () => As4Compression.Decompress(part, info, 1_000_000, Spool);

        act.Should().Throw<As4ProcessingException>().Where(e => e.Code == As4ErrorCode.DecompressionFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200_000)]           // more than one read buffer
    public void Decompression_OfAWholeGzip_ChecksItsTrailer_AndRestoresTheContent(int length)
    {
        var content = new byte[length];
        new Random(length).NextBytes(content);
        var part = new SwaPart("p1", "application/octet-stream", content);
        var info = new PartInfo("cid:p1", As4Compression.Compress(part, Spool));

        As4Compression.Decompress(part, info, 1_000_000, Spool).Should().BeTrue();

        part.ToArray().Should().Equal(content);
    }

    [Theory]
    [InlineData("whole", true)]
    [InlineData("an empty member between", true)]
    [InlineData("a header with name, comment, extra and crc", true)]
    [InlineData("last member cut", false)]
    [InlineData("last member without its trailer", false)]
    [InlineData("first member's crc wrong", false)]
    [InlineData("bytes after the last member", false)]
    public void Decompression_OfSeveralGzipMembers_IsWalkedMemberByMember(string shape, bool whole)
    {
        // RFC 1952 §2.2: a gzip file is a series of members. GZipStream inflates them all; only a walk over the members
        // tells a whole stream from one that stops inside its last member.
        static byte[] Member(string text) => WholeGzip(System.Text.Encoding.UTF8.GetBytes(text));
        var first = Member(string.Concat(Enumerable.Repeat("first ", 2000)));
        var second = Member(string.Concat(Enumerable.Repeat("second ", 3000)));
        var expected = string.Concat(Enumerable.Repeat("first ", 2000)) + string.Concat(Enumerable.Repeat("second ", 3000));

        byte[] bytes = shape switch
        {
            "whole" => [.. first, .. second],
            "an empty member between" => [.. first, .. WholeGzip([]), .. second],
            "a header with name, comment, extra and crc" => [.. WithOptionalHeaderFields(first), .. second],
            "last member cut" => [.. first, .. second[..(second.Length / 2)]],
            "last member without its trailer" => [.. first, .. second[..^8]],
            "first member's crc wrong" => [.. first[..^8], (byte)(first[^8] ^ 0xFF), .. first[^7..], .. second],
            _ => [.. first, .. second, 0x00, 0x01, 0x02],
        };
        var part = new SwaPart("p1", "application/gzip", bytes);
        var info = new PartInfo("cid:p1", [new Property("MimeType", "text/plain", null), new Property("CompressionType", "application/gzip", null)]);

        if (whole)
        {
            As4Compression.Decompress(part, info, 1_000_000, Spool).Should().BeTrue();
            System.Text.Encoding.UTF8.GetString(part.ToArray()).Should().Be(expected);
        }
        else
        {
            var act = () => As4Compression.Decompress(part, info, 1_000_000, Spool);
            act.Should().Throw<As4ProcessingException>().Where(e => e.Code == As4ErrorCode.DecompressionFailure);
        }
    }

    /// <summary>A whole gzip member of <paramref name="content"/>, also for empty content on every runtime.</summary>
    private static byte[] WholeGzip(byte[] content)
    {
        var part = new SwaPart("m", "application/octet-stream", content);
        As4Compression.Compress(part, Spool);
        return part.ToArray();
    }

    /// <summary>The member with FEXTRA, FNAME, FCOMMENT and FHCRC set in its header (RFC 1952 §2.3.1).</summary>
    private static byte[] WithOptionalHeaderFields(byte[] member)
    {
        var header = member[..10];
        header[3] = 0x02 | 0x04 | 0x08 | 0x10;
        byte[] extra = [3, 0, (byte)'a', (byte)'b', (byte)'c'];           // XLEN = 3, then three bytes
        byte[] name = [(byte)'f', (byte)'.', (byte)'x', 0];
        byte[] comment = [(byte)'c', 0];
        // CRC16: the two least significant bytes of the CRC-32 of the header up to here.
        byte[] before = [.. header, .. extra, .. name, .. comment];
        var crc = ~redb.Route.As4.Compression.Crc32.Append(0xFFFFFFFFu, before);
        byte[] headerCrc = [(byte)crc, (byte)(crc >> 8)];
        return [.. before, .. headerCrc, .. member[10..]];
    }

    [Fact]
    public void Multipart_IsWrittenWithCrlfLineEndings_OnEveryPlatform()
    {
        // MIME lines end in CRLF (RFC 2046). MimeKit's default follows the platform, so on Linux the body was written
        // with a bare LF before each boundary: Domibus took it for the last byte of the encrypted attachment before it,
        // and the AES-GCM tag did not match. The content here has no line break of its own, so every LF in the output
        // belongs to the MIME structure.
        SwaMessage.WireFormat.NewLineFormat.Should().Be(MimeKit.NewLineFormat.Dos);

        var message = SwaMessage.Create(As4TestMessages.UserMessage("m1@a.example", "p1@a.example", moreContentIds: "p2@a.example"));
        message.AddPart("p1@a.example", "application/octet-stream", [1, 2, 3, 250, 251]);
        message.AddPart("p2@a.example", "application/octet-stream", [4, 5, 6]);
        var (contentType, body) = message.Write();

        var envelopeStart = body.AsSpan().IndexOf("<soapenv:Envelope"u8);
        var envelopeEnd = body.AsSpan().IndexOf("</soapenv:Envelope>"u8);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != (byte)'\n' || (i > envelopeStart && i < envelopeEnd)) continue;   // the envelope's own text aside
            (i > 0 && body[i - 1] == (byte)'\r').Should().BeTrue($"the line break at offset {i} must be CRLF");
        }

        // And it reads back to the same bytes.
        var read = SwaMessage.Read(contentType, body, 1024 * 1024);
        read.Parts["p1@a.example"].ToArray().Should().Equal(1, 2, 3, 250, 251);
        read.Parts["p2@a.example"].ToArray().Should().Equal(4, 5, 6);
    }

    [Fact]
    public void Compression_OfEmptyContent_IsTheEmptyGzipMember_NotZeroBytes()
    {
        // GZipStream of .NET 9 and later writes nothing for empty content; zero bytes are not gzip to any other receiver.
        var part = new SwaPart("p1", "text/xml", []);
        As4Compression.Compress(part, Spool);

        var compressed = part.ToArray();
        compressed.Should().HaveCount(20);
        compressed[..3].Should().Equal(0x1F, 0x8B, 0x08);
        As4TestMessages.Gunzip(compressed).Should().BeEmpty();
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
