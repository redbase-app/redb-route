# redb Route XML

Editor support for [redb.Route](https://github.com/redbase-app/redb-route) XML routes in VS Code: new projects from
the redb templates, completion and validation from the format's schema, a route tree, composite skeletons,
bean reference hints and a graph editor over the XML text.

The format does not need an editor. A route file is plain XML that the runtime loads as it is; this extension only
makes writing and reading it easier.

## Features

### New project

**redb: New Project** from the Command Palette opens a page that creates a project from the redb templates
through `dotnet new`:

- **Status.** The top of the page shows the installed .NET SDKs and the two template packages, `redb.Templates`
  and `redb.Tsak.Templates`, with an **Install** button for a missing package and an **Update** button when
  nuget.org has a newer version.
- **Templates.** Every project and solution template of the installed packages is a card, grouped as redb.Route
  XML modules, redb.Tsak hosts and applications (console, Blazor, Razor Pages, worker, chat). The basic
  `redb-route` module is selected when the page opens.
- **Form.** The chosen template's form asks for the project name, the parameters that template declares (for
  example the target framework or the database) and the location. A target framework with no SDK for it on this
  machine is marked, and a location where a folder of that name already holds files is refused.
- **Create.** **Create** runs `dotnet new` and, when the project carries a local tool manifest,
  `dotnet tool restore`. Their output shows on the page and in the **redb Route** output channel, and a running
  step can be cancelled. When it is done, the page offers to open the project in this window, in a new window or to
  add it to the workspace.

The cards and the form come from the templates themselves, so a new template in the redb template packages
appears without an update of this extension.

### Schema: completion and validation

Completion of elements, attributes and enum values, and validation as you type, for every element of the format and
for the typed options of every connector (`<kafka topic>`, `<sql dataSource>`, `<as4 path>` and so on). The schema
also knows which side an option belongs to: an option that only a consumer takes is accepted inside `<from>` and
refused inside `<to>`.

The work is done by [Red Hat XML](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-xml) reading
the schema this extension ships. The binding goes by namespace, not by file name: a document gets the schema
because its root element declares `urn:redb:route:1.0`. Any other XML file, including someone else's
`route.xml`, is left alone.

### Skeletons

Composite inserts the schema cannot offer, served only in redb route documents and only where a new element may
start:

| Skeleton | Inserts |
|---|---|
| `<route` | a route with its consumer |
| `<choice` | a choice with a `when` branch and `otherwise` |
| `<tryCatch` | a try block with `catch` and `finally` |

Single elements come from the schema completion.

### Bean references

Typing `#` where a value names a registry object lists the `<bean name>` declarations of the current document,
then those of the module's `context.xml`. The module is the nearest folder with a `.csproj` up to three levels
above the file, the project root `redb-route-xml pack` builds from; an unsaved `context.xml` open in an editor is
read as it stands. This works in route files and in `context.xml` itself, in three places: the whole attribute
value (`dataSource="#"`), the path right after the scheme (`bean:#`) and a URI option value
(`?headerFilterStrategy=#`). A `#` inside other text, such as the SQL parameter `:#name`, is not a reference and
gets no hint. An attribute that only takes a reference (`predicate=`, `repository=`, `processor=`,
`onRedelivery=` and the others the schema types that way) offers the same list on an empty value, before the `#`
is typed, and inserts the name with it. So does a connector option that names a registry object
(`dataSource` of `sql`, `connectionFactory`, `headerFilterStrategy`, `idempotentRepository` and the others the
connector catalog marks), as a URI option (`uri="sql:select 1?dataSource="`) and as an attribute of the
structured form (`<sql dataSource="">`); the item and the hover on the option name show the type the connector
expects.

The bean step `<bean ref="#pricing" method="Gross"/>` (or `beanType="Namespace.Type, Assembly"`) in a route or a
scope is completed and validated like any other step, its `ref=` gets the same list, and the graph shows it as a
user-code tile, the same one as `<to><bean path/></to>` and `<to uri="bean:..."/>`. A `<bean name type>` directly
under `<routes>` or `<context>` stays a declaration: it is what the list offers and what the graph shows in its
beans row.

A bean call of the expression language gets the same list for its bean name: after `${bean:` in any template
(`<setBody value="Total: ${bean:pricing.Gross(body)}"/>`, element text, `toD`), and after a bare `bean:` in an
expression attribute (`<filter expr="bean:orders.IsGold(body)">`), at the start or after an operator. The method
after the `.` is not offered: the editor does not know the bean's methods. A registry read `${ref:` or a bare
`ref:` (`<setProperty name="rates" expr="ref:rates"/>`) gets the list in the same places; its name is the whole run, so nothing
follows it.

The list is not filtered by type, and beans registered from C# code are not in it. A reference to a bean that is not declared, or of the wrong type, is reported by
`redb-route-xml check` and `redb-route-xml pack`.

### Data formats

In `<marshal>`, `<unmarshal>` and `<dataFormat>`, `format=` offers the registered format names (`xmljson`, `csv`,
`yaml`...) and `type=` the format type names. Once `type=` names a format, the attribute list offers that
format's options with their kind and default (`schema`, `arrays`, `namespaces` for xmljson), an enum or boolean
option offers its values, and `target=` of `<unmarshal>` offers the targets the format accepts. Hovering an option
or the `type=`/`format=` value shows its description.

A path option offers what the package will carry: `schema=` the files under the module's `resources/` folder, as
the short path the runtime reads (`xsd/order.xsd`), and `schemaRoot=` the folders under `resources/` relative to the
schema's folder (`..`, `common`). A file elsewhere in the project folder is not offered: `pack` does not ship it.

### Route tree

