# SerialNumbers.Xml — the same integration, spelled in XML

This is the module next door, `SerialNumbers.Core`, with one difference: the flow lives in
`context.xml` and `routes/*.route.xml` instead of `RouteBuilder` classes. The domain, the
services, the database and the worker are the same code — nothing was rewritten to make the
markup possible.

| Path | What |
|---|---|
| `routes/*.route.xml` | the routes: intake, request, outbox, delivery, reports |
| `context.xml` | the objects the routes reference by name: the beans the routes call |
| `Beans/SerialBeans.cs` | ten wrappers: a route says `bean:#serial-requests?method=Register`, the wrapper hands the exchange to the service |
| `InitRoute.cs` | the module bootstrap — the part markup cannot express (see below) |
| `resources/` | `SerialNumberRequest.xsd`, resolved from the package root by `<validateXsd file=…>` |
| `config/SerialNumbers.Xml.config.json` | module identity and the settings the routes read as `{{placeholders}}` |

## What XML buys, and what it costs

**Buys.** The pipeline is readable without a compiler: what `<transaction>` encloses is what
commits together, and the branch names of a `<choice>` are the decision table. The package deploys as a `.tpkg` the worker hot-reloads,
and the graph editor of the VS Code extension draws it as it is.

**Costs.** Two things the C# spelling does that markup does not:

1. **Partner topology.** `SerialNumbers.Core` generates one SFTP consumer and one delivery
   route *per partner read from the database*. Markup is static, so here every partner is
   written out: `acme` over SFTP, `globex` over AS2. Choose by how partners change — by
   deploy, or at runtime.
2. **The module bootstrap.** The demo database is created and seeded from code, and this module
   reuses the C# module's `InitRoute.main` for it - which also registers the components, the
   `sql:` data source and the partner connections. Those four are declarable (see below); they
   stay in code only while the bootstrap is shared.

That boundary is the point of this demo: markup describes the flow, code provides what has to
be code.

Certificates used to belong to the second group and no longer do. A `<property>` takes a nested
anonymous `<bean>`, and `factoryMethod=` calls a static creator, so an AS2 partner is declarable
in full - key material included:

```xml
<bean name="globex" type="redb.Route.As2.As2ConnectionFactory, redb.Route.As2">
  <property key="OurCertificate">
    <bean type="System.Security.Cryptography.X509Certificates.X509CertificateLoader, System.Security.Cryptography.X509Certificates"
          factoryMethod="LoadPkcs12FromFile">
      <constructorArg value="{{As2.CertificateDirectory}}/hub.pfx"/>
      <constructorArg value="{{As2.CertificatePassword}}"/>
    </bean>
  </property>
  <property key="As2From" value="{{As2.Id}}"/>
  <property key="As2To" value="GLOBEX"/>
  <property key="Sign" value="true"/>
</bean>
```

## Build and deploy

    dotnet build -p:PackRouteOnBuild=true -p:Version=1.0.0
    # -> pkg/serial-numbers-xml-1.0.0.tpkg, checked by the Ф5.4 gate against the build output

Drop the `.tpkg` into the worker's `modules/`. Run it INSTEAD of the C# module, not beside it:
both read the same demo database, so they share its partners and their SFTP folders and would
race for the same files. Everything else is already distinct - context name, API port (8091),
AS2 port (4082), archive and report folders - so switching spellings is dropping one `.tpkg`
and removing the other.
