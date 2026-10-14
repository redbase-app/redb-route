using System.Globalization;
using System.Security.Cryptography.X509Certificates;

using redb.Route.Abstractions;            // IRouteContext
using redb.Route.As4;

namespace ConformanceModule;

/// <summary>
/// What the eDelivery support team hands over at registration, and where this module listens. The values come from
/// the route context (<see cref="FromContext"/>), so nothing of one test account is compiled into the module.
/// </summary>
public sealed class ConformanceSettings
{
    /// <summary><c>eb:PartyId/@type</c> of every party of the conformance tests.</summary>
    public const string UnregisteredPartyIdType = "urn:oasis:names:tc:ebcore:partyid-type:unregistered";

    /// <summary>Party id of the access point under test, as assigned by the support team.</summary>
    public string SutPartyId { get; init; } = "system_under_test";

    /// <summary>Party id of the test driver in the message exchanges under test.</summary>
    public string TestDriverPartyId { get; init; } = "test_driver";

    /// <summary>Our party id on the command channel (Submit, Deliver, Notify).</summary>
    public string ControllerPartyId { get; init; } = "sut_controller_domain";

    /// <summary>Party id of the test driver on the command channel.</summary>
    public string TestDriverControllerPartyId { get; init; } = "test_driver_sut_controller_domain";

    /// <summary>MSH address of the test driver for the messages under test.</summary>
    public required string TestDriverUrl { get; init; }

    /// <summary>MSH address of the test driver for Deliver and Notify commands.</summary>
    public required string TestDriverControllerUrl { get; init; }

    /// <summary>Port both receive endpoints listen on (one shared host).</summary>
    public int Port { get; init; } = 4090;

    /// <summary>Path of the access point under test.</summary>
    public string SutPath { get; init; } = "/as4/sut";

    /// <summary>Path that receives Submit commands.</summary>
    public string ControllerPath { get; init; } = "/as4/controller";

    /// <summary>Host part of the message ids we generate.</summary>
    public string ExternalHostName { get; init; } = "redb.route";

    /// <summary>The SUT key: signs our messages and receipts, decrypts what the test driver encrypts for us.</summary>
    public required X509Certificate2 SutKey { get; init; }

    /// <summary>The test driver's certificate: verifies its signatures, and payloads are encrypted for it.</summary>
    public required X509Certificate2 TestDriverCertificate { get; init; }

    /// <summary>Redeliveries of the ONEWAY_RETRY profile; the user guide asks for 5.</summary>
    public int RetryCount { get; init; } = 5;

    /// <summary>Delay between those redeliveries; the user guide asks for less than 3 minutes.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Where the ids of received messages are remembered: <c>memory</c>, lost with the process, or <c>redb</c>, the
    /// worker's redb, so that a resend is still recognised after a restart and by every node of a cluster.
    /// </summary>
    public string DuplicateStore { get; init; } = "memory";

    /// <summary>How long a received message id is remembered.</summary>
    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Milliseconds to wait for the test driver's response with the receipt.</summary>
    public int SendTimeout { get; init; } = 60000;

    /// <summary>
    /// The certification authorities above <see cref="SutKey"/>: sent with it as its certification path on the PKI path
    /// legs. Empty when the SUT certificate is its own authority.
    /// </summary>
    public IReadOnlyList<X509Certificate2> SutChain { get; init; } = [];

    /// <summary>
    /// The authorities the test driver's signing certificate must chain to on the PKI path legs. Empty: the test
    /// driver's certificate itself, as its own authority.
    /// </summary>
    public IReadOnlyList<X509Certificate2> TestDriverTrustAnchors { get; init; } = [];

    /// <summary>Certificate policy OID required on the leg <c>bstPkiPathSutC3PolicyFail</c>; that leg is left out when null.</summary>
    public string? PkiPathPolicy { get; init; }

    /// <summary>Service and action pairs added to <see cref="ConformanceLegs.Default"/>: <c>service|action;service|action</c>.</summary>
    public string? ExtraLegs { get; init; }

