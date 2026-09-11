using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>A body type for the unmarshal/ofType tests.</summary>
public class CatalogOrder
{
    public string? Id { get; set; }
    public int Amount { get; set; }
}

/// <summary>
/// Route-XML Ф2, catalog batch: the rest of the Ф0 §4 element set — leaves, scopes, branch
/// children and the schema errors the format promises. Behavioral spot-checks plus one
/// anti-drift document that exercises every element at once.
/// </summary>
public class CatalogContributionsTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IProducer> StartAndProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    private void Load(string xml, string? sourceName = null)
        => _context.AddXmlRoutesFromContent(xml, sourceName ?? "<inline>");

    // ── behavior ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetHeaders_MixesConstantsAndExpressions()
    {
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-setheaders">
                <from uri="direct://cat-sh-in"/>
                <setHeaders>
                  <header name="fixed" value="A"/>
                  <header name="derived" expr="${body}-tail"/>
                </setHeaders>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-sh-in");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.In.Headers["fixed"].Should().Be("A");
        exchange.In.Headers["derived"].Should().Be("x-tail");
    }

    [Fact]
    public async Task TryCatch_CatchHandles_FinallyAlwaysRuns()
    {
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-trycatch">
                <from uri="direct://cat-tc-in"/>
                <tryCatch>
                  <try>
                    <throwException type="System.InvalidOperationException" message="boom"/>
                  </try>
                  <catch exceptions="System.InvalidOperationException">
                    <setHeader name="caught" value="yes"/>
                  </catch>
                  <finally>
                    <setHeader name="final" value="yes"/>
                  </finally>
                </tryCatch>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-tc-in");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        // Engine semantics: a matched catch marks the exception handled; the record stays.
        exchange.ExceptionHandled.Should().BeTrue();
        exchange.In.Headers["caught"].Should().Be("yes");
        exchange.In.Headers["final"].Should().Be("yes");
    }

    [Fact]
    public async Task Loop_Count_RepeatsTheBody()
    {
        var hits = 0;
        _context.AddRoutes(r => r.From("direct://cat-loop-out").Process(_ => hits++));
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-loop">
                <from uri="direct://cat-loop-in"/>
                <loop count="3">
                  <to uri="direct://cat-loop-out"/>
                </loop>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-loop-in");

        await producer.Process(new Exchange(new Message("x")));

        hits.Should().Be(3);
    }

    [Fact]
    public async Task Aggregate_CompletionSize_WithNamedStrategy_Merges()
    {
        object? merged = null;
        _context.AddRoutes(r => r.From("direct://cat-agg-out").Process(e => merged = e.In.Body));
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-aggregate">
                <from uri="direct://cat-agg-in"/>
                <aggregate correlation="header.group" strategy="concat:+" completionSize="2">
                  <to uri="direct://cat-agg-out"/>
                </aggregate>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-agg-in");

        await producer.Process(Msg("a", ("group", "g")));
        await producer.Process(Msg("b", ("group", "g")));

        merged.Should().Be("a+b");
    }

    [Fact]
    public async Task Multicast_DeliversToEveryBranchStep()
    {
        // Multicast runs its branches in parallel by default — the sink must be thread-safe.
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        _context.AddRoutes(r =>
        {
            r.From("direct://cat-mc-1").Process(_ => seen.Add("one"));
            r.From("direct://cat-mc-2").Process(_ => seen.Add("two"));
        });
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-multicast">
                <from uri="direct://cat-mc-in"/>
                <multicast>
                  <to uri="direct://cat-mc-1"/>
                  <to uri="direct://cat-mc-2"/>
                </multicast>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-mc-in");

        await producer.Process(new Exchange(new Message("x")));

        seen.Should().BeEquivalentTo("one", "two");
    }

    [Fact]
    public async Task Split_TokenizeLines_SplitsTheBody()
    {
        var parts = new List<object?>();
        _context.AddRoutes(r => r.From("direct://cat-split-out").Process(e => parts.Add(e.In.Body)));
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-split">
                <from uri="direct://cat-split-in"/>
                <split>
                  <tokenizeLines separator=";" skipEmpty="true"/>
                  <to uri="direct://cat-split-out"/>
                </split>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-split-in");

        await producer.Process(new Exchange(new Message("a;b;;c")));

        parts.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task ClaimCheck_SetAndGet_RestoresTheBody()
    {
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-claim">
                <from uri="direct://cat-claim-in"/>
                <claimCheck operation="Set" key="${header.ticket}"/>
                <setBody value="stub"/>
                <claimCheck operation="GetAndRemove" key="${header.ticket}"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-claim-in");

        var exchange = Msg("payload", ("ticket", "t-1"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("payload");
    }

    [Fact]
    public async Task MarshalUnmarshal_JsonRoundtrip_ByRegisteredFormat()
    {
        Load($$"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-json">
                <from uri="direct://cat-json-in"/>
                <marshal format="application/json"/>
                <unmarshal format="application/json" target="{{typeof(CatalogOrder).FullName}}, {{typeof(CatalogOrder).Assembly.GetName().Name}}"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-json-in");

        var exchange = new Exchange(new Message(new CatalogOrder { Id = "o-1", Amount = 7 }));
        await producer.Process(exchange);

        var order = exchange.In.Body.Should().BeOfType<CatalogOrder>().Subject;
        order.Id.Should().Be("o-1");
        order.Amount.Should().Be(7);
    }

    [Fact]
    public async Task OnException_RouteLevel_HandlesTheFailure()
    {
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-onexc">
                <from uri="direct://cat-onexc-in"/>
                <onException exceptions="System.InvalidOperationException" handled="true">
                  <setHeader name="rescued" value="yes"/>
                </onException>
                <throwException type="System.InvalidOperationException" message="boom"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-onexc-in");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        exchange.Exception.Should().BeNull();
        exchange.In.Headers["rescued"].Should().Be("yes");
    }

    [Fact]
    public async Task Enrich_NamedStrategy_PutsTheResourceIntoAHeader()
    {
        _context.AddRoutes(r => r.From("direct://cat-enrich-src").SetBody("resource-data"));
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-enrich">
                <from uri="direct://cat-enrich-in"/>
                <enrich uri="direct://cat-enrich-src" strategy="intoHeader:extra"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-enrich-in");

        var exchange = new Exchange(new Message("original"));
        await producer.Process(exchange);

        exchange.In.Body.Should().Be("original");
        exchange.In.Headers["extra"].Should().Be("resource-data");
    }

    [Fact]
    public async Task RecipientList_SendsToEveryListedEndpoint()
    {
        var seen = new List<string>();
        _context.AddRoutes(r =>
        {
            r.From("direct://cat-rl-a").Process(_ => seen.Add("a"));
            r.From("direct://cat-rl-b").Process(_ => seen.Add("b"));
        });
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-rl">
                <from uri="direct://cat-rl-in"/>
                <recipientList expr="${header.targets}" delimiter=";"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-rl-in");

        await producer.Process(Msg("x", ("targets", "direct://cat-rl-a;direct://cat-rl-b")));

        seen.Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task OfType_IsATypedSection_ConvertingNotGuarding()
    {
        // Ф1.3: OfType converts the body to the target type; an incompatible body is a
        // conversion failure on the exchange, never a silent skip.
        var hits = 0;
        _context.AddRoutes(r => r.From("direct://cat-oftype-out").Process(_ => hits++));
        Load($$"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-oftype">
                <from uri="direct://cat-oftype-in"/>
                <ofType type="{{typeof(CatalogOrder).FullName}}, {{typeof(CatalogOrder).Assembly.GetName().Name}}">
                  <to uri="direct://cat-oftype-out"/>
                </ofType>
                <setHeader name="after" value="yes"/>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-oftype-in");

        var typed = new Exchange(new Message(new CatalogOrder()));
        await producer.Process(typed);
        var incompatible = new Exchange(new Message("just a string"));
        var act = () => producer.Process(incompatible);
        await act.Should().ThrowAsync<InvalidOperationException>("an incompatible body is a conversion failure");

        hits.Should().Be(1);
        typed.In.Headers["after"].Should().Be("yes", "siblings after </ofType> stay on the parent route");
    }

    [Fact]
    public async Task Normalize_ByConditionAndOtherwise_TransformsTheBody()
    {
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-normalize">
                <from uri="direct://cat-norm-in"/>
                <normalize>
                  <when expr="header.kind == 'wrapped'" transform="unwrapped:${body}"/>
                  <otherwise transform="passthrough:${body}"/>
                </normalize>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-norm-in");

        var wrapped = Msg("w", ("kind", "wrapped"));
        await producer.Process(wrapped);
        var plain = Msg("p", ("kind", "plain"));
        await producer.Process(plain);

        wrapped.In.Body.Should().Be("unwrapped:w");
        plain.In.Body.Should().Be("passthrough:p");
    }

    [Fact]
    public async Task LoadBalance_RoundRobin_AlternatesEndpoints()
    {
        var seen = new List<string>();
        _context.AddRoutes(r =>
        {
            r.From("direct://cat-lb-1").Process(_ => seen.Add("one"));
            r.From("direct://cat-lb-2").Process(_ => seen.Add("two"));
        });
        Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-lb">
                <from uri="direct://cat-lb-in"/>
                <loadBalance strategy="roundRobin">
                  <endpoint uri="direct://cat-lb-1"/>
                  <endpoint uri="direct://cat-lb-2"/>
                </loadBalance>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-lb-in");

        await producer.Process(new Exchange(new Message("1")));
        await producer.Process(new Exchange(new Message("2")));

        seen.Should().BeEquivalentTo("one", "two");
    }

    [Fact]
    public async Task Saga_RegistryProcessorSteps_CompensateOnFailure()
    {
        var log = new List<string>();
        _context.AddToRegistry("step1", new ProbeProcessor("one", log));
        _context.AddToRegistry("undo1", new ProbeProcessor("undo-one", log));
        _context.AddToRegistry("boom", new ProbeProcessor("boom", log, fail: true));
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-saga">
                <from uri="direct://cat-saga-in"/>
                <saga>
                  <step processor="#step1" compensate="#undo1"/>
                  <step processor="#boom"/>
                </saga>
              </route>
            </routes>
            """);
        var producer = await StartAndProducer("direct://cat-saga-in");

        var act = () => producer.Process(new Exchange(new Message("x")));
        await act.Should().ThrowAsync<Exception>("the failing step surfaces after compensation");

        log.Should().Equal("one", "boom", "undo-one");
    }

    private sealed class ProbeProcessor(string mark, List<string> log, bool fail = false) : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            log.Add(mark);
            return fail ? Task.FromException(new InvalidOperationException(mark)) : Task.CompletedTask;
        }
    }

    // ── schema errors ────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_WithoutStrategy_IsASchemaError()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-agg">
                <from uri="direct://bad-agg-in"/>
                <aggregate correlation="header.g" completionSize="2">
                  <removeBody/>
                </aggregate>
              </route>
            </routes>
            """, "bad-agg.xml");

        act.Should().Throw<XmlRouteException>().WithMessage("*bad-agg.xml(4,*strategy*");
    }

    [Fact]
    public void UnknownAggregationStrategy_ReportsThePosition()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-strategy">
                <from uri="direct://bad-strategy-in"/>
                <aggregate correlation="header.g" strategy="nosuchstrategy" completionSize="2">
                  <removeBody/>
                </aggregate>
              </route>
            </routes>
            """, "bad-strategy.xml");

        act.Should().Throw<XmlRouteException>().WithMessage("*bad-strategy.xml(4,*nosuchstrategy*");
    }

    [Fact]
    public void Loop_WithTwoSources_IsASchemaError()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-loop">
                <from uri="direct://bad-loop-in"/>
                <loop count="2" while="header.go == true">
                  <removeBody/>
                </loop>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*exactly one of count, expr or while*");
    }

    [Fact]
    public void Xslt_FileAndContentTogether_IsASchemaError()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-xslt">
                <from uri="direct://bad-xslt-in"/>
                <xslt file="transform.xslt"><![CDATA[<xsl:stylesheet/>]]></xslt>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*either the file attribute or inline*not both*");
    }

    [Fact]
    public void WeightedLoadBalance_MissingWeight_IsASchemaError()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-weighted">
                <from uri="direct://bad-weighted-in"/>
                <loadBalance strategy="weighted">
                  <endpoint uri="direct://bad-w-1" weight="2"/>
                  <endpoint uri="direct://bad-w-2"/>
                </loadBalance>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*weight on every <endpoint>*");
    }

    [Fact]
    public void BadTransactionPolicy_ReportsTheKnownNames()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-policy">
                <from uri="direct://bad-policy-in"/>
                <transaction policy="nested">
                  <removeBody/>
                </transaction>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*not a transaction policy (default, requiresNew, suppress, mandatory)*");
    }

    [Fact]
    public void FormatOptionsOnMarshal_RequireTheTypeForm()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-marshal">
                <from uri="direct://bad-marshal-in"/>
                <marshal format="application/json" indented="true"/>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*need the type= form*");
    }

    [Fact]
    public void RichLog_TextAndChildrenTogether_IsASchemaError()
    {
        var act = () => Load("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-log">
                <from uri="direct://bad-log-in"/>
                <log level="Information">some text<header name="h"/></log>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*either message text or child elements*");
    }

    // ── anti-drift: the whole catalog in one document ────────────────────────

    [Fact]
    public async Task TheWholeCatalog_LoadsAndStarts_InOneDocument()
    {
        _context.AddToRegistry("idempotent:cat-repo", new Processors.InMemoryIdempotentRepository());
        Load($$"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="cat-all-leaves">
                <from uri="direct://cat-all-1"/>
                <setHeader name="a" value="1"/>
                <setProperty name="p" value="1"/>
                <setBody expr="${body}"/>
                <setHeaders><header name="b" value="2"/></setHeaders>
                <transform expr="${body}"/>
                <sort expr="property.items" by="body" descending="true"/>
                <sample messageFrequency="1"/>
                <streamCaching/>
                <validate expr="body != null"/>
                <validateJsonSchema throwOnFailure="false"><![CDATA[{"type":"object"}]]></validateJsonSchema>
                <marshal format="application/json"/>
                <unmarshal format="application/json" target="{{typeof(CatalogOrder).FullName}}, {{typeof(CatalogOrder).Assembly.GetName().Name}}"/>
                <convertBody type="System.String"/>
                <controlBus action="Status" routeId="cat-all-leaves" async="true"/>
                <enrich uri="direct://cat-all-2" strategy="useLatest"/>
                <pollEnrich uri="direct://cat-all-2" timeout="00:00:00.100"/>
                <recipientList expr="direct://cat-all-2"/>
                <dynamicRouter expr="null"/>
                <routingSlip expr="direct://cat-all-2"/>
                <claimCheck operation="Set" key="k"/>
                <beginTransaction policy="requiresNew"/>
                <commitTransaction/>
                <rollbackTransaction/>
                <rollbackAll/>
                <exceptionHandled/>
                <wireTap uri="direct://cat-all-2"/>
                <removeHeader name="a"/>
                <removeProperty name="p"/>
                <removeHeaders pattern="X-*" except="X-Keep"/>
                <removeProperties pattern="tmp*"/>
                <removeBody/>
                <delay duration="00:00:00.001"/>
                <log level="Debug">plain</log>
                <log level="Debug" showRouteId="true"><message>rich</message><header name="b"/><property name="p"/></log>
                <stop/>
              </route>
              <route id="cat-all-scopes">
                <from uri="direct://cat-all-3"/>
                <onException exceptions="System.InvalidOperationException" handled="true" maximumRedeliveries="1" redeliveryDelay="00:00:00.001" exponentialBackOff="true" backOffMultiplier="1.5">
                  <removeBody/>
                </onException>
                <intercept><when expr="body != null"/><removeHeader name="x"/></intercept>
                <interceptFrom uri="direct://*"><removeHeader name="y"/></interceptFrom>
                <interceptSendToEndpoint uri="direct://cat-all-2" skipSendToOriginalEndpoint="true"><removeHeader name="z"/></interceptSendToEndpoint>
                <onCompletion onCompleteOnly="true" modeBeforeConsumer="true"><when expr="body != null"/><removeHeader name="w"/></onCompletion>
                <filter expr="body != null">
                  <multicast parallel="true" maxParallelism="2" stopOnException="true"><removeBody/></multicast>
                </filter>
                <aggregate correlation="header.g" strategy="groupedBody" completionTimeout="00:00:00.050"><removeBody/></aggregate>
                <tryCatch>
                  <try><removeBody/></try>
                  <catch exceptions="System.Exception"><exceptionHandled/></catch>
                  <finally><removeHeader name="f"/></finally>
                </tryCatch>
                <loop expr="1"><removeBody/></loop>
                <loop while="header.go == true" copy="true"><removeBody/></loop>
                <throttle maxPerPeriod="10" period="00:00:01" rejectOnOverflow="true"><removeBody/></throttle>
                <throttle maxPerPeriod="header.tier == 'gold' ? 100 : 10" key="header.customer" period="00:00:01"><removeBody/></throttle>
                <debounce key="header.device" quietPeriod="00:00:00.010"><removeBody/></debounce>
                <circuitBreaker failureThreshold="3" resetTimeout="00:00:01" halfOpenMaxCalls="1">
                  <removeBody/>
                  <fallback><setBody value="fb"/></fallback>
                </circuitBreaker>
                <idempotentConsumer key="${header.id}" repository="cat-repo" skipDuplicate="true"><removeBody/></idempotentConsumer>
                <resequence key="header.seq" batchSize="2" timeout="00:00:00.050"><removeBody/></resequence>
                <transaction policy="default"><removeBody/></transaction>
                <traced name="cat-span"><removeBody/></traced>
                <metered name="cat-meter"><removeBody/></metered>
                <replayable name="cat-replay" exposed="false"><removeBody/></replayable>
                <threads poolSize="2"><removeBody/></threads>
                <ofType type="System.String"><removeBody/></ofType>
                <split expr="body" parallel="false"><removeBody/></split>
                <split><tokenizeJsonArray/><removeBody/></split>
                <split><tokenizeXml element="item"/><removeBody/></split>
                <scatterGather timeout="00:00:00.100" parallel="true" maxDop="2" stopOnException="false" strategy="groupedBody">
                  <recipient uri="direct://cat-all-2"/>
                </scatterGather>
                <loadBalance strategy="weighted">
                  <endpoint uri="direct://cat-all-2" weight="2"/>
                  <endpoint uri="direct://cat-all-4" weight="1"/>
                </loadBalance>
                <normalize>
                  <when expr="header.kind == 'a'" transform="${body}"/>
                  <whenContentType type="application/json" transform="${body}"/>
                  <otherwise transform="${body}"/>
                </normalize>
                <choice>
                  <when expr="body != null"><stop/></when>
                  <otherwise><removeBody/></otherwise>
                </choice>
              </route>
              <route id="cat-all-sinks">
                <from uri="direct://cat-all-2"/>
                <toD uri="direct://cat-all-4"/>
              </route>
              <route id="cat-all-4-sink">
                <from uri="direct://cat-all-4"/>
                <removeBody/>
              </route>
            </routes>
            """);
        await _context.Start();

        _context.Routes.Select(r => r.RouteId).Should()
            .Contain(["cat-all-leaves", "cat-all-scopes", "cat-all-sinks", "cat-all-4-sink"]);
    }

    private static IExchange Msg(object? body, params (string Name, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message(body));
        foreach (var (name, value) in headers) exchange.In.Headers[name] = value;
        return exchange;
    }
}
