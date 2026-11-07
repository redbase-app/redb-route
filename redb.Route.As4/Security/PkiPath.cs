using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace redb.Route.As4.Security;

/// <summary>
/// What a partner's signature is trusted by when its certificate is not pinned: the trust anchors a certification path
/// must lead to, and the certificate policies the certificates of the path must assert (eDelivery AS4 1.16 §4.3.3).
/// </summary>
/// <param name="Anchors">Certification authority certificates, roots or intermediate ones ("certificate path prefixes").</param>
/// <param name="Policies">Certificate policy OIDs every certificate below the anchor must assert; empty for none.</param>
internal sealed record PkiPathTrust(IReadOnlyCollection<X509Certificate2> Anchors, IReadOnlyCollection<string> Policies);

/// <summary>
/// The <c>X509PKIPathv1</c> security token (WS-Security X.509 Token Profile 1.1 §3.1.2): an ordered list of X.509
/// certificates packaged in a PKIPath, <c>PkiPath ::= SEQUENCE OF Certificate</c> (ITU-T X.509), where each
/// certificate is issued by the one before it — the signing certificate is the last. A receiver that does not hold the
/// signer's certificate validates the path up to an authority it trusts.
/// </summary>
internal static class PkiPath
{
    /// <summary>Token type of a <c>wsse:BinarySecurityToken</c> carrying a PKIPath, and of a <c>wsse:Reference</c> to one.</summary>
    public const string ValueType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-x509-token-profile-1.0#X509PKIPathv1";

    /// <summary>A path is the signer and its authorities; more certificates than this is not one.</summary>
    public const int MaxLength = 10;

    private const string CertificatePoliciesOid = "2.5.29.32";
    private const string AnyPolicyOid = "2.5.29.32.0";