The **redb** button in the Activity Bar opens the **Routes** view: every routes document of the workspace
(`*.route.xml`, `*.redb-route.xml`), with the id and the `from` endpoint of each route. A click opens the route at
its line. The view's title bar has **New Project** and **Refresh**; in a workspace without route files the view
offers **New Project** and a link to the format.

### Route graph

**Open With...** > **redb Route Graph**, the graph button in the editor title, or **redb Route: Open Route Graph**
from the Command Palette opens the route file as a graph. The graph is a second view of the same text: every edit in
it is an edit of the XML, and undo works as in the text editor.

- **Reading.** Every step is a tile of one size with a glyph and a short caption (`from`, `bean`, `sql`, `set`,
  `choice`, `loop`, `log`...). The tooltip and the properties panel show what the step does: the property name,
  the condition, the SQL. Wrappers (`metered`, `loop`, `split`, a transaction) put their steps on the route's line
  between their own tile and an `end` tile; `choice`, `tryCatch` and `filter` fan out into branches. A run of
  `setHeader` or `setProperty` steps is one tile `set ×N`.
- **Paths.** Conditional paths are dashed: orange where a message goes past a step (filter not passed, no match,
  duplicate), grey with a cross where it is dropped (`sample`, `debounce`), red where a step raises (`catch`,
  `onException`, error).
- **Colors.** Green where a message comes in, blue where it goes out, purple your code (`bean`), orange data
  changes, cyan routing, grey observation, red errors.
- **Editing.** Insert a step from the palette at any `+` between steps, delete with `Delete`, drag a step to
  reorder it or to move it into or out of a container, rename an element, edit attributes and child texts in the
  properties panel. The palette's **Send to** list holds the connectors that can send; one that only receives
  (`imap`, `pop3`, `timer`, `cron`, `qtimer`) is not offered there.
- **Endpoints.** For a step with a URI the panel splits it into the scheme, the path and the options of that
  connector, with enum values offered as a list. A sensitive option (a password, a key) is masked and
  cannot be edited from the panel.
- **Placeholders.** A `{{key}}` or `{{key:default}}` value is shown together with what it resolves to, read from
  `context.sample.json` and `config/*.json` found next to the route file or in up to three folders above it. The
  edit always keeps the placeholder.
- **Layout.** The toolbar switches left to right or top down, folds or unfolds every container at once, zooms and
  switches the tile look (glyph over the caption or beside it). A route folds to its title with the arrow before
  its name.

### Mermaid diagram

**redb Route: Show Mermaid Diagram** renders the current route file as a Mermaid diagram with the `redb-route-xml`
.NET tool:

```bash
dotnet tool install -g redb.Route.Xml.CodeGen
```

This and **New Project** are the only features that need .NET on the machine.

## Requirements

- [Red Hat XML](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-xml) (`redhat.vscode-xml`).
  VS Code installs it together with this extension when it has network access.
- The `redb-route-xml` .NET tool, only for the Mermaid diagram.
- A .NET SDK and the redb template packages (`dotnet new install redb.Templates`, `dotnet new install
  redb.Tsak.Templates`), only for **New Project**. Its page offers to install the packages itself.

## Installation

From the Visual Studio Marketplace: in the **Extensions** view search for `redb Route XML` and click **Install**,
or open the [extension page](https://marketplace.visualstudio.com/items?itemName=redb.redb-route-xml). From a
terminal: `code --install-extension redb.redb-route-xml`. VS Code then keeps the extension up to date.

Every redb.Route release also carries the extension as a file, for a machine without access to the Marketplace or
to pin a version:

1. Open the [releases of redb-route](https://github.com/redbase-app/redb-route/releases), pick the version you use
   and download `redb-route-xml-<version>.vsix` from its assets.
2. In VS Code: **Extensions** view > `...` > **Install from VSIX...** > the downloaded file. From a terminal:
   `code --install-extension redb-route-xml-<version>.vsix`.
3. Run **Developer: Reload Window**. A graph tab opened before an update keeps the old view until it is closed and
   opened again.

On a machine without network access, download the Red Hat XML `.vsix` from its Marketplace page and install it
first, the same way.

## Settings

The extension has no settings of its own. It registers its XML catalog with Red Hat XML through that extension's
API. With an older Red Hat XML that has no such API, it adds the catalog to the user setting `xml.catalogs` once.

## Documentation

- [redb.Route.Xml](https://github.com/redbase-app/redb-route/blob/main/redb.Route.Xml/README.md): the XML format,
  elements, endpoints, beans, the context document.
- [Schema and editor support](https://github.com/redbase-app/redb-route/blob/main/redb.Route.Xml/README.md#schema-and-editor-support)
  and [tooling](https://github.com/redbase-app/redb-route/blob/main/redb.Route.Xml/README.md#tooling-scaffold-pack-generate):
  the schemas, `check`, `pack` and the code generator.

## Development

```bash
npm install
npm run compile
npm test
```

`npm test` runs the unit tests with `node:test`, without VS Code. Press `F5` to start an Extension Development Host
with the extension loaded.

The files in `media/` other than the graph's script and styles are generated by the `redb-route-xml` tool from the
built connector assemblies (`catalog`, `elements` and `xsd` commands). They are not edited by hand.

## License

[Apache License 2.0](https://github.com/redbase-app/redb-route/blob/main/vscode-redb-route/LICENSE).

## Credits

The graph glyphs follow the notation of *Enterprise Integration Patterns* by Gregor Hohpe and Bobby Woolf
([enterpriseintegrationpatterns.com](https://www.enterpriseintegrationpatterns.com/), pattern icons under
CC BY 4.0), redrawn as 24×24 line icons. Steps the notation has no symbol for are drawn in the same style.
