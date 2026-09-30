using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Xml;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>A props type the markup references by CLR name.</summary>
public sealed class RedbXmlOrderProps
{
    /// <summary>Sample payload property.</summary>
    public string? Value { get; set; }

    /// <summary>Sample numeric property (ordering comparisons).</summary>
    public int Rank { get; set; }
}

/// <summary>
/// The redb storage elements (Route-XML Шаг 1, the Р21 contribution of redb.Route.Core):
/// routes with <c>&lt;redbGet&gt;</c>/<c>&lt;redbSave&gt;</c>/<c>&lt;redbDelete&gt;</c>/
/// <c>&lt;beginRedbTransaction&gt;</c> against a substituted <see cref="IRedbService"/>, the
/// <c>&lt;redb&gt;</c> context block, the schema pass and the generator face.
/// </summary>
public class RedbXmlContributionsTests : IAsyncDisposable
{
    private static readonly XmlRouteLoaderOptions Options = new()
    {
        Extensions =
        [
            new RedbGetXmlContribution(), new RedbSaveXmlContribution(),
            new RedbDeleteXmlContribution(),
            new RedbQueryXmlContribution(), new RedbContextXmlContribution(),
        ],
    };

    private readonly RouteContext _context = new();
    private readonly IRedbService _redb = Substitute.For<IRedbService>();

    public RedbXmlContributionsTests() => _context.AddService(typeof(IRedbService), _redb);

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static IExchange Msg(object? body = null, params (string Key, object? Value)[] headers)
    {
        var exchange = new Exchange();
        exchange.In.Body = body;
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        return exchange;
    }

    private async Task<IProducer> StartAndProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    // ── routes ───────────────────────────────────────────────────────

    [Fact]
    public async Task RedbGet_WithoutType_IsTheRawJsonLoad()
    {
        _redb.LoadJsonAsync(11L, 2).Returns("{\"id\":11}");
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-json">
                <from uri="direct://redb-json-in"/>
                <redbGet id="${header.oid}" depth="2"/>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://redb-json-in");

        var exchange = Msg(headers: ("oid", 11L));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("{\"id\":11}");
    }

    [Fact]
    public async Task RedbGet_WithType_LoadsTyped_IntoTheTarget()
    {
        var stored = new RedbObject<RedbXmlOrderProps>();
        _redb.LoadAsync<RedbXmlOrderProps>(7L, 10).Returns(stored);
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-typed">
                <from uri="direct://redb-typed-in"/>
                <redbGet id="${header.oid}" type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                         target="header:order"/>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://redb-typed-in");

        var exchange = Msg(body: "kept", headers: ("oid", 7L));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("kept");
        exchange.In.Headers["order"].Should().BeSameAs(stored);
    }

    [Fact]
    public async Task RedbSave_And_Delete_RoundTheBodyThroughTheService()
    {
        _redb.DeleteAsync(5L).Returns(true);
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-save">
                <from uri="direct://redb-save-in"/>
                <redbSave type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"/>
                <redbDelete id="${header.gone}"/>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://redb-save-in");

        var exchange = Msg(body: "{\"properties\":{\"Value\":\"x\"}}", headers: ("gone", 5L));
        await producer.Process(exchange);

        await _redb.Received(1).SaveAsync(Arg.Is<redb.Core.Models.Contracts.IRedbObject>(
            o => o is RedbObject<RedbXmlOrderProps>));
        await _redb.Received(1).DeleteAsync(5L);
        exchange.In.Headers["redbDeleted"].Should().Be(true);
    }

