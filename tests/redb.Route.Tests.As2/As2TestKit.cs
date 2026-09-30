using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit;
using redb.Route.As2;
using redb.Route.As2.Crypto;

namespace redb.Route.Tests.As2;

/// <summary>
/// Shared test material: certificates, mirrored partnerships, raw AS2 messages built with the real engine, and a
/// listener that records whatever is posted to it (the "trap" for receipt URLs that must not be called).
/// </summary>
internal static class As2TestKit
{
    /// <summary>The certificate of the loopback partnerships: both sides use it, as the loopback tests always did.</summary>
    public static readonly X509Certificate2 Cert = MakeCert("as2-kit");

    /// <summary>A certificate no partnership pins: a message signed with it did not come from the partner.</summary>
    public static readonly X509Certificate2 Stranger = MakeCert("as2-stranger");

    /// <summary>The receiving side: we are <c>US</c>, the partner is <c>THEM</c>.</summary>
    public static As2ConnectionFactory Receiver(Action<As2ConnectionFactory>? configure = null)
    {
        var f = new As2ConnectionFactory
        {
            OurCertificate = Cert, PartnerCertificate = Cert,
            As2From = "US", As2To = "THEM",
            Sign = true, Encrypt = true, SignedMdn = true, MdnMode = As2MdnMode.Sync,
        };
        configure?.Invoke(f);
        return f;
    }

    /// <summary>The sending side of the same partnership: we are <c>THEM</c>, the partner is <c>US</c>.</summary>
    public static As2ConnectionFactory Sender(Action<As2ConnectionFactory>? configure = null)
    {
        var f = new As2ConnectionFactory
        {
            OurCertificate = Cert, PartnerCertificate = Cert,
            As2From = "THEM", As2To = "US",
            Sign = true, Encrypt = true, SignedMdn = true, MdnMode = As2MdnMode.Sync,
        };
        configure?.Invoke(f);
        return f;
    }

    public static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    public static X509Certificate2 MakeCert(string cn, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certWithKey = req.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(10));
#pragma warning disable SYSLIB0057
        using var publicOnly = new X509Certificate2(certWithKey.Export(X509ContentType.Cert));
#pragma warning restore SYSLIB0057
        return publicOnly.CopyWithPrivateKey(rsa);
    }

    /// <summary>
    /// A raw AS2 body: the payload signed with <paramref name="signer"/> (none: unsigned) and encrypted for
    /// <paramref name="recipient"/> (none: not encrypted). Returns the top-level MIME headers and the body.
    /// </summary>
    public static (string ContentType, string? TransferEncoding, byte[] Body) BuildMessage(
        string payload, X509Certificate2? signer, X509Certificate2? recipient, string signAlg = "sha-256")
    {
        var engine = new As2CryptoEngine();
        MimeEntity entity = new MimePart("application", "edi-x12")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(payload))),
            ContentTransferEncoding = ContentEncoding.Binary,
        };
        if (signer is not null) entity = engine.Sign(entity, signer, signAlg);
        if (recipient is not null) entity = engine.Encrypt(entity, recipient, "aes-128-cbc");

        using var ms = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        entity.WriteTo(options, ms);
        var all = ms.ToArray();

        var sep = -1;
        for (var i = 0; i + 3 < all.Length; i++)
            if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10) { sep = i; break; }
        var body = sep >= 0 ? all[(sep + 4)..] : all;

        return (entity.Headers[HeaderId.ContentType]!, entity.Headers[HeaderId.ContentTransferEncoding], body);
    }

    /// <summary>
    /// POSTs a raw AS2 message from <c>THEM</c> to <c>US</c> that asks for a signed synchronous receipt.
    /// <paramref name="headers"/> overrides or adds headers; a null value removes one.
    /// </summary>
    public static async Task<HttpResponseMessage> PostAsync(
        int port, string path, (string ContentType, string? TransferEncoding, byte[] Body) message,
        IReadOnlyDictionary<string, string?>? headers = null, TimeSpan? timeout = null, bool expectContinue = false)
    {
        var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["AS2-Version"] = "1.2",
            ["AS2-From"] = "THEM",
            ["AS2-To"] = "US",
            ["Message-ID"] = $"<{Guid.NewGuid():N}@kit>",
            ["Disposition-Notification-To"] = "them@example.com",
            ["Disposition-Notification-Options"] = "signed-receipt-protocol=optional, pkcs7-signature; signed-receipt-micalg=optional, sha-256",
        };
        if (headers is not null)
            foreach (var (k, v) in headers) all[k] = v;

        using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        var content = new ByteArrayContent(message.Body);
        content.Headers.TryAddWithoutValidation("Content-Type", message.ContentType);
        if (message.TransferEncoding is not null)
            content.Headers.TryAddWithoutValidation("Content-Transfer-Encoding", message.TransferEncoding);
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}") { Content = content };
        // A server that refuses the body answers before it is sent, instead of resetting the connection mid-upload.
        if (expectContinue) request.Headers.ExpectContinue = true;
        foreach (var (k, v) in all)
            if (v is not null) request.Headers.TryAddWithoutValidation(k, v);

        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }
}

/// <summary>An HTTP listener that records every request posted to it and answers 200.</summary>
internal sealed class As2Trap : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public As2Trap(int port)
    {
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string key in ctx.Request.Headers) headers[key] = ctx.Request.Headers[key]!;
                Requests.Enqueue((ctx.Request.Url!.AbsolutePath, headers, ms.ToArray()));
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        });
    }

    public int Port { get; }

    public string Url(string path = "/mdn") => $"http://127.0.0.1:{Port}{path}";

    public System.Collections.Concurrent.ConcurrentQueue<(string Path, Dictionary<string, string> Headers, byte[] Body)> Requests { get; } = new();

    /// <summary>Waits up to <paramref name="within"/> for a request; true when one arrived.</summary>
    public async Task<bool> HitAsync(TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (Requests.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        return !Requests.IsEmpty;
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}
