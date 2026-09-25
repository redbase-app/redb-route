using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Core;
using redb.Route.Abstractions;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Two questions a fault has to answer, and the consumer used to get both wrong for a request it could
/// not read: <b>whose fault is it</b> and <b>how much do we say about why</b>.
/// <para>
/// Whose: every rejection below used to come back as <c>soap:Receiver</c> / <c>soap:Server</c>, which
/// SOAP 1.2 defines as a failure "attributable to the processing of the message rather than to the
/// contents of the message itself". That is a retry hint, and it is false here: the same bytes will fail
/// again, so a retry handler is told to loop on a request that can never succeed. A request we cannot
/// parse is <c>Sender</c> / <c>Client</c>.
/// </para>
/// <para>
/// How much: for the two parse failures, all of it. Their text describes the caller's own bytes, so
/// withholding it buys no secrecy and costs the integrator the clue. For a decryption failure, nothing:
/// the exception describes our key and our configuration, and text that varies with the cause is a
/// decryption oracle. WS-Security is explicit that a fault here "could be used as part of a denial of
/// service or cryptographic attack", and gives one code for every cause: <c>wsse:FailedCheck</c>.
/// </para>
/// </summary>
public class SoapFaultAttributionTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static X509Certificate2 SelfSigned(string cn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static async Task<(string? Code, string? Reason)> PostAndReadFault(
        int port, byte[] body, string contentType, SoapVersion version)
    {
        using var http = new HttpClient();
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        using var resp = await http.PostAsync($"http://127.0.0.1:{port}/svc", content);
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        var parsed = SoapEnvelope.Parse(bytes, version);
        parsed.IsFault.Should().BeTrue("a request the consumer refuses must come back as a SOAP fault");
        return (parsed.FaultCode, parsed.FaultString);
    }

    /// <summary>
    /// Stands up a listening SOAP route. The route body never runs in these tests: every request here is
    /// rejected before an exchange exists.
    /// </summary>
    private static RouteContext Listening(int port, string? factoryName = null,
        SoapConnectionFactory? factory = null, ILoggerFactory? loggerFactory = null)
    {
        var ctx = new RouteContext(loggerFactory: loggerFactory);
        ctx.AddComponent(new SoapComponent());
        if (factoryName is not null && factory is not null) ctx.AddToRegistry(factoryName, factory);

        ctx.AddRoutes(r =>
        {
            var listen = SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port);
            if (factoryName is not null) listen = listen.ConnectionFactory(factoryName);
            r.From(listen).Process(e => e.In.Body = "<Ack xmlns=\"urn:test\">ok</Ack>");
        });
        return ctx;
    }

    [Theory]
    [InlineData(SoapVersion.Soap11, "soap:Client")]
    [InlineData(SoapVersion.Soap12, "soap:Sender")]
    public async Task A_request_the_route_could_not_bind_is_the_senders_fault(SoapVersion version, string expected)
    {
        // MalformedRequestException is the transport-neutral "your request is wrong" signal (the
        // controller dispatcher throws it when the action's parameters do not bind). The consumer
        // must map it to a Sender fault CARRYING the message — the text describes the caller's own
        // bytes (BR-4 allows exactly that class out) — instead of a Receiver fault with the generic
        // reference text, which is a retry hint for bytes that will fail identically forever.
        var port = FreePort();
        var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r => r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port))
            .Process(_ => throw new MalformedRequestException("Malformed request for operation 'Register': boom")));
        await using var _ = ctx;
        await ctx.Start();

        var envelope = SoapEnvelope.Build("<Register xmlns=\"urn:t\"/>", version);
        var (code, reason) = await PostAndReadFault(port, envelope, SoapEnvelope.ContentType(version, "urn:t/Register"), version);

        code.Should().Be(expected, "unbindable bytes are the sender's fault, resending them cannot help");
        reason.Should().Contain("Malformed request for operation 'Register'",
            "the caller is told what is wrong with THEIR request, not handed a reference id");
    }

    [Theory]
    [InlineData(SoapVersion.Soap11, "soap:Client")]
    [InlineData(SoapVersion.Soap12, "soap:Sender")]
    public async Task An_envelope_carrying_a_dtd_is_refused_before_its_entities_expand(SoapVersion version, string expected)
    {
        // SOAP forbids a document type declaration in an envelope, and for good reason: nested internal
        // entities expand in memory long before anything validates the message. 452 bytes become 300 000
        // characters at these settings, and the real attack does not stop at five levels. Reported while
        // preparing the AS4 connector, 2026-09-25.
        var port = FreePort();
        await using var ctx = Listening(port);
        await ctx.Start();

        var sb = new System.Text.StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\">");
        for (var i = 1; i <= 5; i++)
        {
            sb.Append($"<!ENTITY lol{i} \"");
            for (var j = 0; j < 10; j++) sb.Append(i == 1 ? "&lol;" : $"&lol{i - 1};");
            sb.Append("\">");
        }
        var ns = version == SoapVersion.Soap12 ? "http://www.w3.org/2003/05/soap-envelope" : "http://schemas.xmlsoap.org/soap/envelope/";
        sb.Append($"]><soap:Envelope xmlns:soap=\"{ns}\"><soap:Body><lolz>&lol5;</lolz></soap:Body></soap:Envelope>");

        var (code, _) = await PostAndReadFault(
            port, System.Text.Encoding.UTF8.GetBytes(sb.ToString()), SoapEnvelope.ContentType(version, "urn:t/Op"), version);

        code.Should().Be(expected, "an envelope that SOAP does not allow is the sender's fault");
    }

    [Theory]
    [InlineData(SoapVersion.Soap11, "soap:Client")]
    [InlineData(SoapVersion.Soap12, "soap:Sender")]
    public async Task An_unparseable_envelope_is_the_callers_fault_not_ours(SoapVersion version, string expected)
    {
        var port = FreePort();
        await using var ctx = Listening(port);
        await ctx.Start();

        var (code, reason) = await PostAndReadFault(
            port, "this is not an envelope"u8.ToArray(), SoapEnvelope.ContentType(version, null), version);

        code.Should().Be(expected, "the same bytes will fail again, so telling the caller to retry is a lie");
        reason.Should().Contain("Malformed SOAP request",
            "the parser is describing the caller's own bytes, which is the one clue that finds a BOM " +
            "or a truncated stream, and discloses nothing about this process");
    }

    [Fact]
    public async Task A_malformed_MTOM_request_is_the_callers_fault_not_ours()
    {
        var port = FreePort();
        await using var ctx = Listening(port);
        await ctx.Start();

        // A boundary is declared and no part carries it: SoapMultipart rejects it before the envelope exists.
        var (code, reason) = await PostAndReadFault(
            port, "nothing here matches the boundary"u8.ToArray(),
            "multipart/related; boundary=\"mtom-x\"; type=\"application/xop+xml\"; start-info=\"text/xml\"",
            SoapVersion.Soap11);

        code.Should().Be("soap:Client");
        reason.Should().Contain("Malformed MTOM request");
    }

    /// <summary>
    /// The one place where the message is withheld, and the code says so: <c>wsse:FailedCheck</c> is what
    /// WS-Security defines for "the signature or decryption was invalid".
    /// </summary>
    [Fact]
    public async Task A_decryption_failure_names_no_reason_and_leaks_no_exception_text()
    {
        var port = FreePort();
        var logs = new CapturingLogs();
        using var loggerFactory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(logs); });

        using var theirs = SelfSigned("client");
        using var ours = SelfSigned("server");

        await using var ctx = Listening(port, "server", new SoapConnectionFactory { SigningCert = ours }, loggerFactory);
        await ctx.Start();

        // Encrypted to a certificate whose private key this server does not hold.
        var (code, reason) = await PostAndReadFault(
            port, EncryptedTo(theirs), SoapEnvelope.ContentType(SoapVersion.Soap11, null), SoapVersion.Soap11);

        code.Should().Be("wsse:FailedCheck",
            "WS-Security gives exactly one code for every way a decryption can fail");
        reason.Should().Contain("ref: ", "the caller still needs something to quote when it asks us why");

        // The real exception must have reached the log, and none of its words the caller.
        var logged = logs.Exceptions.Should().ContainSingle(
            "the operator is the one who gets to know why decryption failed").Subject;
        reason.Should().NotContain(logged.Message, "that text describes our key, not the caller's bytes");
        reason.Should().NotContain(logged.GetType().Name);
    }

    /// <summary>
    /// The anti-oracle assertion, and the reason the message is withheld rather than merely trimmed: an
    /// attacker who can vary the ciphertext must not be able to tell one failure from another. Two
    /// genuinely different causes, a key we do not hold and ciphertext that has been tampered with, have
    /// to come back identical once the per-request reference is masked.
    /// </summary>
    [Fact]
    public async Task A_decryption_failure_reads_the_same_whatever_actually_broke()
    {
        var port = FreePort();
        using var theirs = SelfSigned("client");
        using var ours = SelfSigned("server");

        await using var ctx = Listening(port, "server", new SoapConnectionFactory { SigningCert = ours });
        await ctx.Start();

        var ct = SoapEnvelope.ContentType(SoapVersion.Soap11, null);
        var wrongKey = await PostAndReadFault(port, EncryptedTo(theirs), ct, SoapVersion.Soap11);
        var tampered = await PostAndReadFault(port, EncryptedTo(ours, corrupt: true), ct, SoapVersion.Soap11);

        // Both really did fail for different reasons, otherwise this test compares one cause with itself.
        DecryptFailureCause(ours, corrupt: false).Should().NotBe(DecryptFailureCause(ours, corrupt: true),
            "the two inputs must break the decryptor in different ways for the comparison below to mean anything");

        tampered.Code.Should().Be(wrongKey.Code);
        Mask(tampered.Reason).Should().Be(Mask(wrongKey.Reason),
            "differing text per cause is exactly what a padding oracle reads");

        static string? Mask(string? reason) => reason is null ? null : Regex.Replace(reason, "ref: [^)]*", "ref: X");
    }

    /// <summary>
    /// An envelope whose Body is encrypted to <paramref name="cert"/>, optionally with the ciphertext
    /// tampered with after the fact.
    /// </summary>
    private static byte[] EncryptedTo(X509Certificate2 cert, bool corrupt = false)
    {
        var doc = Encrypted(cert, corrupt);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static System.Xml.XmlDocument Encrypted(X509Certificate2 cert, bool corrupt)
    {
        var envelope = SoapEnvelope.Build("<Secret xmlns=\"urn:test\"><n>1</n></Secret>", SoapVersion.Soap11);
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(envelope));
        SoapEncryption.EncryptBody(doc, cert, SoapVersion.Soap11);

        if (corrupt)
        {
            var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("soap", "http://schemas.xmlsoap.org/soap/envelope/");
            ns.AddNamespace("xenc", SoapEncryption.XmlEncNs);

            var cipher = doc.SelectSingleNode("//soap:Body/xenc:EncryptedData//xenc:CipherValue", ns)!;
            var raw = Convert.FromBase64String(cipher.InnerText);
            raw[^1] ^= 0xFF;                        // still valid base64, no longer valid ciphertext
            cipher.InnerText = Convert.ToBase64String(raw);
        }
        return doc;
    }

    /// <summary>
    /// What the decryptor actually raises for this input, used only to prove the two inputs of the oracle
    /// test break it in different ways.
    /// </summary>
    private static string DecryptFailureCause(X509Certificate2 ours, bool corrupt)
    {
        using var other = SelfSigned("nobody");
        var doc = Encrypted(corrupt ? ours : other, corrupt);
        try
        {
            SoapEncryption.DecryptBody(doc, ours);
            return "<no failure>";
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly List<Exception> _exceptions = [];

        public IReadOnlyList<Exception> Exceptions { get { lock (_exceptions) return [.. _exceptions]; } }

        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                if (ex is not null) lock (owner._exceptions) owner._exceptions.Add(ex);
            }
        }
    }
}
