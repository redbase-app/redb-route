# Changelog

All notable changes to redb.Route will be documented in this file.
This changelog covers the **NuGet-published packages**:

| Package | Description |
|---------|-------------|
| `redb.Route` | Core engine: DSL, processors, expressions, telemetry |
| `redb.Route.Amqp` | AMQP 1.0 transport |
| `redb.Route.As2` | AS2 (RFC 4130) B2B/EDI transport — signed/encrypted S/MIME over HTTP with MDN receipts |
| `redb.Route.AzureServiceBus` | Azure Service Bus transport |
| `redb.Route.Cache` | Caching as an EIP — a `.Cache(key, ttl)` scope and the `cache:` component |
| `redb.Route.Controllers` | Transport-agnostic controller dispatch |
| `redb.Route.Core` | Bridge to redb.Core props storage |
| `redb.Route.DataFormats.Avro` | Apache Avro data format (Chr.Avro) — binary marshal/unmarshal |
| `redb.Route.DataFormats.Csv` | CSV data format (CsvHelper) — POCO collections, dictionaries, raw rows |
| `redb.Route.DataFormats.Protobuf` | Protocol Buffers data format (Google.Protobuf), optional Confluent Schema Registry |
| `redb.Route.DataFormats.Yaml` | YAML data format (YamlDotNet) — POCOs and dynamic documents |
| `redb.Route.Elasticsearch` | Elasticsearch 8.x transport |
| `redb.Route.Exec` | Local process execution transport (`exec:` scheme) |
| `redb.Route.File` | File system transport |
| `redb.Route.Firebase` | Firebase (Firestore, Cloud Storage, FCM) transport |
| `redb.Route.Ftp` | FTP/FTPS transport |
| `redb.Route.GenericFile` | Base library for file-based transports |
| `redb.Route.Grpc` | gRPC transport |
| `redb.Route.Http` | HTTP/HTTPS transport |
| `redb.Route.Http.Hosting` | Shared Kestrel HTTP hosting (`SharedHttpServerManager`) reused by HTTP-based transports |
| `redb.Route.IbmMq` | IBM MQ transport |
| `redb.Route.JsonTransform` | Declarative JSON-to-JSON transformation (JSONata) |
| `redb.Route.Kafka` | Apache Kafka transport |
| `redb.Route.Ldap` | LDAP / Active Directory transport |
| `redb.Route.Llm` | LLM transport — universal OpenAI-compatible provider + native AnthropicProvider |
| `redb.Route.Llm.Abstractions` | LLM tool-capability contracts (`ILlmToolDescriptor`, `LlmToolCapability`, `.AsLlmTool()` DSL) |
| `redb.Route.Llm.Mcp` | MCP-client connector (`mcp:` scheme) — spawns external Model Context Protocol servers, auto-discovers tools via `tools/list` |
| `redb.Route.Llm.Tools` | Utility LLM tools — HttpFetch / JsonPath / XPath / MathEval / RegexExtract / Tavily web search |
| `redb.Route.Mail` | Email transport (SMTP, IMAP, POP3) |
| `redb.Route.MqttNet` | MQTT 5.0 transport |
| `redb.Route.Quartz` | Quartz.NET scheduling transport |
| `redb.Route.RabbitMQ` | RabbitMQ transport |
| `redb.Route.Redis` | Redis transport |
| `redb.Route.S3` | AWS S3 / MinIO transport |
| `redb.Route.Sftp` | SFTP transport |
| `redb.Route.SignalR` | SignalR transport |
| `redb.Route.Sql` | SQL database transport |
| `redb.Route.Soap` | SOAP 1.1/1.2 transport — WS-Security, MTOM attachments, WSDL-first services |
| `redb.Route.Sqs` | Amazon SQS + SNS transport |
| `redb.Route.Tcp` | Raw TCP transport |
| `redb.Route.Telegram` | Telegram Bot transport |
| `redb.Route.Templates` | Payload templates (Scriban) — the PayloadFactory analog |
| `redb.Route.TestKit` | Test kit — AdviceWith, mock expectations, NotifyBuilder |
| `redb.Route.Validation.Adapters` | FluentValidation + DataAnnotations adapters |
| `redb.Route.WebSocket` | WebSocket transport |
| `redb.Route.XPath2` | XPath 2.0 expressions (regex, sequences, dates, `for`/`some`/`every`) on XPath2.Net |
| `redb.Route.Xml` | Declarative XML routes — `.route.xml` files load into the same fluent DSL |
| `redb.Route.Xml.CodeGen` | `redb-route-xml` .NET tool — fluent C#, Mermaid diagrams and the XSD from `.route.xml`; not part of the runtime |

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Note on version history:** redb.Route has been running in production since version 1.0.0.
> Versions 1.0.0 – 1.0.3 were not published to NuGet (internal deployments only).
> The first public NuGet release is **1.0.4**.