    /// <summary>The DER encoding of <paramref name="path"/>, given from the authority's side to the signer.</summary>
    public static byte[] Encode(IReadOnlyList<X509Certificate2> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (var certificate in path)
                writer.WriteEncodedValue(certificate.RawData);
        }
        return writer.Encode();
    }

    /// <summary>
    /// The certificates of a DER-encoded PKIPath, in its order: the signer is the last. The caller owns them: what it
    /// keeps of the path it disposes of when done, the rest at once (<see cref="Release"/>).
    /// </summary>
    public static IReadOnlyList<X509Certificate2> Decode(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
        var path = new List<X509Certificate2>();
        try
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            reader.ThrowIfNotEmpty();

            while (sequence.HasData)
            {
                if (path.Count == MaxLength)
                    throw new CryptographicException($"The PKIPath token holds more than {MaxLength} certificates.");
                path.Add(Load(sequence.ReadEncodedValue().ToArray()));
            }
            if (path.Count == 0)
                throw new CryptographicException("The PKIPath token holds no certificate.");
            return path;
        }
        catch (Exception e)
        {
            Release(path, keep: null);
            if (e is AsnContentException)
                throw new CryptographicException("The PKIPath token is not a DER sequence of certificates.", e);
            throw;
        }
    }

    /// <summary>Disposes of the certificates of a decoded path, all but <paramref name="keep"/> when it is one of them.</summary>
    public static void Release(IReadOnlyList<X509Certificate2> path, X509Certificate2? keep)
    {
        foreach (var certificate in path)
            if (!ReferenceEquals(certificate, keep))
                certificate.Dispose();
    }

    /// <summary>
    /// The path of <paramref name="signer"/> from its outermost known authority down to itself, built from
    /// <paramref name="authorities"/> given in any order. An authority that issued none of the others is left out.
    /// </summary>
    public static IReadOnlyList<X509Certificate2> Order(X509Certificate2 signer, IEnumerable<X509Certificate2> authorities)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var remaining = (authorities ?? []).Where(a => a.Thumbprint != signer.Thumbprint).ToList();
        var path = new List<X509Certificate2> { signer };
        while (path.Count < MaxLength && !IsSelfIssued(path[0]))
        {
            var issuer = remaining.FirstOrDefault(a => SameName(a.SubjectName, path[0].IssuerName) && Issued(a, path[0]));
            if (issuer is null) break;
            remaining.Remove(issuer);
            path.Insert(0, issuer);
        }
        return path;
    }

    /// <summary>
    /// Throws unless <paramref name="path"/> is a certification path whose signer chains to one of the trust anchors: each
    /// certificate is issued by the one before it and every issuer is a certification authority allowed to sign
    /// certificates, and from the anchor down (RFC 5280 §6.1) every authority allows the authorities below it, the anchor
    /// and every certificate below it are within their validity period, the certificates below the anchor assert the
    /// required policies and — when <paramref name="revocation"/> asks for it — none of them is revoked.
    /// </summary>
    public static void EnsureTrusted(IReadOnlyList<X509Certificate2> path, PkiPathTrust trust, DateTimeOffset now,
        RevocationPolicy? revocation = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(trust);
        if (path.Count == 0)
            throw new CryptographicException("The PKIPath token holds no certificate.");

        // The token must be a certification path, all of it: a token that is not one is refused, whatever it holds.
        for (var i = 1; i < path.Count; i++)
            EnsureIssued(issuer: path[i - 1], subject: path[i]);

        // The anchor nearest to the signer decides, and the checks of trust start at it. The token may also leave the
        // anchor out: then it is the issuer of the first certificate.
        X509Certificate2? anchor = null;
        var below = 0;                                    // index of the first certificate under the anchor
        for (var i = path.Count - 1; i >= 0 && anchor is null; i--)
        {
            if (trust.Anchors.Any(a => a.Thumbprint == path[i].Thumbprint))
            {
                anchor = path[i];
                below = i + 1;
            }
        }
        if (anchor is null)
        {
            anchor = trust.Anchors.FirstOrDefault(a => SameName(a.SubjectName, path[0].IssuerName) && Issued(a, path[0]))
                ?? throw new CryptographicException(
                    $"The signing certificate {path[^1].Subject} does not chain to a trust anchor of the agreement.");
            EnsureIssued(anchor, path[0]);
        }
        EnsurePathLength(anchor, path, below);

        if (CertificateValidity.Problem(anchor, now, "The trust anchor") is { } anchorProblem)
            throw new CryptographicException(anchorProblem);
        for (var i = below; i < path.Count; i++)
        {
            if (CertificateValidity.Problem(path[i], now, Role(path, i)) is { } problem)
                throw new CryptographicException(problem);
        }

        // The signer always, and every authority between it and the anchor (§4.3.3: "any policy and issuing
        // Certification Authorities involved in issuing the presented signing certificate").
        foreach (var policy in trust.Policies)
        {
            for (var i = Math.Min(below, path.Count - 1); i < path.Count; i++)
            {
                if (!AssertsPolicy(path[i], policy))
                    throw new CryptographicException(
                        $"The certificate {path[i].Subject} does not assert the certificate policy {policy} the agreement requires.");
            }
        }

        if (revocation is { Mode: not X509RevocationMode.NoCheck })
            EnsureNotRevoked(path, below, anchor, trust, now, revocation);
    }

    /// <summary>
    /// Throws when a certificate below the anchor is revoked, or when its status cannot be determined and soft-fail is
    /// not chosen. A certificate that names no CRL distribution point and no authority information access passes, as for
    /// a pinned certificate (<see cref="CertificateValidity"/>).
    /// <para>
    /// The CRLs are found and checked by <see cref="X509Chain"/>, which does so only on a chain that ends at a
    /// self-signed root (on Windows and on Linux alike: under an intermediate trust anchor it reports a partial chain
    /// and checks nothing). So the chain is completed above the anchor from what the token carries there, the
    /// agreement's anchors and, failing those, the local stores and the anchor's authority information access; the root
    /// found is made the chain's trust root for this check alone: the trust decision has been taken by then. The anchor
    /// itself is not judged (RFC 5280 §6.1: validation starts below it).
    /// </para>
    /// </summary>
    private static void EnsureNotRevoked(IReadOnlyList<X509Certificate2> path, int below, X509Certificate2 anchor,
        PkiPathTrust trust, DateTimeOffset now, RevocationPolicy revocation)
    {
        var judged = Enumerable.Range(below, path.Count - below).Where(i => CertificateValidity.NamesRevocationSources(path[i])).ToList();
        if (judged.Count == 0)
            return;

        var aboveAnchor = path.Take(Math.Max(below - 1, 0)).Concat(trust.Anchors).ToList();
        var upward = Order(anchor, aboveAnchor);          // the root first, the anchor last
        if (!IsSelfIssued(upward[0]))
            upward = Discover(upward, aboveAnchor);
        if (!IsSelfIssued(upward[0]))
        {
            Undetermined(path[judged[0]], Role(path, judged[0]),
                $"no self-signed root above the trust anchor {anchor.Subject} is known, and the status is checked on a chain to a root");
            return;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(upward[0]);
        chain.ChainPolicy.ExtraStore.AddRange(upward.Skip(1).Concat(path.Skip(below).Take(path.Count - below - 1)).ToArray());
        chain.ChainPolicy.RevocationMode = revocation.Mode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.DisableCertificateDownloads = true;   // the path is the token's; only CRLs and OCSP are fetched
        chain.Build(path[^1]);

        // The chain must be the path that was judged trusted, not another one the builder found.
        var expected = upward.Concat(path.Skip(below)).Reverse().Select(c => c.Thumbprint).ToList();
        var built = chain.ChainElements.Select(e => e.Certificate.Thumbprint).ToList();
        if (!built.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            throw new CryptographicException(
                $"The revocation status of {path[^1].Subject} was checked on another certification path than the one the message carries.");

        foreach (var i in judged)
        {
            var status = chain.ChainElements[path.Count - 1 - i].ChainElementStatus
                .Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
            if ((status & X509ChainStatusFlags.Revoked) != 0)
                throw new CryptographicException($"{Role(path, i)} {path[i].Subject} ({path[i].Thumbprint}) has been revoked.");
            if ((status & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0)
                Undetermined(path[i], Role(path, i), "CRL or OCSP responder unreachable");
        }

        void Undetermined(X509Certificate2 certificate, string role, string why)
        {
            if (!revocation.SoftFail)
                throw new CryptographicException(
                    $"{role} {certificate.Subject} ({certificate.Thumbprint}): its revocation status could not be determined ({why}).");
        }
    }

    /// <summary>
    /// <paramref name="upward"/> completed towards a root as for a pinned certificate (<see cref="CertificateValidity"/>):
    /// from the local certificate stores and the authority information access of its outermost certificate. Only for the
    /// revocation check; what is found is not trusted for anything else.
    /// </summary>
    private static IReadOnlyList<X509Certificate2> Discover(IReadOnlyList<X509Certificate2> upward, IReadOnlyList<X509Certificate2> known)
    {
        using var discovery = new X509Chain();
        discovery.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        discovery.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        discovery.ChainPolicy.ExtraStore.AddRange(known.ToArray());
        discovery.Build(upward[0]);
        var found = discovery.ChainElements.Skip(1).Select(e => e.Certificate).Reverse();   // the root first
        var completed = found.Concat(upward).ToList();
        return completed.Count > MaxLength ? upward : completed;
    }

    private static string Role(IReadOnlyList<X509Certificate2> path, int index) =>
        index == path.Count - 1 ? "The signing certificate" : "The certification authority";

    private static void EnsureIssued(X509Certificate2 issuer, X509Certificate2 subject)
    {
        if (!SameName(issuer.SubjectName, subject.IssuerName))
            throw new CryptographicException(
                $"The PKIPath token is not a path: {subject.Subject} is not issued by {issuer.Subject}.");
        if (!IsAuthority(issuer))
            throw new CryptographicException($"The certificate {issuer.Subject} issues {subject.Subject} but is not a certification authority.");
        // RFC 5280 §4.2.1.3: a key that may sign certificates says so when the certificate restricts its use at all.
        if (issuer.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage
            && (usage.KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0)
            throw new CryptographicException($"The key of {issuer.Subject} is not allowed to sign certificates (key usage keyCertSign).");
        if (!Issued(issuer, subject))
            throw new CryptographicException($"The signature of {issuer.Subject} on the certificate {subject.Subject} does not verify.");
    }

    /// <summary>
    /// RFC 5280 §4.2.1.9, §6.1.4 (l)-(m): an authority with a path length constraint has at most that many certification
    /// authorities below it, not counting self-issued ones, before the signing certificate.
    /// </summary>
    private static void EnsurePathLength(X509Certificate2 anchor, IReadOnlyList<X509Certificate2> path, int below)
    {
        var authorities = new List<X509Certificate2> { anchor };
        authorities.AddRange(path.Skip(below).Take(path.Count - below - 1));
        for (var k = 0; k < authorities.Count; k++)
        {
            if (authorities[k].Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { HasPathLengthConstraint: true } constraints)
                continue;
            var under = authorities.Skip(k + 1).Count(a => !IsSelfIssued(a));
            if (under > constraints.PathLengthConstraint)
                throw new CryptographicException(
                    $"The certification authority {authorities[k].Subject} allows {constraints.PathLengthConstraint} authorities below it; " +
                    $"the path has {under}.");
        }
    }

    private static bool IsSelfIssued(X509Certificate2 certificate) => SameName(certificate.SubjectName, certificate.IssuerName);

    private static bool SameName(X500DistinguishedName a, X500DistinguishedName b) => a.RawData.AsSpan().SequenceEqual(b.RawData);

    private static bool IsAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is { CertificateAuthority: true };

    /// <summary>Whether the key of <paramref name="issuer"/> signed <paramref name="subject"/>.</summary>
    private static bool Issued(X509Certificate2 issuer, X509Certificate2 subject)
    {
        try
        {
            var parser = new Org.BouncyCastle.X509.X509CertificateParser();
            parser.ReadCertificate(subject.RawData).Verify(parser.ReadCertificate(issuer.RawData).GetPublicKey());
            return true;
        }
        catch (Exception e) when (e is Org.BouncyCastle.Security.GeneralSecurityException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether the certificate policies extension (RFC 5280 §4.2.1.4) lists <paramref name="policy"/> or anyPolicy.</summary>
    private static bool AssertsPolicy(X509Certificate2 certificate, string policy)
    {
        var extension = certificate.Extensions.FirstOrDefault(e => e.Oid?.Value == CertificatePoliciesOid);
        if (extension is null)
            return false;
        try
        {
            var policies = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (policies.HasData)
            {
                var information = policies.ReadSequence();
                var identifier = information.ReadObjectIdentifier();
                if (identifier == policy || identifier == AnyPolicyOid)
                    return true;
            }
            return false;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static X509Certificate2 Load(byte[] der)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificate(der);
#else
        return new X509Certificate2(der);
#endif
    }
}
