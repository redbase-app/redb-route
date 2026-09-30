using System.Security.Cryptography.X509Certificates;
using redb.Route.Core;

namespace redb.Route.As2;

/// <summary>
/// Describes an AS2 trading partner: our key material, the partner's certificate, identifiers and the
/// agreed profile (algorithms, MDN mode). Registered by name in the route context registry and referenced
/// from a URI via <c>connectionFactory=name</c> — so certificates never live in the URI. This mirrors the
/// framework-wide ConnectionFactory pattern (RabbitMQ/Telegram/...). See <c>docs/as2/02-DESIGN.md §4</c>.
/// </summary>
public sealed class As2ConnectionFactory
{
    // ── Key material ─────────────────────────────────────────────────────────
    /// <summary>Our certificate incl. private key — signs outgoing messages and decrypts incoming ones.</summary>
    public X509Certificate2? OurCertificate { get; set; }

    /// <summary>Partner's public certificate — encrypts outgoing messages and verifies their signature.</summary>
    public X509Certificate2? PartnerCertificate { get; set; }

    // ── Identity ─────────────────────────────────────────────────────────────
    /// <summary>Our AS2 identifier (<c>AS2-From</c>).</summary>
    public string As2From { get; set; } = "";

    /// <summary>Partner AS2 identifier (<c>AS2-To</c>).</summary>
    public string As2To { get; set; } = "";

    /// <summary>Partner endpoint URL the producer POSTs to.</summary>
    public string PartnerUrl { get; set; } = "";

    // ── Profile ──────────────────────────────────────────────────────────────
    /// <summary>Sign outgoing messages. Default true.</summary>
    public bool Sign { get; set; } = true;

    /// <summary>Encrypt outgoing messages. Default true.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Compress the payload. Default false.</summary>
    public bool Compress { get; set; }

    /// <summary>Signature digest algorithm. Default sha-256.</summary>
    public string SignAlg { get; set; } = "sha-256";

    /// <summary>Encryption algorithm. Default aes-128-cbc.</summary>
    public string EncryptAlg { get; set; } = "aes-128-cbc";

    /// <summary>MDN mode. Default <see cref="As2MdnMode.Sync"/>.</summary>
    public As2MdnMode MdnMode { get; set; } = As2MdnMode.Sync;

    /// <summary>Require/produce a signed MDN. Default true.</summary>
    public bool SignedMdn { get; set; } = true;

    /// <summary>
    /// When true, a send whose MDN is negative, has a MIC mismatch, or lacks a required valid signature throws
    /// (the transfer is treated as not confirmed) instead of only logging. Default false: the outcome is
    /// surfaced on <c>redbAs2.mdn*</c> headers for the route to act on.
    /// </summary>
    public bool RequireValidMdn { get; set; }

    /// <summary>URL the partner posts asynchronous MDNs to (our receiver).</summary>
    public string? AsyncMdnUrl { get; set; }

    /// <summary>
    /// Receive side: the hosts (DNS names or IP literals, compared case-insensitively) an asynchronous MDN may be
    /// posted to. The address comes from the partner's <c>Receipt-Delivery-Option</c> header; a receipt is posted
    /// only when the message authenticated, <see cref="MdnMode"/> is <see cref="As2MdnMode.Async"/>, and the
    /// address's host is listed here. Required for a receive endpoint whose <see cref="MdnMode"/> is async.
    /// </summary>
    public IList<string> AsyncMdnAllowedHosts { get; set; } = [];

    // ── Transport security ───────────────────────────────────────────────────
    /// <summary>
    /// PFX certificate our receive server presents to the partner over TLS. Distinct from
    /// <see cref="OurCertificate"/>, which is the S/MIME key for the payload: one secures the
    /// connection, the other the message. Lives here so its password stays out of the route URI.
    /// </summary>
    public string? SslCertPath { get; set; }

    /// <summary>Password for <see cref="SslCertPath"/>.</summary>
    [Sensitive]
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// Permits the legacy algorithms <c>sha-1</c> (broken collision resistance) and <c>3des</c> (a 64-bit block,
    /// SWEET32) for <see cref="SignAlg"/> and <see cref="EncryptAlg"/>. Only for a partner that still requires them;
    /// default false, so a new agreement does not inherit weak cryptography by copy and paste.
    /// </summary>
    public bool AllowLegacyAlgorithms { get; set; }

    /// <summary>
    /// Checks the agreement; throws <see cref="InvalidOperationException"/> naming <paramref name="name"/> and what is
    /// missing or wrong: both identifiers, supported (and, unless allowed, non-legacy) algorithms, our certificate with
    /// its private key and the partner's certificate wherever something is signed or encrypted, and absolute http(s)
    /// URLs. Every endpoint naming the factory calls it when it starts; what only one side needs (a partner URL on a
    /// send, receipt hosts on an async receive) that side checks itself.
    /// </summary>
    public void Validate(string name) => As2Profile.From(this).Validate($"AS2 connection factory '{name}'");
}
