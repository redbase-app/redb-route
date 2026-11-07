using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// The SOAP receiver bounds what it reads (11-XML-HARDENING, layer 1). The envelope is parsed several times on its
/// way to the route, so its structure is checked once up front against <see cref="XmlLimits.Default"/>, and again
/// after decryption: anyone can encrypt for our certificate, so the plaintext is the caller's too. A request over a
/// limit is the sender's fault and says which limit. The request body is bounded by <c>maxRequestBodySize</c>.
/// </summary>
public class SoapXmlLimitsTests
{
    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static string Nested(int levels) =>
        string.Concat(Enumerable.Repeat("<x>", levels)) + string.Concat(Enumerable.Repeat("</x>", levels));

    /// <summary>A raw envelope: SoapEnvelope.Build parses its body through SafeXml and would refuse the hostile one itself.</summary>
    private static byte[] RawEnvelope(string body, SoapVersion version)
    {
        var ns = version == SoapVersion.Soap12 ? "http://www.w3.org/2003/05/soap-envelope" : "http://schemas.xmlsoap.org/soap/envelope/";
        return System.Text.Encoding.UTF8.GetBytes($"<soap:Envelope xmlns:soap=\"{ns}\"><soap:Body>{body}</soap:Body></soap:Envelope>");
    }

    private static RouteContext Listening(int port, Func<redb.Route.Soap.Fluent.SoapConsumerBuilder, redb.Route.Soap.Fluent.SoapConsumerBuilder>? listen = null,
        SoapConnectionFactory? factory = null, Action? reached = null)
    {
        var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        if (factory is not null) ctx.AddToRegistry("server", factory);
        ctx.AddRoutes(r =>
        {
            var builder = SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port);
            if (factory is not null) builder = builder.ConnectionFactory("server");
            if (listen is not null) builder = listen(builder);
            r.From(builder).Process(e => { reached?.Invoke(); e.In.Body = "<Ack xmlns=\"urn:test\">ok</Ack>"; });
        });
        return ctx;
    }

    private static async Task<HttpResponseMessage> Post(int port, byte[] body, SoapVersion version)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(SoapEnvelope.ContentType(version, "urn:t/Op"));
        return await http.PostAsync($"http://127.0.0.1:{port}/svc", content);
    }

    private static async Task<(string? Code, string? Reason)> Fault(HttpResponseMessage response, SoapVersion version)
    {
        var parsed = SoapEnvelope.Parse(await response.Content.ReadAsByteArrayAsync(), version);
        parsed.IsFault.Should().BeTrue("a request the consumer refuses comes back as a SOAP fault");
        return (parsed.FaultCode, parsed.FaultString);
    }

    [Theory]
    [InlineData(SoapVersion.Soap11, "soap:Client")]
    [InlineData(SoapVersion.Soap12, "soap:Sender")]
    public async Task A_body_over_the_depth_limit_is_the_senders_fault_and_never_reaches_the_route(SoapVersion version, string expected)
    {
        var port = FreePort();
        var reached = false;
        await using var ctx = Listening(port, reached: () => reached = true);
        await ctx.Start();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await Post(port, RawEnvelope(Nested(100_000), version), version);
        watch.Stop();

        var (code, reason) = await Fault(response, version);
        code.Should().Be(expected);
        reason.Should().Contain(nameof(XmlLimits.MaxDepth));
        reached.Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_body_within_the_limits_reaches_the_route()
    {
        var port = FreePort();
        var reached = false;
        await using var ctx = Listening(port, reached: () => reached = true);
        await ctx.Start();

        using var response = await Post(port, SoapEnvelope.Build(Nested(100), SoapVersion.Soap11), SoapVersion.Soap11);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        reached.Should().BeTrue();
    }

    [Fact]
    public async Task An_encrypted_body_is_checked_again_after_decryption()
    {
        // The envelope is small and flat; the hostile structure is inside the ciphertext: one element with more attributes
        // than the default limit. Not depth: the patched XML library (9.0.21 on net8/net9) refuses deep plaintext itself
        // during decryption, before our check would see it.
        using var rsa = RSA.Create(2048);
        using var ours = new CertificateRequest("CN=server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var port = FreePort();
        var reached = false;
        await using var ctx = Listening(port, factory: new SoapConnectionFactory { SigningCert = ours }, reached: () => reached = true);
        await ctx.Start();

        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        var wide = "<x " + string.Join(' ', Enumerable.Range(0, XmlLimits.DefaultMaxAttributesPerElement + 10).Select(i => $"a{i}=\"v\"")) + "/>";
        doc.Load(new MemoryStream(RawEnvelope(wide, SoapVersion.Soap11)));
        SoapEncryption.EncryptBody(doc, ours, SoapVersion.Soap11);
        using var ms = new MemoryStream();
        doc.Save(ms);

        using var response = await Post(port, ms.ToArray(), SoapVersion.Soap11);

        var (code, reason) = await Fault(response, SoapVersion.Soap11);
        code.Should().Be("soap:Client");
        reason.Should().Contain(nameof(XmlLimits.MaxAttributesPerElement));
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task A_request_over_maxRequestBodySize_is_refused_by_the_server()
    {
        var port = FreePort();
        var reached = false;
        await using var ctx = Listening(port, listen: b => b.MaxRequestBodySize(64 * 1024), reached: () => reached = true);
        await ctx.Start();

        var big = SoapEnvelope.Build($"<Big xmlns=\"urn:t\">{new string('x', 200 * 1024)}</Big>", SoapVersion.Soap11);
        using var response = await Post(port, big, SoapVersion.Soap11);

        response.IsSuccessStatusCode.Should().BeFalse();
        reached.Should().BeFalse();
    }

    [Fact]
    public void The_default_request_body_bound_is_100_megabytes()
    {
        new SoapEndpointOptions().MaxRequestBodySize.Should().Be(100L * 1024 * 1024);
    }
}