## [Unreleased]

Pending changes live in `changelog/unreleased/` until the release; `pwsh scripts/changelog.ps1 -Preview` shows them.

## [4.7.0] — 2026-11-06

### Breaking

- **A mail whose exchange fails is left as it is.** `imap://` and `pop3://` apply `postProcess` only when the exchange
  succeeds, as Apache Camel does; an unhandled failure or `.RollbackAll()` leaves the mail, and it is fetched again.
  `peek` (default `true`) now works: `peek=false` marks each IMAP mail read at fetch, so a failed one is not fetched again. Upgrade: [UPGRADING.md](UPGRADING.md#a-mail-whose-exchange-fails-is-left-as-it-is).
- **A mail option the other side reads is refused.** `smtp://` refuses the consumer options (`postProcess`, `folder`,
  `delay`, ...) and `imap://` / `pop3://` the producer ones (`subject`, `to`, `from`, ...) when the endpoint is created,
  naming them; they used to bind and do nothing. The editor schema offers each option only on its side. Upgrade: [UPGRADING.md](UPGRADING.md#a-mail-option-the-other-side-reads-is-refused).

### Added

- **`bean:name.Method(args)` in the expression language.** A registry bean's method can be called in any condition,
  value or template: `Filter("bean:loyalty.IsGold(header.customer)")`, `expr="${bean:pricing.Gross(header.qty, 2.5)}"`.
  The call is synchronous, as in Apache Camel's Simple: a method returning a task is refused and is called as a step.
  `check` / `pack` verify each call, and the new `EXPRESSIONS.md` guide describes the language.
- **A bean call reads as a step.** `<bean ref="#pricing" method="Gross"/>` (or `beanType="Type, Assembly"`) calls
  user code directly in a route or a scope, and `.Bean("#pricing", "Gross")` does the same in C#. Both are the
  `bean:` endpoint the URI form uses; `<to uri="bean:..."/>`, `<to><bean path="..."/></to>` and `.Bean<T>(...)` work
  as before.
- **`=~` and `range` / `not range` compare as in Apache Camel's Simple.** `header.s =~ 'hello'` ignores case for two
  strings; `header.n range '1..10'` tests a number within both ends, inclusive, with the literal checked at load.
- **`check` and `pack` warn about text after a complete expression.** The engine ignores it, so `header.b == 3 garbage`
  compares only `header.b == 3`; the warning names the token and its position. Component spellings such as
  `${file:name}` and `${env:X}` in URI options are not warned.
- **A literal `${` in a template is written `\${`.** It opens no placeholder and renders as `${`, so a template can
  carry text such as a shell variable (`echo \${HOME}`) next to its own placeholders.
- **`Method(reference, spec)`: a registry bean's method as an expression or a condition, Camel's `method()`.**
  `.Filter(Method("#rules", "IsGold(${header.name})"))`, `When(...)`, `SetBody(...)` take it; in a condition a
  method returning `Task<bool>` is awaited. `Validate(IExpression condition, ...)` is added beside
  `Validate(IPredicate, ...)`, so `Validate(Method(...))` and `Validate(Expr("..."))` read a condition as `Filter` does.
- **`{{key}}` reads the OS environment when the context has no `IConfiguration`.** A plain console host can write
  `{{TELEGRAM_TOKEN}}` in a URI; with an `IConfiguration` the host's own sources decide, as before.
- **`check` and `pack` warn about a bare `ref:name` in a text that also holds a `${...}`.** Such a text renders as a template, so
  `ref:limit == ${header.x}` is text, not a comparison; the warning gives both correct spellings.
- **`ref:name` reads a registry object in an expression,** as Apache Camel's `${ref:name}`: `${ref:chat.settings}` keeps the
  object, `ref:limits.max > 10` compares it. `check` and `pack` warn about a name no `<bean>` declares.
- **VS Code extension: bean names in expression bean calls.** After `${bean:` in a template and after a bare `bean:`
  in an expression attribute (`expr="bean:orders.IsGold(body)"`), completion offers the declared beans.
- **VS Code extension: the bean step.** `<bean ref="#svc" method="Handle"/>` and `<bean beanType="..."/>` in a route
  or a scope get schema completion and validation, bean hints in `ref=`, and a user-code tile in the route graph.
- **VS Code extension: data format hints.** `<marshal>`, `<unmarshal>` and `<dataFormat>` complete `format=` names,
  `type=` type names, the options of the format `type=` names with their values and defaults, and the `target=` of
  `<unmarshal>`; hovering an option shows its description.
- **VS Code extension: path hints for data format schemas.** `schema=` offers the files of the module's `resources/`
  folder, and `schemaRoot=` the folders under it relative to the schema's folder.
- **VS Code extension: bean names in expression registry reads.** After `${ref:` in a template and after a bare `ref:`
  in an expression attribute (`expr="ref:rates"`), completion offers the declared beans.
- **VS Code extension: bean names in reference attributes before the `#`.** In an attribute that takes a registry
  reference (`predicate=`, `repository=`, `processor=`, `onRedelivery=` and the others), completion now offers the
  declared beans on an empty value and inserts them with the `#`.
- **A component declares the only side it has.** `[EndpointRole(EndpointRole.Producer, Hint = "...")]` on a component
  class is Apache Camel's `producerOnly` (`Consumer` is `consumerOnly`); bean, cache, embed, fcm, knowledge, log, mcp,
  smtp, sns, stt, validator and xslt are producer-only, imap, pop3, timer, cron and qtimer consumer-only. A route that
  uses one on its missing side is refused in one message: "smtp has no consumer side: a route cannot start from it".
- **`format="csv"` and the other data format names work after `AddXxxDataFormat()`.** Each package registers its name
  as Apache Camel names it (`csv`, `yaml`, `avro`, `protobuf`, `mimeMultipart`, `xmljson`); before, a name came only
  from `<dataFormat name="...">`.
- **`env:NAME` reads an environment variable in an expression,** as Apache Camel's `${env:NAME}`: `${env:BILLING_TOKEN}`
  in a template, `env:REGION == 'eu'` in a condition. A variable that is not set is `null`; a template's sandbox refuses it.
- **`FileLanguage`, `IFileFacts` and `GenericFileFacts`**: the table of file keys and the resolver that templates and the
  file consumers share. `GenericFileConsumer` has `PostProcessAsync` and `OnProcessingFailedAsync` overloads taking the
  `GenericFileInfo` and its work path; the path forms keep working for subclasses that override them.
- **`${file:name}`, `${file:name.noext}`, `${file:size}` and the other keys of Apache Camel's file language** read the
  `redbFile.*` headers a file consumer sets. Before, they rendered empty outside the file components' own options; an
  unknown key is now an error when the route is built.
- **`${jsonpath:$.order.id}` and `${xpath:/order/id}` read the body,** as in Apache Camel: they mean `jpath('...')` and
  `xpath('...')`. Before, they rendered empty.
- **The route decides what happens to each mail it consumed.** Set `redbMail.Delete` (`true` deletes, `false` marks
  read on IMAP or keeps on POP3), `redbMail.MoveTo` or `redbMail.CopyTo` (IMAP folders) on the message, as Apache
  Camel's `CamelMailDelete`, `CamelMailMoveTo` and `CamelMailCopyTo` do; they override `postProcess` for that mail.
- **`delete` and `copyTo` on `imap://` and `pop3://`.** Apache Camel's names beside `postProcess`: `delete=true` deletes
  a mail whose exchange succeeded (moved to `moveTo` first when set), `copyTo` copies it to an IMAP folder first. A
  `delete` that contradicts an explicit `postProcess` stops the route at start, naming both.
- **`idempotentRepository` on `imap://` and `pop3://`.** Apache Camel's option: a registered `IIdempotentRepository`
  (a persistent one keeps the keys across restarts) skips the mails it holds; a failed exchange releases the key, a
  committed one removes it or, with `idempotentRepositoryRemoveOnCommit=false`, confirms it.
- **`mcp:` in a package's markup and in the catalog.** `McpComponent` gets a constructor without arguments that takes
  the `IMcpRegistry` the context holds (from `ctx.AddService` in a module's `InitRoute`, or the host's
  `AddRedbRouteMcp()`), so `<component type="…McpComponent"/>` and the editor's catalog know the scheme; `new
  McpComponent(registry)` is unchanged. The option is `callTimeoutMs` (milliseconds), as the documentation now says.
- **`IPackageCheck`**: a package of the route format can add its own checks to `redb-route-xml check` and `pack`. With
  `--bin` the tool runs every public implementation in the build output's `redb.Route.*.dll` beside the gate's checks;
  `RoutePackage.Check` and `Build` take them as `checks`.
- **The component catalog marks options that name a registry object.** `connectionFactory`, `headerFilterStrategy`,
  `idempotentRepository`, `dataSource` and the other such options carry `[RegistryReference(typeof(T))]`, and the
  catalog writes the expected type as the option's `reference`. Connector authors mark their own options the same way.
- **VS Code extension: bean hints for connector options that name a registry object.** `dataSource`,
  `connectionFactory`, `headerFilterStrategy` and the other options the connector catalog marks offer the declared
  beans before the `#` is typed, in a URI and in the structured form, with the expected type in the item and the
  hover.
- **New package `redb.Route.DataFormats.XmlJson`: XML to JSON and back without a class.** `MarshalXmlJson()` /
  `<marshal format="xmljson"/>` writes XML as JSON (`@attribute`, `#text`), and a single element that may repeat stays an
  array when an XSD (`schema=`) or an `arrays=` path says so; `UnmarshalXmlJson<string>()` writes JSON back as XML in the
  schema's order and namespaces.
- **`check` and `pack` verify an xmljson format's schema and arrays paths.** The schema must be a file of the package and
  an `arrays=` path well formed; a format with neither is warned. With `--bin` the format compiles the schema with its own
  reader, walks each `arrays=` path through the schema's structure, flags `xs:any` and mixed content, and with
  `plural=true` names the pairs it reads as lists.
- **`check` and `pack` compile the schemas of `<validateXsd file="…">`.** An import that is missing, outside
  `schemaRoot` or on the network, or a schema that does not compile, is now an error at the gate instead of at the route
  start; so is a `schemaRoot` that leads out of `resources/`.

### Changed

- **`AND` and `OR` short-circuit.** The right side runs only when the left side does not decide the result, so a side
  effect or an error on a right side that does not matter no longer happens; the results are unchanged. Upgrade: [UPGRADING.md](UPGRADING.md#and-and-or-short-circuit).
- **`AstNode.Evaluate` runs the compiled expression.** A tree parsed by hand and evaluated directly gives what the
  engine gives for the same text, increments included; there is one evaluator instead of two. Upgrade: [UPGRADING.md](UPGRADING.md#astnodeevaluate-runs-the-compiled-expression).
- **A payload template's `expr(...)` evaluates its text as a `${...}` hole does.** Text without a spaced operator used
  to pass through unevaluated (`expr('1>0')` gave `1>0`, a `bean:` call came back as text); now every `expr(...)` is
  read by the same engine, and a constant belongs in the template text. Upgrade: [UPGRADING.md](UPGRADING.md#a-payload-templates-expr-evaluates-its-text).
- **`++` / `--` on a value that is not a number is an error.** It used to read the value as 0 and overwrite it; it now
  fails naming the value. A missing value still starts at 0. Upgrade: [UPGRADING.md](UPGRADING.md#incrementing-a-value-that-is-not-a-number-is-an-error).
- **`??` evaluates its right side only when the left side is `null`.** In a compiled expression the right side used to
  run always; a side effect or an error there now happens only when its value is used. The result is unchanged. Upgrade: [UPGRADING.md](UPGRADING.md#and-and-or-short-circuit).
- **An unclosed `${` is an error.** A template whose `${` has no closing `}` used to render as text or as an empty
  value; it now fails with `unclosed '${' at position n of "...": the placeholder has no closing '}'.`, at load for a
  route file and in `check` / `pack`. A placeholder may now hold another one and a quoted `}`:
  `${header.a == '}'}` compares instead of being cut at the first `}`. Upgrade: [UPGRADING.md](UPGRADING.md#an-unclosed--is-an-error).
- **An unended string literal is an error.** A quoted literal without its closing quote (`header.kind == 'order`) used
  to be read one character short; it now fails with `unterminated string literal starting at position n within the
  expression "..."`, at load for a route file. Upgrade: [UPGRADING.md](UPGRADING.md#an-unended-string-literal-is-an-error).
- **Bare text that breaks off as arithmetic is an error.** `1 +` used to give `1`, `header.a+b` gave `2b`, and
  `application/json` in an expression gave `null`; now each fails where the route is built, naming the place. Quote such
  text (`'application/json'`); text without `+ - * /` is unchanged. Upgrade: [UPGRADING.md](UPGRADING.md#bare-text-that-breaks-off-as-arithmetic-is-an-error).
- **The editor schema offers a scheme only on the side it has.** The Route-XML catalog writes `"role": "producer"` or
  `"consumer"` on a component with one side (no `role` means both), and the generated schema no longer has
  `<from><smtp/></from>`, `<from><log/></from>` or `<to><imap/></to>`: the 16 types of missing sides are gone, the
  other type names are unchanged.
- **`redb-route-xml catalog` names every component it leaves out.** A component without a constructor callable without
  arguments, or whose constructor throws, gets one warning line on stderr ("... its scheme is not in the catalog.")
  instead of being skipped in silence.
- **File keys in consumer options resolve or fail.** `moveTo`, `preMove`, `moveFailed`, `doneFileName` and
  `idempotentKey`, and S3's `doneFileName`, now take every key of the file language (`${file:ext}`, `${file:parent}`,
  `${file:size}`); a key other than `name` and `name.noext` used to stay in the path as written. An unknown key fails
  when the endpoint is created. Upgrade: [UPGRADING.md](UPGRADING.md#file-keys-in-consumer-options-resolve-or-fail).
- **Firebase Storage move prefixes take file keys.** `moveAfterRead` and `moveFailed` resolve `${file:ext}` and the other
  file keys from the object (`failed/${file:ext}/` moves `in/a.csv` to `failed/csv/in/a.csv`); the object keeps its name.
  A prefix without keys works as before, an unknown key fails when the endpoint is created. Upgrade: [UPGRADING.md](UPGRADING.md#file-keys-in-consumer-options-resolve-or-fail).
- **A format suffix in a placeholder formats.** `${header.amount:F2}` used to drop the suffix and write `2.5`; it now
  writes `2.50`, as `format(value, 'F2')` with the invariant culture. Upgrade: [UPGRADING.md](UPGRADING.md#a-format-suffix-in-a-placeholder-formats).
- **A missing `moveTo` or `copyTo` folder is created.** `imap://` creates the folder (with the folders above it) the
  first time a mail is moved or copied there, from the options or the `redbMail.MoveTo` / `redbMail.CopyTo` headers,
  as Apache Camel does; before, the commit failed and the poll stopped.
- **A method call on a value fails loudly.** `${header.o.Check()}` that throws, or a method the value does not have, used
  to read as `null`; it is now an error naming the call and its position. Method calls in comparisons
  (`header.s.length() > 10`) now compile. Upgrade: [UPGRADING.md](UPGRADING.md#a-method-call-on-a-value-fails-loudly).
- **A step on the side its component lacks fails when the route is built.** A static target on a receive-only
  component (timer, imap, ...) fails the route build in C#, and the XML loader and `check` refuse either mistake at its
  position; before, the step failed on the first exchange that reached it. Dynamic targets fail on the exchange as before. Upgrade: [UPGRADING.md](UPGRADING.md#a-step-on-the-side-its-component-lacks-fails-when-the-route-is-built).
- **Every expression runs on one engine.** A bare `Expr("header.o.Check()")` that throws is an error naming the call,
  as in templates and conditions, and a bare `exception.Message` reads the exception's message. Text that is not an
  expression (`hello`, `key=value`) is still the text itself. Upgrade: [UPGRADING.md](UPGRADING.md#a-method-call-on-a-value-fails-loudly).
- **`check` reads a bare name in an option that names a registry object.** `connectionFactory=main` and
  `headerFilterStrategy=filter` count as references like `#main` did, in the URI and the structured form, so a name no
  `<bean>` of the package declares gets the usual warning; module code that registers it at startup is still fine.
- **`check --bin` holds every typed registry-reference option against its type.** A `connectionFactory`,
  `idempotentRepository`, `dataSource` or other marked option naming a `<bean>` of the package whose type does not fit
  is now a gate error, as `headerFilterStrategy` already was; the check is per connector and reads bare names too. API
  note: `RoutePackage.Check` and `Build` type connector options only when the catalog is passed with the type resolver,
  as the `redb-route-xml` tool always does with `--bin`.
- **A `ref:name` or `env:NAME` refused in a template's sandbox says so.** The message used to call it a method
  (`Method 'ref:rules' on Rules cannot be called here`); it now names the spelling (`'ref:rules' cannot be read here`).
  `ExpressionSandboxViolationException` has a constructor taking the spelling and a `Written` property; `TargetType` is
  `null` for such a read.
- **`tempFileName` of the file producers reads file keys.** File, FTP and SFTP resolve `${file:name}.part` and the other
  file keys from the file being written, ignoring case as the consumer options do; before, the keys stayed in the temp
  name as written. A value without keys is unchanged, an unknown key fails when the endpoint is created. Upgrade: [UPGRADING.md](UPGRADING.md#file-keys-in-consumer-options-resolve-or-fail).
- **Text after a complete expression is an error.** `header.b == 3 garbage` and `${exception.type|message}` used to drop
  the rest silently; they now fail when the route is built, naming the text and its position, and `check` / `pack`
  report the same as an error instead of a warning. Upgrade: [UPGRADING.md](UPGRADING.md#text-after-a-complete-expression-is-an-error).
- **`ValidateXsd` / `<validateXsd>` accept a schema that starts with its own DOCTYPE,** such as W3C's xmldsig schema: the
  DOCTYPE is skipped and nothing in it is expanded. A message with a DTD is still refused.
- **`validateXsd` reads multi-file schemas.** `<validateXsd file="…" schemaRoot="…">`, `validator:schema.xsd?schemaRoot=…`
  and the new `ValidateXsdFile(path, targetNamespace, schemaRoot)` resolve `xs:import` and `xs:include` relative to the
  file, so UBL and ebMS3 validate. An import that is not found, lies outside `schemaRoot` or points to the network now
  fails the route start. Upgrade: [UPGRADING.md](UPGRADING.md#an-xsd-file-reads-its-imports).

### Deprecated

- **.NET 8 and .NET 9 targets end with 4.7.** Microsoft's support for both ends on 10 November 2026, so 4.7.0 is the
  last release whose packages target them; from 4.8.0 the packages target .NET 10 and later. Fixes on the 4.7.x line
  are still built for .NET 8, 9 and 10 while those lines can be built without known vulnerabilities. Move your projects
  to .NET 10 before taking 4.8.

### Fixed

- **A dot inside a method argument no longer splits the path.** `${header.list.contains(5.0)}` and
  `${header.names.contains('a.b')}` used to give `null`; they now call the method with the whole argument.
- **`<to><bean path/></to>` and `<to><log/></to>` load without a catalog schema.** The schema a route file is
  checked against at load read an endpoint child named like a step or like the `<bean>` declaration (`bean`, `log`,
  `xslt`) as that element and refused it. Such a child is now an endpoint child, and its options are checked where
  the endpoint is created, as for every other scheme.
- **A condition comparing numbers of different types, or a number with a numeric string, could be true when the left
  side was smaller.** `header.qty == 5.0` with `qty` = 3 was true; such pairs are now equal only when their values are.
  Check filters and `when` branches that compare an `int` header with a decimal or quoted literal. Upgrade: [UPGRADING.md](UPGRADING.md#comparing-numbers-of-different-types).
- **VS Code extension: hints in a tag whose attribute value holds `>`.** After an attribute such as
  `expr="header.amount>1000"`, the `#` bean hints and the skeleton completion now read the tag correctly, and the
  Routes view keeps a route's id and endpoint after a `description` holding `>`; before, the `>` in the value was
  taken for the end of the tag.
- **A constant cache key with a dash or a slash (`Cache("orders-all")`, `<cache key="reports/daily"/>`) is the key as
  written.** A key without `${...}` that starts with no root of the message is a constant; one that starts with a root
  (`header.customerId`) is still evaluated.
- **`check` no longer passes a resource that `pack` does not ship.** A `file=` or `schema=` naming a file beside the
  project (outside `resources/`) passed the check and failed on the worker; it is now an error that says to move it under
  `resources/`.
- **An HTTP `AuthToken` of `${env:BILLING_TOKEN}` sent an empty bearer token.** The expression language rendered
  `${env:...}` empty; it now reads the variable on every request, as the documentation said.
- **IBM MQ `connectionFactory` connects through the registered factory.** `wmq:` endpoints with `connectionFactory=name`
  now take the host, channel, queue manager and credentials from the registered `IbmMqConnectionFactory`, over the inline
  options; before, the inline options or their defaults were used. An unregistered name now fails the route start. Upgrade: [UPGRADING.md](UPGRADING.md#ibm-mq-connectionfactory-connects-through-the-registered-factory).
- **A mail whose route throws no longer stops the mails after it.** `imap://` (polling and `idle`) and `pop3://` leave
  that one message as it is and go on with the next; a POP3 poll now ends with QUIT, so the messages it deleted before
  the failure stay deleted instead of coming back and being routed again.
- **`idempotent=true` on mail skips only what was routed successfully.** A mail whose exchange failed is routed again,
  a mail that `minAge` or a filter held back is routed once it passes, and a POP3 mail without a Message-ID is told
  apart by its UIDL instead of its position, which named a different mail in the next session.
- **`searchQuery` on `imap://` says that it is not applied.** The option was documented as an IMAP SEARCH string that
  overrides `fetchFilter`, but the consumer never read it; the mails are selected by `fetchFilter` and the client-side
  filters. A URI that sets it still binds.
- **VS Code extension: the graph palette offers only connectors that can send.** The **Send to** list no longer offers
  `<to>` for a connector that only receives (`imap`, `pop3`, `timer`, `cron`, `qtimer`).
- **`validateXsd` takes the schema's own target namespace.** Without `targetNamespace` (`ValidateXsd(xsd)`,
  `<validateXsd file=...>`, `validator:`), a schema with a `targetNamespace` failed to load with "The targetNamespace
  parameter '' should be the same value as ..."; it now loads, as documented. An explicit namespace works as before.

## Earlier versions

Earlier releases are archived by line in [changelog/](changelog/): [4.6.x](changelog/CHANGELOG-4.6.x.md), [4.5.x](changelog/CHANGELOG-4.5.x.md), [4.4.x](changelog/CHANGELOG-4.4.x.md), [4.3.x](changelog/CHANGELOG-4.3.x.md), [4.2.x](changelog/CHANGELOG-4.2.x.md), [4.1.x](changelog/CHANGELOG-4.1.x.md), [4.0.x](changelog/CHANGELOG-4.0.x.md), [3.x](changelog/CHANGELOG-3.x.md), [2.x](changelog/CHANGELOG-2.x.md), [1.x](changelog/CHANGELOG-1.x.md).
