using System.Security.Cryptography;
using System.Xml;
using redb.Route.As4.Security;

namespace redb.Route.Tests.As4;

/// <summary>
/// Ф7 (review R5): <c>wsu:Timestamp</c> is checked as WSS4J checks it, the library Domibus and Holodeck verify with —
/// <c>Created</c> is required (BSP R3203), may not lie in the future beyond the clock tolerance nor be older than the
/// time to live (WSS4J timeStampTTL, 300 s); <c>Expires</c> is optional and, when present, must not have passed.
/// An <c>Expires</c> far in the future therefore buys nothing: the age is bounded by <c>Created</c>.
/// </summary>
public class As4TimestampTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static XmlElement Timestamp(DateTimeOffset? created, DateTimeOffset? expires)
    {
        var doc = new XmlDocument();
        var timestamp = doc.CreateElement("wsu", "Timestamp", WsSecurityNames.Wsu);
        if (created is { } c)
            timestamp.AppendChild(doc.CreateElement("wsu", "Created", WsSecurityNames.Wsu))!.InnerText = c.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        if (expires is { } e)
            timestamp.AppendChild(doc.CreateElement("wsu", "Expires", WsSecurityNames.Wsu))!.InnerText = e.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        return timestamp;
    }

    private static Action Check(XmlElement timestamp) => () => As4Signature.CheckTimestamp(timestamp, Tolerance, TimeToLive, Now);

    [Fact]
    public void Fresh_WithOrWithoutExpires_Passes()
    {
        Check(Timestamp(Now.AddSeconds(-30), Now.AddMinutes(5))).Should().NotThrow();
        Check(Timestamp(Now.AddSeconds(-30), null)).Should().NotThrow();
    }

    [Fact]
    public void WithoutCreated_IsRefused() =>
        Check(Timestamp(null, Now.AddMinutes(5))).Should().Throw<CryptographicException>().WithMessage("*Created*");

    [Fact]
    public void CreatedTooLongAgo_IsRefused_EvenWithExpiresAYearAhead() =>
        Check(Timestamp(Now.AddMinutes(-10), Now.AddYears(1))).Should().Throw<CryptographicException>().WithMessage("*too long ago*");

    [Fact]
    public void CreatedInTheFuture_BeyondTheTolerance_IsRefused() =>
        Check(Timestamp(Now.AddMinutes(10), null)).Should().Throw<CryptographicException>().WithMessage("*future*");

    [Fact]
    public void ExpiresPassed_BeyondTheTolerance_IsRefused() =>
        Check(Timestamp(Now.AddSeconds(-30), Now.AddMinutes(-10))).Should().Throw<CryptographicException>().WithMessage("*expired*");

    [Fact]
    public void NotADate_IsRefused_AsAFailedSignature()
    {
        var timestamp = Timestamp(Now, null);
        timestamp.FirstChild!.InnerText = "yesterday";
        Check(timestamp).Should().Throw<CryptographicException>();
    }
}