    [Fact]
    public void RedbSave_ByUniqueWithoutType_IsALoadTimeError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-bad">
                <from uri="direct://redb-bad-in"/>
                <redbSave byUnique="true"/>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>()
            .Which.Errors.Should().ContainSingle(e => e.Contains("byUnique") && e.Contains("type="));
    }

    [Fact]
    public void BeginRedbTransaction_IsNotAnElement_TheTransactionScopeIsTheOnePrimitive()
    {
        // The verb behind it is obsolete (TRANSACTIONS_GUIDE: one primitive, .Transacted()) and a
        // no-op under an ambient scope. Markup has no warning channel, so the element is gone —
        // the steps go inside <transaction> instead. Red before the removal: the element loaded.
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-tx">
                <from uri="direct://redb-tx-in"/>
                <beginRedbTransaction/>
                <redbSave type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"/>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>()
            .Which.Errors.Should().Contain(e => e.Contains("beginRedbTransaction"));
    }

    [Fact]
    public void RedbGet_AttributeTypo_IsCaughtByTheSchemaPass()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-typo">
                <from uri="direct://redb-typo-in"/>
                <redbGet id="${header.oid}" dept="2"/>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>()
            .Which.Errors.Should().Contain(e => e.Contains("[schema]") && e.Contains("dept"));
    }

    [Fact]
    public async Task RedbQuery_TranslatesTheWhereString_ServerSide()
    {
        var query = Substitute.For<redb.Core.Query.IOrderedRedbQueryable<RedbXmlOrderProps>>();
        System.Linq.Expressions.Expression<Func<RedbXmlOrderProps, bool>>? captured = null;
        query.Where(Arg.Do<System.Linq.Expressions.Expression<Func<RedbXmlOrderProps, bool>>>(e => captured = e))
            .Returns(query);
        query.Take(Arg.Any<int>()).Returns(query);
        query.ToListAsync().Returns([new RedbObject<RedbXmlOrderProps>()]);
        _redb.Query<RedbXmlOrderProps>().Returns(query);

        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-query">
                <from uri="direct://redb-query-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           where="Value == header.wanted" take="5"/>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://redb-query-in");

        var exchange = Msg(headers: ("wanted", "hit"));
        await producer.Process(exchange);

        query.Received(1).Take(5);
        captured.Should().NotBeNull();
        var predicate = captured!.Compile();
        predicate(new RedbXmlOrderProps { Value = "hit" }).Should().BeTrue();
        predicate(new RedbXmlOrderProps { Value = "miss" }).Should().BeFalse();
        exchange.In.Body.Should().BeOfType<List<RedbObject<RedbXmlOrderProps>>>();
    }

    [Fact]
    public void RedbQuery_UntranslatableWhere_IsAPositionedLoadError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-query-bad">
                <from uri="direct://redb-query-bad-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           where="Valeu == 'x'"/>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>()
            .Which.Errors.Should().ContainSingle(e =>
                e.Contains("(4,") && e.Contains("Valeu") && e.Contains("Value"),
            "the refusal carries the element position and the property candidates");
    }

    [Fact]
    public async Task RedbQuery_ConditionAsText_CdataFreesTheAngleBrackets()
    {
        var query = Substitute.For<redb.Core.Query.IOrderedRedbQueryable<RedbXmlOrderProps>>();
        System.Linq.Expressions.Expression<Func<RedbXmlOrderProps, bool>>? captured = null;
        query.Where(Arg.Do<System.Linq.Expressions.Expression<Func<RedbXmlOrderProps, bool>>>(e => captured = e))
            .Returns(query);
        query.Take(Arg.Any<int>()).Returns(query);
        query.ToListAsync().Returns([]);
        _redb.Query<RedbXmlOrderProps>().Returns(query);

        // The whole point of the text form: a raw `<` lives inside CDATA, no &lt; anywhere.
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-query-text">
                <from uri="direct://redb-query-text-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml" take="5"><![CDATA[
                  Rank < header.max AND Value != null
                ]]></redbQuery>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://redb-query-text-in");

        await producer.Process(Msg(headers: ("max", 10)));

        captured.Should().NotBeNull();
        var predicate = captured!.Compile();
        predicate(new RedbXmlOrderProps { Rank = 9, Value = "x" }).Should().BeTrue();
        predicate(new RedbXmlOrderProps { Rank = 10, Value = "x" }).Should().BeFalse();
        predicate(new RedbXmlOrderProps { Rank = 9 }).Should().BeFalse("Value is null");
    }

    [Fact]
    public void RedbQuery_BothWhereFormsAtOnce_IsAnError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-query-both">
                <from uri="direct://redb-query-both-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           where="Value == 'a'">Value == 'b'</redbQuery>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>()
            .Which.Errors.Should().ContainSingle(e => e.Contains("not both"));
    }

    // ── the <redb> context block ─────────────────────────────────────

    [Fact]
    public async Task ContextRedbBlock_SyncsSchemes_AndRegistersTheRepository()
    {
        _context.SetServiceProvider(new ServiceCollection().BuildServiceProvider());
        var document = XDocument.Parse("""
            <context xmlns="urn:redb:route:1.0">
              <redb>
                <syncScheme type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"/>
                <idempotentRepository name="orders-idem" ttl="7.00:00:00"/>
              </redb>
            </context>
            """, System.Xml.Linq.LoadOptions.SetLineInfo);

        new XmlContextLoader(_context, Options).Load(document, "context.xml");
        await _context.Start();

        await _redb.Received(1).SyncSchemeAsync<RedbXmlOrderProps>();
        _context.GetFromRegistry<IIdempotentRepository>("orders-idem")
            .Should().NotBeNull("the repository must be reachable by <idempotentConsumer repository=…>");
    }

    [Fact]
    public void ContextRedbBlock_InRoutePosition_IsAnError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-misplaced">
                <from uri="direct://redb-misplaced-in"/>
                <redb/>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>()
            .Which.Errors.Should().Contain(e => e.Contains("context-level"));
    }

    [Fact]
    public void UnknownContextSection_ErrorNamesTheContributedBlocks()
    {
        var document = XDocument.Parse("""
            <context xmlns="urn:redb:route:1.0">
              <redbb/>
            </context>
            """, System.Xml.Linq.LoadOptions.SetLineInfo);

        var act = () => new XmlContextLoader(_context, Options).Load(document, "context.xml");

        act.Should().Throw<XmlRouteException>()
            .Which.Errors.Should().Contain(e => e.Contains("<redb>"));
    }

    // ── the generator face ───────────────────────────────────────────

    [Fact]
    public void Generator_PrintsTheVerbs_AndTheirUsings()
    {
        var document = XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-gen">
                <from uri="direct://redb-gen-in"/>
                <redbGet id="${header.oid}" depth="2"/>
                <redbSave type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml" byUnique="true"/>
                <redbDelete id="${header.gone}" storage="archive"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           where="Value == 'x'" orderBy="Value" descending="true" take="5"/>
              </route>
            </routes>
            """);

        var code = XmlCodeGenerator.Generate(document, "RedbGenerated", "Tests.Generated", options: Options);

        code.Should().Contain("using redb.Route.RedbCore.Extensions;");
        code.Should().NotContain("BeginRedbTransaction", "the obsolete verb has no markup any more");
        code.Should().Contain("RedbGetJson(\"${header.oid}\", depth: 2)");
        code.Should().Contain("byUnique: true");
        code.Should().Contain("RedbDelete(\"${header.gone}\", storage: \"archive\")");
        code.Should().Contain("RedbQuery(").And.Contain("where: \"Value == 'x'\"")
            .And.Contain("orderBy: \"Value\"").And.Contain("descending: true").And.Contain("take: 5");
    }

    // ── whereRedb / orderByRedb: the base fields of the stored object ─

    [Fact]
    public async Task RedbQuery_WhereRedb_FiltersOnBaseFields_AndCombinesWithWhere()
    {
        var query = Substitute.For<redb.Core.Query.IOrderedRedbQueryable<RedbXmlOrderProps>>();
        System.Linq.Expressions.Expression<Func<redb.Core.Models.Contracts.IRedbObject, bool>>? capturedBase = null;
        System.Linq.Expressions.Expression<Func<RedbXmlOrderProps, bool>>? capturedProps = null;
        query.WhereRedb(Arg.Do<System.Linq.Expressions.Expression<Func<redb.Core.Models.Contracts.IRedbObject, bool>>>(e => capturedBase = e))
            .Returns(query);
        query.Where(Arg.Do<System.Linq.Expressions.Expression<Func<RedbXmlOrderProps, bool>>>(e => capturedProps = e))
            .Returns(query);
        query.OrderByRedb(Arg.Any<System.Linq.Expressions.Expression<Func<redb.Core.Models.Contracts.IRedbObject, long>>>())
            .Returns(query);
        query.ToListAsync().Returns([]);
        _redb.Query<RedbXmlOrderProps>().Returns(query);

        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-query-base">
                <from uri="direct://redb-query-base-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           whereRedb="ValueGuid == header.key AND ParentId == null"
                           where="Rank > 0" orderByRedb="Id"/>
              </route>
            </routes>
            """, options: Options);
        var producer = await StartAndProducer("direct://redb-query-base-in");
        var key = Guid.NewGuid();

        await producer.Process(Msg(headers: ("key", key.ToString())));

        capturedProps.Should().NotBeNull("where= still filters the props");
        capturedBase.Should().NotBeNull();
        var predicate = capturedBase!.Compile();
        predicate(new RedbObject<RedbXmlOrderProps> { ValueGuid = key }).Should().BeTrue();
        predicate(new RedbObject<RedbXmlOrderProps> { ValueGuid = key, ParentId = 1 }).Should().BeFalse();
        query.Received(1).OrderByRedb(Arg.Any<System.Linq.Expressions.Expression<Func<redb.Core.Models.Contracts.IRedbObject, long>>>());
    }

    [Fact]
    public void RedbQuery_WhereRedbOnAPropsName_IsAPositionedLoadError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-query-base-bad">
                <from uri="direct://redb-query-base-bad-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           whereRedb="Rank == 1"/>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>()
            .Which.Errors.Should().ContainSingle(e =>
                e.Contains("(4,") && e.Contains("'Rank' is neither a base field") && e.Contains("ValueGuid"));
    }

    [Fact]
    public void Generator_PrintsWhereRedbAndOrderByRedb()
    {
        var document = XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-gen-base">
                <from uri="direct://redb-gen-base-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           whereRedb="ValueString == header.code" orderByRedb="DateCreate" descending="true"/>
              </route>
            </routes>
            """);

        var code = XmlCodeGenerator.Generate(document, "RedbBaseGenerated", "Tests.Generated", options: Options);

        code.Should().Contain("whereRedb: \"ValueString == header.code\"").And.Contain("orderByRedb: \"DateCreate\"");
    }

    [Fact]
    public void Schema_KnowsWhereRedbAndOrderByRedb()
    {
        var document = XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-schema-base">
                <from uri="direct://in"/>
                <redbQuery type="X" whereRedb="Id == 1" orderByRedb="Id"/>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo);

        XmlRouteSchema.Validate(document, ElementRegistry.CreateDefault(Options.Extensions)).Should().BeEmpty();
    }

    [Fact]
    public async Task RedbQuery_OutputTypeFirst_LandsOneObject_AndPrints()
    {
        var query = Substitute.For<redb.Core.Query.IOrderedRedbQueryable<RedbXmlOrderProps>>();
        var found = new RedbObject<RedbXmlOrderProps>();
        query.WhereRedb(Arg.Any<System.Linq.Expressions.Expression<Func<redb.Core.Models.Contracts.IRedbObject, bool>>>())
            .Returns(query);
        query.FirstOrDefaultAsync().Returns(found);
        _redb.Query<RedbXmlOrderProps>().Returns(query);
        const string xml = """
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-query-first">
                <from uri="direct://redb-query-first-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           whereRedb="ValueGuid == header.key" outputType="First" target="property:existing"/>
              </route>
            </routes>
            """;

        _context.AddXmlRoutesFromContent(xml, options: Options);
        var producer = await StartAndProducer("direct://redb-query-first-in");
        var exchange = Msg(headers: ("key", Guid.NewGuid().ToString()));
        await producer.Process(exchange);

        exchange.Properties["existing"].Should().BeSameAs(found);
        XmlCodeGenerator.Generate(XDocument.Parse(xml), "FirstGenerated", "Tests.Generated", options: Options)
            .Should().Contain("outputType: RedbQueryOutput.First");
    }

    [Fact]
    public void RedbQuery_UnknownOutputType_IsASchemaError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="redb-query-output-bad">
                <from uri="direct://redb-query-output-bad-in"/>
                <redbQuery type="redb.Route.Tests.Xml.RedbXmlOrderProps, redb.Route.Tests.Xml"
                           whereRedb="Id > 0" outputType="Single"/>
              </route>
            </routes>
            """, options: Options);

        act.Should().Throw<XmlRouteException>().Which.Errors.Should().Contain(e => e.Contains("Single"));
    }
}