    /// <summary>
    /// Reads the settings from the route context, the way the Tsak worker provides them: the module's
    /// <c>ConformanceModule.config.json</c> gives the defaults, and what differs per test account arrives through the
    /// Override layer, environment variables on the worker such as
    /// <c>Tsak__Contexts__as4-conformance__Override__TestDriver__Url</c>. Every root key is a context property; a
    /// section is a dictionary.
    /// </summary>
    public static ConformanceSettings FromContext(IRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IDictionary<string, object?> Section(string name) =>
            context.GetProperty<IDictionary<string, object?>>(name) ?? new Dictionary<string, object?>();
        static string? Value(IDictionary<string, object?> section, string key) =>
            section.TryGetValue(key, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;
        string Need(IDictionary<string, object?> section, string sectionName, string key) => Value(section, key)
            ?? throw new InvalidOperationException(
                $"{sectionName}:{key} is not configured for context '{context.ContextId}': set it in ConformanceModule.config.json, " +
                $"or, for a value of one test account, in Tsak:Contexts:{context.ContextId}:Override (see demos/As4ConformanceDemo/README.md).");
        // Certificate files separated by ";".
        static IReadOnlyList<X509Certificate2> Certificates(string? files) =>
            (files ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(X509CertificateLoader.LoadCertificateFromFile).ToList();
        static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);

        var driver = Section("TestDriver");
        var sut = Section("Sut");
        var receive = Section("Receive");
        var retry = Section("Retry");
        var pkiPath = Section("PkiPath");
        var url = Need(driver, "TestDriver", "Url");

        return new ConformanceSettings
        {
            TestDriverUrl = url,
            TestDriverControllerUrl = Value(driver, "ControllerUrl") ?? url,
            TestDriverPartyId = Need(driver, "TestDriver", "PartyId"),
            TestDriverControllerPartyId = Need(driver, "TestDriver", "ControllerPartyId"),
            TestDriverCertificate = X509CertificateLoader.LoadCertificateFromFile(Need(driver, "TestDriver", "Certificate")),
            TestDriverTrustAnchors = Certificates(Value(driver, "TrustAnchors")),
            SutPartyId = Need(sut, "Sut", "PartyId"),
            ControllerPartyId = Need(sut, "Sut", "ControllerPartyId"),
            SutKey = X509CertificateLoader.LoadPkcs12FromFile(Need(sut, "Sut", "Pfx"), Value(sut, "PfxPassword")),
            SutChain = Certificates(Value(sut, "Chain")),
            ExternalHostName = Need(sut, "Sut", "HostName"),
            Port = Int(Need(receive, "Receive", "Port")),
            SutPath = Need(receive, "Receive", "SutPath"),
            ControllerPath = Need(receive, "Receive", "ControllerPath"),
            DuplicateStore = Need(receive, "Receive", "DuplicateStore"),
            DuplicateWindow = TimeSpan.FromDays(Int(Need(receive, "Receive", "DuplicateWindowDays"))),
            RetryCount = Int(Need(retry, "Retry", "Count")),
            RetryDelay = TimeSpan.FromSeconds(Int(Need(retry, "Retry", "DelaySeconds"))),
            SendTimeout = Int(Need(retry, "Retry", "SendTimeoutMs")),
            PkiPathPolicy = Value(pkiPath, "Policy"),
            ExtraLegs = context.GetProperty<string>("ExtraLegs") is { Length: > 0 } extra ? extra : null,
        };
    }
}

/// <summary>One service and action pair of the conformance tests, with how our signature references our certificate.</summary>
public sealed record ConformanceLeg(string Service, string Action, As4KeyReference KeyReference = As4KeyReference.BinarySecurityToken,
    bool PkiPath = false, bool WithPolicy = false)
{
    /// <summary>Whether a missing receipt is redelivered: the ONEWAY_RETRY profile of the user guide.</summary>
    public bool Retry => Action == "retry";
}

/// <summary>
/// The legs between the test driver and the access point under test: user guide v2.90, annex A (PMode parameters 11 and
/// 12) for the common profile and the Four Corner enhancement. The agreements of the node are built from this list, one
/// per pair, since a received message is matched on sender, service and action.
/// </summary>
public static class ConformanceLegs
{
    public const string OneWay = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/oneWay";
    public const string TwoWay = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/twoWay";

    /// <summary>ebMS 3.0 test service (Core §5.2.2): its messages get a receipt and reach no business application.</summary>
    public const string TestService = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/service";
    public const string TestAction = "http://docs.oasis-open.org/ebxml-msg/ebms/v3.0/ns/core/200704/test";

    /// <summary>Service of the command channel; its actions are Submit, Deliver and Notify.</summary>
    public const string CommandService = "http://www.edelivery.eu/as4/conformancetest";

    public static IReadOnlyList<ConformanceLeg> Default { get; } =
    [
        new(OneWay, "default"),
        new(OneWay, "retry"),
        new(OneWay, "fourCorner"),
        new(OneWay, "duplicateDetection"),
        new(OneWay, "emptyBodyNotReferencedInSignature"),
        new(OneWay, "bstDefault"),
        new(OneWay, "ski", As4KeyReference.KeyIdentifier),
        new(OneWay, "issuerSerial", As4KeyReference.IssuerSerial),
        // PKI path: we sign with our certification path, and the test driver's signer must present one that leads to
        // a trust anchor (AS4_CP_TA47 to TA51). The last leg also requires a certificate policy.
        new(OneWay, "bstPkiPath", As4KeyReference.PkiPath, PkiPath: true),
        new(OneWay, "bstPkiPathSutC3Fail", As4KeyReference.PkiPath, PkiPath: true),
        new(OneWay, "bstPkiPathSutC3PolicyFail", As4KeyReference.PkiPath, PkiPath: true, WithPolicy: true),
        new(TwoWay, "default"),
        new(TestService, TestAction),
    ];

    /// <summary><see cref="Default"/> plus the pairs of <c>service|action;service|action</c>.</summary>
    public static IReadOnlyList<ConformanceLeg> With(string? extra)
    {
        var legs = Default.ToList();
        foreach (var pair in (extra ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('|');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                throw new InvalidOperationException($"ExtraLegs: '{pair}' is not service|action.");
            if (!legs.Any(l => l.Service == parts[0] && l.Action == parts[1]))
                legs.Add(new ConformanceLeg(parts[0], parts[1]));
        }
        return legs;
    }
}
