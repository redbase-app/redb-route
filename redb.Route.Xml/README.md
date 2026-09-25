# redb.Route.Xml

Declarative XML routes for [redb.Route](https://www.nuget.org/packages/redb.Route): `.route.xml`
documents load into the existing fluent DSL — **one engine, no second semantics**. Every element
is a thin facade over exactly one DSL verb; whatever the XML can say, C# can say, and the two
spellings produce byte-identical definition trees (the test suite proves it for every shipped
example).

What you get on top of the engine:

- the **full EIP vocabulary** as XML — ~60 elements covering every string-expressible verb of
  the DSL, plus container-level handlers and the REST DSL;
- **load-time failure** for everything that can fail early: unknown elements (with a
  *did-you-mean* hint), malformed expressions, unregistered URI schemes, attribute typos —
  all collected in **one pass** with `file(line,column)` positions;
- a **generated XSD** for editor autocompletion (VS Code / Visual Studio / Rider, no plugin of
  ours), a **component catalog** for property panels, a **C# generator** and **Mermaid
  diagrams** from the same parse;
- **project scaffolding and packaging**: `redb-route-xml new` → an ordinary `.csproj` with
  routes, resources and layered configuration; `pack` → a signed-ready package layout with a
  manifest and a hard-error check gate.

## Quick start

```csharp
await using var context = new RouteContext()
    .AddXmlRoutesFromContent("""
        <routes xmlns="urn:redb:route:1.0">
          <route id="orders-in" description="Orders intake">
            <from uri="direct://orders"/>
            <setHeader name="priority" expr="${header.amount > 1000 ? 'high' : 'normal'}"/>
            <filter expr="header.amount > 0">
              <choice>
                <when expr="header.priority == 'high'">
                  <to uri="direct://orders-vip"/>
                </when>
                <otherwise>
                  <to uri="direct://orders-std"/>
                </otherwise>
              </choice>
            </filter>
          </route>
        </routes>
        """);
await context.Start();
```

Files and globs work the same way — `AddXmlRoutes("routes/*.route.xml")` loads every match in
deterministic order (relative paths resolve through the route resource resolver, never the
process working directory), and a pattern matching nothing is an error naming the searched
places. In a DI host, register inside `AddRedbRoute`:

```csharp
services.AddRedbRoute(r => r.AddXmlRoutes("routes/*.route.xml"));
```

A document with errors registers **nothing** — there is no half-loaded state.

## The format at a glance

The namespace is `urn:redb:route:1.0`; the major part changes only on a breaking format change,
and a document carrying a newer minor is refused with "update the redb.Route.Xml package" rather
than a confusing parse error. Every element lives in this namespace — a foreign-namespace
element never passes just because its local name matches.

Every element accepts `id=` and `description=`: they flow into step identity (message history
node ids and labels, `WeaveById` targets, generated-code comments).

**Leaf steps** — `to`, `toD`, `setHeader`, `setProperty`, `setBody`, `setHeaders`, `setProperties`, `transform`,
`removeHeader/Property/Body/Headers/Properties`, `log`, `delay`, `stop`, `throwException`,
`convertBody`, `wireTap`, `validate`, `sort`, `sample`, `streamCaching`, `validateJsonSchema`,
`validateXsd`, `xslt`, `marshal`, `unmarshal`, `controlBus`, `enrich`, `pollEnrich`,
`recipientList`, `dynamicRouter`, `routingSlip`, `claimCheck`, `beginTransaction`,
`commitTransaction`, `rollbackTransaction`, `rollbackAll`, `exceptionHandled`, `routePolicy`,
`saga`, `scatterGather`, `loadBalance`, `normalize`.

**Scopes** (children are steps) — `filter`, `split` (with `tokenizeLines` / `tokenizeXml` /
`tokenizeJsonArray` children), `multicast`, `aggregate`, `tryCatch` (`try`/`catch`/`finally`),
`loop` (count / expr / while), `throttle` (plain and keyed), `debounce`, `circuitBreaker`
(with `fallback`), `idempotentConsumer`, `resequence`, `transaction`, `traced`, `metered`,
`replayable`, `threads`, `ofType`, `onException`, `intercept`, `interceptFrom`,
`interceptSendToEndpoint`, `onCompletion`.

**Branching** — `choice` with `when` (expression or `predicate="#name"` registry reference)
and `otherwise`.

**Container level** (directly under `<routes>`; the handlers apply to every route of the context, not only of this file) —
`bean`, `onException`, `intercept`, `interceptFrom`, `interceptSendToEndpoint`,
`onCompletion`, and package-contributed top-level elements such as `rest`.

A few rules that keep documents unambiguous:

- `value=` is a constant, `expr=` is an expression — **both at once is a schema error**, and
  which one you used decides the meaning, never the content of the string;
- file-or-content elements (`xslt`, `validateXsd`, `validateJsonSchema`, `transformJson`,
  `payload`) take `file=`/locator **or** inline content (CDATA welcome) — not both;
- `<aggregate>` requires an explicit `strategy` — there is no silent default;
- a condition is written `header.kind == 'order'`, **not** `${header.kind} == 'order'`: with
  `${…}` the line is a template, and a template in a condition position renders to a non-empty
  string, which is true whatever the header holds;
- a `${…}` placeholder in a **consumer** URI (`<from>`) is refused at load: there is no
  message to resolve it against.

## Expressions

Positions decide meaning — the same rule the fluent DSL follows:

| Position | Elements | Becomes |
|---|---|---|
| condition | `filter`, `when`, `validate`, `loop while=` | a predicate |
| value | `setBody`, `setHeader`, `transform`, `toD`, keys and correlations | an evaluated object |
| plain string | `uri`, `id`, `description`, `name`, literal `value=` | a literal, never parsed |

The expression language is the engine's own — `${header.x}`, `count(property.items) > 2 ?
'many' : 'few'`, `${jpath('$.order.id')}`, `${xpath('/order/@id')}`, arithmetic, `uuid()`,
`datediff(...)`, `messageHistory(...)` (the exchange's own trail: `'compact'`, `'count'`,
`'slowestMs'`, ...), `stats(...)` (endpoint statistics, and the OpenTelemetry layer through
`'otel:<instrument>'`) — and a broken expression fails **when the document loads**, not on the
first message. Whitespace around operators is insignificant, so `expr="header.amount>1000"` avoids
XML escaping entirely (`>` needs no escape in attribute values; `<` does).

Type-shaped checks the language cannot express go through the registry:
`<when predicate="#isStringList">` resolves an `IPredicate` you registered from code.

## Endpoints: URI form and structured form

Short addresses stay URIs. Long or option-heavy ones can be written structurally — the loader
normalizes the element into **the same URI string** before any endpoint exists, so the whole
engine pipeline (endpoint cache, statistics, mock masks, secret redaction) sees one canon:

```xml
<to uri="kafka://orders?key=${header.tripId}"/>          <!-- same thing -->
<to>
  <kafka topic="orders" key="${header.tripId}" groupId="orders-svc"/>
</to>

<to>
  <sql dataSource="#main-db">
    <![CDATA[ INSERT INTO auth_log(login, at) VALUES (:#login, :#at) ]]>
    <param name="login" value="${header.login}"/>
    <param name="at" value="${dateformat(now(), 'o')}"/>
  </sql>
</to>
```

The rules are generic — **no connector has XML-specific code**:

- the element name is the scheme; every attribute becomes a query option verbatim;
- the path part is the universal `path=` attribute, the component's declared one-line synonym
  (`kafka` → `topic`, `file` → `directory`), or the element's text content for text-path
  components (`sql` — and `path=` on those is refused: the query belongs in CDATA);
- `<param name="…" value="…"/>` children become `param.name=…` family options; a child with
  content and no `name` becomes a long text option (`<onSuccess><![CDATA[…]]></onSuccess>`);
- connection factories are ordinary options carrying registry references:
  `connectionFactory="#mainKafka"`, `dataSource="#main-db"`.

## Container level: beans, handlers, REST

`<bean>` declares objects in the context registry — the factories your endpoints reference:

```xml
<bean name="main-db" type="redb.Route.Sql.Connection.SqlConnectionFactory, redb.Route.Sql">
  <constructorArg>
    <bean type="redb.Route.Sql.Connection.SqlConnectionOptions, redb.Route.Sql">
      <property key="ConnectionString" value="{{db.main.connection}}"/>
    </bean>
  </constructorArg>
</bean>
```

Values resolve `{{key}}` / `{{key:default}}` through the context's configuration chain
(`IConfiguration`, then context properties) **before** type conversion — `{{ldap.port:636}}`
binds to an `int`. Nested anonymous beans build option graphs inline. Objects with real
dependencies stay in code; XML references them by `#name`.

A property takes either `value=` or one nested anonymous `<bean>`, so a factory that holds an
OBJECT - a certificate, a credentials object, a serializer - is declarable too. When the type is
built by a static creator rather than a constructor, `factoryMethod=` names it and the
`<constructorArg>` values are its arguments:

```xml
<bean name="globex" type="redb.Route.As2.As2ConnectionFactory, redb.Route.As2">
  <property key="OurCertificate">
    <bean type="System.Security.Cryptography.X509Certificates.X509CertificateLoader, System.Security.Cryptography.X509Certificates"
          factoryMethod="LoadPkcs12FromFile">
      <constructorArg value="{{as2.certificates}}/hub.pfx"/>
      <constructorArg value="{{as2.password}}"/>
    </bean>
  </property>
  <property key="As2From" value="{{as2.id}}"/>
  <property key="Sign" value="true"/>
</bean>
```

Handlers declared at the container level apply to every route of the **context**, not only to the routes of their own file: the engine registers `onException`, `intercept*` and `onCompletion` of every builder globally, so one file of handlers covers the routes of all the other files and the C# routes of the same context. Declare each of them once:

```xml
<onException exceptions="System.Exception" handled="true"
             maximumRedeliveries="2" redeliveryDelay="00:00:01" exponentialBackOff="true">
  <log level="Error">[ERR] ${routeId}: ${exception.message}</log>
</onException>
```

### The retry policy of a handler

`<onException>` spells everything its fluent counterpart does:

```xml
<onException exceptions="System.Net.Http.HttpRequestException"
             handled="true" maximumRedeliveries="3" redeliveryDelay="00:00:02"
             exponentialBackOff="true" backOffMultiplier="2.0"
             useOriginalBody="true" logStackTrace="false" logExhausted="true"
             retryAttemptedLogLevel="Debug" retriesExhaustedLogLevel="Critical"
             onExceptionOccurred="#countFailure" onRedelivery="#stampAttempt"
             onPrepareFailure="#stampDead">
  <when expr="header.retryable == 'true'"/>
  <retryWhile expr="property.attempt &lt; 5"/>
  <to uri="direct://dead-letters"/>
</onException>
```

`<when>` decides whether this handler takes the failure at all, `<retryWhile>` whether another
attempt follows; both are conditions of the handler, not steps of it. The three references name
`IProcessor` beans from the registry and run at their own moments: `onExceptionOccurred` on every
occurrence, `onRedelivery` before each retry, `onPrepareFailure` once, before the handler takes
over for good. A name the registry does not hold is refused at load, with its position.

`handled="true"` ends the route after the handler; `continued="true"` resumes it at the step
**after** the one that failed, as Camel's `continued(true)` does. What the resumption does not
replay:

- the failing step itself — it is skipped, the rest goes on;
- a `.Transacted()` block whose step failed: the transaction rolled back, so the route picks up
  after the block, not inside it;
- the body of a `<tryCatch>` whose failure a `<catch>` already took.

A failure in a resumed step is handled again, but not retried: a redelivery restarts the route
from its first step, so `maximumRedeliveries` does not apply to what the resumption replays.

The REST DSL is a container-level element from the `redb.Route.Http` package:

```xml
<rest path="/api/orders" port="5099" bindingMode="json">
  <get path="/{id}" id="orders-get" to="direct://orders-get-handler"/>
  <post produces="application/json">
    <setHeader name="accepted" value="true"/>
    <to uri="kafka://orders"/>
  </post>
</rest>
```

A verb handles its request with `to=` **or** inline steps — one of the two. Path parameters
arrive as `header.id`, query as `header.query.*`; OpenAPI is served at `{path}/openapi.json`.

## The context document

Besides route files, a package can carry a `context.xml` — the context-level concerns:

```xml
<context xmlns="urn:redb:route:1.0">
  <components>
    <component type="redb.Route.Sql.SqlComponent, redb.Route.Sql"/>
  </components>
  <bean name="main-db" type="…"> … </bean>
  <onInit>
    <to uri="sql:CREATE TABLE IF NOT EXISTS demo_log (id BIGINT)?dataSource=#main-db"/>
    <to uri="bean:#seeder?method=EnsureDefaults"/>
  </onInit>
</context>
```

`<onInit>` is a pipeline of ordinary format steps, run **once** in the engine's fail-fast
bootstrap phase: a failed step keeps the context from accepting traffic, and the error names
the source file. Load it with `AddXmlContext("context.xml")` / `AddXmlContextFromContent(...)`
— before the route files, so `#references` and schemes resolve.

## Configuration and `enabled=`

Configuration is layered; the package itself carries **identity only** (`ContextName`,
`AutoStart`) — settings and secrets arrive from the host's merged configuration at deploy time.
Every `{{key}}` without a `{{key:default}}` is recorded by the packaging tool in the manifest's
`RequiredConfigKeys`, so a missing value fails fast at module init instead of surfacing as a
malformed URI at 3 a.m.

`enabled="{{features.orders:false}}"` on a `<route>` resolves from the same chain at load — a
route can be switched per environment without touching the package. `enabled="false"` skips
registration entirely (stronger than `autoStart="false"`, which registers but does not start).

## Errors: one pass, positions, no half-states

Everything wrong with a document is reported together:

```text
routes/orders.route.xml(4,6): unknown element <setHeaderr>. Did you mean <setHeader>?
routes/orders.route.xml(7,10): <setHeader> takes 'value' or 'expr', not both.
routes/orders.route.xml(12,8): [schema] The 'bogus' attribute is not allowed.
routes/orders.route.xml(15,6): scheme 'kafkaa' is not registered in this context.
```

The parser's own findings win on a shared line; the generated-XSD pass (on by default,
`ValidateAgainstSchema = false` to opt out) adds what the parser deliberately leaves to it —
above all, attribute typos. Fixing ten mistakes takes one run, not ten.

## Testing an XML route

An XML route is tested exactly like a C# route — with `redb.Route.TestKit`, no test host of
its own:

```csharp
await using var ctx = new RouteContext().AddXmlRoutesFromContent(xml);
ctx.AdviceAllRoutes(a => a.MockEndpoints("kafka://*", "sql:*"));
await ctx.Start();

var mock = ctx.Mock("kafka://orders").ExpectMessageCount(1).ExpectHeader("seen", "true");
await ctx.SendBody("direct://in", "payload");
await mock.AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
```

- **Swapping transports.** `MockEndpoints("kafka://*")` rewrites the definition tree between
  load and `Start()` — the same seam for both spellings of a route.
- **Weaving by id.** `AdviceRoute(id, a => a.WeaveById("step-id").Replace(...))` finds a step
  by its XML `id=` attribute.
- **Secrets stay out of reports.** The query string of a URI — where passwords live — is not
  part of the mock name and never appears in a failed assertion's report (nor, from there, in
  CI logs).
- **Why `mock:` and not a real broker.** A unit test asserts the ROUTE — branching, headers,
  bodies; a broker adds latency and flakiness to that claim. Keep one integration test per
  transport against the real thing; let every route test run on mocks.
- **Structural comparison.** `redb.Route.Diagnostics.RouteDescriber` renders a definition tree
  as stable, diffable text — equivalence tests compare XML against its fluent C# twin
  byte-for-byte, and golden files pin the parse of every shipped example.

## Schema and editor support

The XSD is **generated from the element registry** — the same list the parser reads, so they
cannot drift. `XmlRouteSchema.Generate(registry)` covers the vocabulary with enum value hints
(`level=`, `policy=`, `strategy=` complete from a list), required attributes, and rejection of
unknown elements; foreign-**namespace** attributes are tolerated by design so other tools can
annotate route files. Pass a `ComponentCatalog` to make structured endpoint children strict and
typed per connector. Point any XSD-aware editor at it — VS Code with the RedHat XML extension:

```jsonc
// .vscode/settings.json
{
  "xml.fileAssociations": [
    { "pattern": "**/*.route.xml", "systemId": "./schema/redb-route-1.0.xsd" }
  ]
}
```

The component catalog (`ComponentCatalog.Build`, `ToJson`) describes every connector for a
property panel — scheme, path synonym, options with types, defaults, enum values, `[Sensitive]`
marks — by reflection over the connector's own `Options` class. A new connector appears in the
structured form, the catalog and the schema with **zero XML-specific code**.

## Tooling: scaffold, pack, generate

The `redb-route-xml` dotnet tool (package `redb.Route.Xml.CodeGen` — a developer tool, not part
of the runtime):

| Command | What it does |
|---|---|
| `new Orders --context orders` | scaffolds a route project: an ordinary `.csproj`, `context.xml`, `routes/`, `resources/`, layered `config/`, the generated XSD wired into `.vscode` |
| `pack . --version 1.0.0 [--bin bin/Debug/net9.0]` | runs the checks, then builds the package layout (`manifest.json`, config, artifacts, resources) and a `.tpkg` zip |
| `check .` | the same gate for CI — findings only |
| `catalog <binDir> --out dir` | builds the component catalog + catalog-aware XSD from built connector assemblies |
| `csharp routes/x.route.xml --namespace My.Routes` | fluent C# (readable, or `--style machine` with `#line` mapping diagnostics back to the XML) |
| `mermaid routes/x.route.xml` | a flowchart from the same parse |
| `xsd --out dir` | the generated schema for the core set |

Packaging checks are **hard errors, not advice**: schema validation with positions, `]]>`
inside CDATA, files missing from `resources/` (`file=`, and the files package elements name:
`<payload template=>`, `<transformJson spec=>`), undeclared `#name` references;
with `--bin`, bean types are verified against the real assemblies — a renamed type, a
non-public type, a typo in a `<property>` or in `bean:…?method=` refuses the build. A literal
secret in a URI is a warning that names the fix (supply it through configuration). Add
`-p:PackRouteOnBuild=true` to a scaffolded project to pack on every `dotnet build`.

What the gate **warns** about, leaving the decision to the author: a step that never runs
(anything after `<stop/>`, `<rollbackAll/>` or `<throwException/>` in the same list), an
undeclared `#name` (module code may register it at startup), a literal secret in a URI, and a
condition that compares outside a placeholder — `expr="${header.kind} == 'order'"` renders to
text before it is read, and non-empty text is true whatever it says, so the branch always wins.
A lone `${header.enabled}` and a comparison written entirely inside the braces are both read
correctly and stay silent.

About the generated C#: it is what the XML says, pronounced in C# — not what a person would
have written. There are no lambdas in it, because there are none in XML. For migration that is
exactly right; the mechanical guarantee is stronger than style — the generated code produces a
definition tree **byte-identical** to loading the XML.

## Extending the format from your package

A package with DSL of its own ships one class per element — parse, schema shape and C# printing
together, so the loader, the XSD and the generator see the element together or not at all:

```csharp
public sealed class CacheXmlContribution : IXmlElementContribution
{
    public string Name => "cache";
    public XmlElementKind Kind => XmlElementKind.Scope;
    public ElementSpec Spec => …;          // attributes, enum values, children — feeds the XSD
    public IRouteDefinition Apply(…) => …; // calls the package's own fluent DSL, nothing else
    public void Print(…) => …;             // the generator's face; missing = loud error
}
```

Hosts register contributions via `XmlRouteLoaderOptions.Extensions`. A duplicate element name
is a hard error, never a silent override. Container-level elements (like `<rest>`) implement
`IXmlTopLevelContribution` and apply to the route builder. Shipping today:
`<cache>` (redb.Route.Cache), `<transformJson>` (redb.Route.JsonTransform), `<payload>`
(redb.Route.Templates), `<rest>` (redb.Route.Http).

## What XML deliberately does not do

Lambdas, custom processors' logic, complex seeding — code. XML references it (`bean:#name`,
`predicate="#name"`, `<saga><step processor="#name"/>`), never embeds it: there is no compiler
in the runtime and no second trust contour — a package's code is signed and verified before
anything runs, and the XML only chooses which already-trusted types to use.

The schema of the format is generated from the element registry: `redb-route-xml xsd <bin directory>`
writes it for exactly the packages in that directory, their contributed elements included, and the
VS Code extension validates against it. See [XML Route Tools](../README.md#xml-route-tools).
