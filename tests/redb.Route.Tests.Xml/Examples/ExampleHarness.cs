using redb.Route.Abstractions;
using redb.Route.Components.Bean;
using redb.Route.Core;

namespace redb.Route.Tests.Xml.Examples;

// ── Doubles for the Ф0.3 examples (Ф3 §1: thin sugar in the TEST project, never shipped) ──
// The examples reference demo types by name (`Acme.*`, connector factories). The doubles carry
// the same shapes — methods the bean: URIs call, properties the <bean> sections bind — and a
// mapping type resolver serves them under the example's exact type names.

/// <summary>Stamps the demo exchanges: batch keys and trace ids (eip/main-pipeline examples).</summary>
public class DemoStampsDouble
{
    private int _tick;
    public void SetBatchIdAndBody(IExchange exchange)
    {
        var tick = Interlocked.Increment(ref _tick);
        exchange.In.Headers["batchId"] = $"batch-{tick % 3}";
        exchange.In.Body = $"evt-{tick}";
    }

    public void NewTraceId(IExchange exchange)
        => exchange.In.Headers["traceId"] = Guid.NewGuid().ToString("N");
}

/// <summary>Builds the demo JSON response (main-pipeline example).</summary>
public class ResponseBuilderDouble
{
    public string Build(IExchange exchange)
        => $$"""{"traceId":"{{exchange.In.Headers["traceId"]}}","mode":"{{exchange.In.Headers["mode"]}}"}""";
}

/// <summary>The scope diagnostics probe (scope-diag example).</summary>
public class ScopeDiagProbeDouble
{
    private int _probes;
    public void Reset(IExchange exchange)
    {
        _probes = 0;
        exchange.In.Body = Enumerable.Range(1, 50).Select(i => (object?)$"item-{i}").ToList();
    }

    public void ProbeItem(IExchange exchange) => Interlocked.Increment(ref _probes);
    public void Summarize(IExchange exchange) => exchange.In.Body = $"probed:{_probes}";
}

/// <summary>Type test: the body is a collection of strings (deep-dsl-showcase example).</summary>
public class IsStringListDouble : IPredicate
{
    public bool Matches(IExchange exchange) => exchange.In.Body is IEnumerable<string>;
}

/// <summary>Type test: the body is a non-empty string (deep-dsl-showcase example).</summary>
public class IsNonEmptyStringDouble : IPredicate
{
    public bool Matches(IExchange exchange) => exchange.In.Body is string { Length: > 0 };
}

/// <summary>Shape of the LDAP connection factory the factories example configures.</summary>
public class LdapConnectionFactoryDouble
{
    public string? Server { get; set; }
    public int Port { get; set; }
    public bool Ssl { get; set; }
    public string? BindDn { get; set; }
    public string? BindPassword { get; set; }
    public bool FollowReferrals { get; set; }
}

/// <summary>Shape of the mail connection factory the factories example configures.</summary>
public class MailConnectionFactoryDouble
{
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Security { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>Options object built as a nested anonymous bean in the factories example.</summary>
public class SqlConnectionOptionsDouble
{
    public string? ConnectionString { get; set; }
}

/// <summary>Constructor-injected factory, as sap-s4 in tsum (the factories example).</summary>
public class SqlConnectionFactoryDouble(SqlConnectionOptionsDouble options)
{
    public SqlConnectionOptionsDouble Options { get; } = options;
}

/// <summary>Registration-only component for the schemes the examples address.</summary>
public sealed class SchemeStubComponent(string scheme) : ComponentBase
{
    public override string Scheme { get; } = scheme;
    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException($"'{Scheme}' is a registration-only stub in the example tests");
}

/// <summary>Serves the example documents' exact type names with local doubles.</summary>
public sealed class MappingTypeResolver : IBeanTypeResolver
{
    private static readonly Dictionary<string, Type> Map = new(StringComparer.Ordinal)
    {
        ["Acme.Demo.DemoStamps, Acme.Demo"] = typeof(DemoStampsDouble),
        ["Acme.Demo.ResponseBuilder, Acme.Demo"] = typeof(ResponseBuilderDouble),
        ["Acme.Demo.Predicates.IsStringList, Acme.Demo"] = typeof(IsStringListDouble),
        ["Acme.Demo.Predicates.IsNonEmptyString, Acme.Demo"] = typeof(IsNonEmptyStringDouble),
        ["Acme.Diagnostics.ScopeDiagProbe, Acme.Diagnostics"] = typeof(ScopeDiagProbeDouble),
        ["redb.Route.Ldap.LdapConnectionFactory, redb.Route.Ldap"] = typeof(LdapConnectionFactoryDouble),
        ["redb.Route.Mail.MailConnectionFactory, redb.Route.Mail"] = typeof(MailConnectionFactoryDouble),
        ["redb.Route.Sql.Connection.SqlConnectionFactory, redb.Route.Sql"] = typeof(SqlConnectionFactoryDouble),
        ["redb.Route.Sql.Connection.SqlConnectionOptions, redb.Route.Sql"] = typeof(SqlConnectionOptionsDouble),
    };

    public Type? Resolve(string typeName)
        => Map.TryGetValue(typeName, out var mapped) ? mapped : DefaultBeanTypeResolver.Instance.Resolve(typeName);
}

/// <summary>
/// The shared setup for loading the shipped examples: the repo examples directory, a context
/// with stub schemes, the mapping resolver and the examples directory as the resource root.
/// </summary>
internal static class ExampleHarness
{
    internal static string Dir { get; } = Locate();

    internal static RouteContext NewContext()
    {
        var context = new RouteContext();
        foreach (var scheme in new[] { "http", "ldap", "smtp", "sql" })
            context.AddComponent(new SchemeStubComponent(scheme));
        context.AddService(typeof(IBeanTypeResolver), new MappingTypeResolver());
        context.AddService(typeof(IRouteResourceResolver), new RouteResourceResolver { ResourceRoot = Dir });
        // The factories example binds beans from deployment configuration; the keys without a
        // {{key:default}} must be present before the load — exactly the deployment contract.
        context.SetProperty("ldap.server", "ldap.example.test");
        context.SetProperty("ldap.service.dn", "cn=svc,dc=example,dc=test");
        context.SetProperty("ldap.service.password", "test-not-a-secret");
        context.SetProperty("mail.host", "smtp.example.test");
        context.SetProperty("mail.user", "mailer");
        context.SetProperty("mail.password", "test-not-a-secret");
        context.SetProperty("db.main.connection", "Host=db.example.test;Database=demo");
        return context;
    }

    /// <summary>
    /// Every definition tree a document produced: the builder-level handlers (container
    /// onException / intercepts / onCompletion — they live on the builder, not among the
    /// routes) first, then the routes. Golden files and equivalence must see ALL of it, or a
    /// parse drift in the container handlers goes invisible.
    /// </summary>
    internal static IReadOnlyList<IProcessorDefinition> Definitions(RouteContext context)
    {
        var definitions = new List<IProcessorDefinition>();
        foreach (var builder in context.RouteBuilders)
        {
            if (!builder.IsBuilt)
                builder.InternalBuild(context);
            definitions.AddRange(builder.ExceptionDefinitions);
            definitions.AddRange(builder.Intercepts);
            definitions.AddRange(builder.OnCompletions);
            definitions.AddRange(builder.Definitions);
        }
        return definitions;
    }

    private static string Locate()
    {
        var probed = new List<string>();
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "docs", "Route-XML", "examples");
            probed.Add(candidate);
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            "The examples directory was not found above the test output. Probed: " + string.Join("; ", probed));
    }
}
