using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>Two constructors the same two strings fit — the shape X509Certificate2 has.</summary>
public class ProbeCredential
{
    public ProbeCredential(string file, string password) => Chosen = $"string:{file}/{password}";

    public ProbeCredential(string file, object options) => Chosen = $"object:{file}/{options}";

    public string Chosen { get; }

    /// <summary>Two overloads of one arity: the value alone does not say which.</summary>
    public static ProbeCredential From(string file) => new(file, "via-string");

    public static ProbeCredential From(int slot) => new($"slot-{slot}", "via-int");
}

/// <summary>
/// <c>&lt;constructorArg type="…"&gt;</c> — Spring's <c>constructor-arg type</c>: when several
/// constructors (or factory-method overloads) accept the same values, the argument types name the
/// one to call, and each value is converted to its parameter's type. Without it the choice is
/// the engine's (one fitting constructor, one overload of that arity), and an ambiguous one is an
/// error — never a guess. X509Certificate2 from a PFX on net8 is the case that asked for it: its
/// (string, string) constructor is one of several two-argument ones.
/// </summary>
public sealed class XmlBeanTypedArgumentsTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "redb-xml-typed-" + Guid.NewGuid().ToString("N"));

    public XmlBeanTypedArgumentsTests() => Directory.CreateDirectory(_dir);

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string T<TType>() => $"{typeof(TType).FullName}, {typeof(TType).Assembly.GetName().Name}";

    private void Load(string beans)
        => _context.AddXmlContextFromContent($"""
            <context xmlns="urn:redb:route:1.0">
            {beans}
            </context>
            """);

    [Fact]
    public void Certificate_FromPfx_ThroughTheStringStringConstructor()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=redb-typed-args", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = Path.Combine(_dir, "node.pfx");
        File.WriteAllBytes(pfx, issued.Export(X509ContentType.Pkcs12, "secret"));
        _context.SetProperty("node.pfx", pfx);

        Load($"""
            <bean name="node-cert" type="{T<X509Certificate2>()}">
              <constructorArg type="System.String" value="{"{{"}node.pfx{"}}"}"/>
              <constructorArg type="System.String" value="secret"/>
            </bean>
            """);

        using var loaded = _context.GetFromRegistry<X509Certificate2>("node-cert")!;
        loaded.Subject.Should().Be("CN=redb-typed-args");
        loaded.HasPrivateKey.Should().BeTrue();
    }

#if NET9_0_OR_GREATER
    [Fact]
    public void FactoryMethod_WithOptionalTrailingParameters_IsCalledLikeCSharpCallsIt()
    {
        // The documented example: LoadPkcs12FromFile(path, password, flags = DefaultKeySet, limits = null).
        // Matching by the exact parameter count never found it; the optional rest takes its defaults,
        // and the ReadOnlySpan<char> overload is no candidate — no markup value can be one.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=redb-loader", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = Path.Combine(_dir, "hub.pfx");
        File.WriteAllBytes(pfx, issued.Export(X509ContentType.Pkcs12, "secret"));

        Load($"""
            <bean name="hub-cert" type="{typeof(X509CertificateLoader).FullName}, {typeof(X509CertificateLoader).Assembly.GetName().Name}" factoryMethod="LoadPkcs12FromFile">
              <constructorArg value="{pfx}"/>
              <constructorArg value="secret"/>
            </bean>
            """);

        using var loaded = _context.GetFromRegistry<X509Certificate2>("hub-cert")!;
        loaded.Subject.Should().Be("CN=redb-loader");
    }
#endif

    [Fact]
    public void TypedArguments_ChooseTheConstructor_AmongOnesTheValuesAllFit()
    {
        Load($"""
            <bean name="as-string" type="{T<ProbeCredential>()}">
              <constructorArg type="System.String" value="a.pfx"/>
              <constructorArg type="System.String" value="pw"/>
            </bean>
            <bean name="as-object" type="{T<ProbeCredential>()}">
              <constructorArg type="System.String" value="a.pfx"/>
              <constructorArg type="System.Object" value="opts"/>
            </bean>
            """);

        _context.GetFromRegistry<ProbeCredential>("as-string")!.Chosen.Should().Be("string:a.pfx/pw");
        _context.GetFromRegistry<ProbeCredential>("as-object")!.Chosen.Should().Be("object:a.pfx/opts");
    }

    [Fact]
    public void UntypedArguments_ThatFitSeveralConstructors_AreAnError_NotAGuess()
    {
        var act = () => Load($"""
            <bean name="ambiguous" type="{T<ProbeCredential>()}">
              <constructorArg value="a.pfx"/>
              <constructorArg value="pw"/>
            </bean>
            """);

        act.Should().Throw<XmlRouteException>();
    }

    [Fact]
    public void FactoryMethod_Overloads_OfOneArity_AreRefused_UntilTypesNameOne()
    {
        var ambiguous = () => Load($"""
            <bean name="from" type="{T<ProbeCredential>()}" factoryMethod="From">
              <constructorArg value="7"/>
            </bean>
            """);
        ambiguous.Should().Throw<XmlRouteException>()
            .WithMessage("*has 2 public static methods 'From' taking 1 argument(s)*name the parameter types with <constructorArg type=…>*");

        Load($"""
            <bean name="from" type="{T<ProbeCredential>()}" factoryMethod="From">
              <constructorArg type="System.Int32" value="7"/>
            </bean>
            """);
        _context.GetFromRegistry<ProbeCredential>("from")!.Chosen.Should().Be("string:slot-7/via-int",
            "the value is converted to the named parameter type");
    }

    [Fact]
    public void ASignatureNoConstructorHas_AndTypesOnSomeArgumentsOnly_AreErrors()
    {
        var missing = () => Load($"""
            <bean name="none" type="{T<ProbeCredential>()}">
              <constructorArg type="System.Int32" value="1"/>
              <constructorArg type="System.Int32" value="2"/>
            </bean>
            """);
        missing.Should().Throw<XmlRouteException>().WithMessage("*has no public constructor (System.Int32, System.Int32)*");

        var mixed = () => Load($"""
            <bean name="mixed" type="{T<ProbeCredential>()}">
              <constructorArg type="System.String" value="a"/>
              <constructorArg value="b"/>
            </bean>
            """);
        mixed.Should().Throw<XmlRouteException>().WithMessage("*name every parameter type, or none*");
    }

    [Fact]
    public void Generator_PrintsTheTypes_AndTheRuntimeHalfUsesThem()
    {
        var code = XmlCodeGenerator.Generate(XDocument.Parse($"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="c" type="{T<ProbeCredential>()}">
                <constructorArg type="System.String" value="a"/>
                <constructorArg type="System.Object" value="b"/>
              </bean>
            </routes>
            """), "TypedGenerated", "Tests.Generated");

        code.Should().Contain("argumentTypes: [\"System.String\", \"System.Object\"]");
        var built = (ProbeCredential)XmlBeans.Create(_context, T<ProbeCredential>(), ["a", "b"], [],
            argumentTypes: ["System.String", "System.Object"]);
        built.Chosen.Should().Be("object:a/b");
    }

    [Fact]
    public void Gate_WithTheBuiltAssemblies_HoldsTheNamedSignature()
    {
        var project = RouteProjectScaffold.Create(_dir, "Typed");
        File.WriteAllText(Path.Combine(project, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="none" type="{T<ProbeCredential>()}">
                <constructorArg type="System.Int32" value="1"/>
                <constructorArg type="System.Int32" value="2"/>
              </bean>
              <bean name="from" type="{T<ProbeCredential>()}" factoryMethod="From">
                <constructorArg value="7"/>
              </bean>
              <bean name="ok" type="{T<ProbeCredential>()}">
                <constructorArg type="System.String" value="a"/>
                <constructorArg type="System.String" value="b"/>
              </bean>
              <route id="r"><from uri="direct://in"/><removeBody/></route>
            </routes>
            """);

        var errors = RoutePackage.Check(project, "p", "1.0.0", Type.GetType).Errors.Select(e => e.Message).ToList();

        errors.Should().Contain(m => m.Contains("bean 'none'") && m.Contains("has no public constructor (System.Int32, System.Int32)"));
        errors.Should().Contain(m => m.Contains("bean 'from'") && m.Contains("has 2 public static methods 'From'"));
        errors.Should().NotContain(m => m.Contains("bean 'ok'"));
    }
}
