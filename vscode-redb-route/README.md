# redb Route XML — VSCode extension

Text mode for [redb.Route](https://github.com/redbase-app/redb-route) XML routes. The format stays
fully usable without any editor — this extension only makes the text pleasant:

- **Completion, validation and enum hints** for every element of the format, powered by
  [Red Hat XML](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-xml) reading
  the shipped XSD. The binding is **namespace-driven**: a document gets the schema because its
  root says `urn:redb:route:1.0` — never because of its file name. Someone else's `route.xml`
  is left alone.
- **Route tree** in the Explorer sidebar: every routes document of the workspace, its routes
  with ids and `from` endpoints; a click opens the exact line.
- **Skeletons**: composite inserts the schema cannot offer - `<routes` (document with xmlns), `<route` (with its consumer), `<choice` (both branches), `<tryCatch` (catch+finally) - served by the extension itself, ONLY in redb documents and only at a new-element position; single elements come from the XSD completion.
- **Route graph** (`Open With` → `redb Route Graph`): every step is a tile of one size, a
  colored glyph and a short caption (`from`, `bean`, `sql`, `set`, `choice`, `when`, `loop`,
  `log`…); what exactly a step does (the property name, the condition, the SQL) is in the
  tooltip and in the properties panel on click. The toolbar switches the layout (left to
  right or top down), folds or unfolds every container at once (⊟ / ⊞; a route folds to its
  title with the ▾ before its name), and switches the
  tile look (glyph over the caption or beside it); the tile look
  is remembered for all documents. No container draws a box: a wrapper (`metered`, `loop`,
  `split`, a transaction) puts its steps into the route's line between its tile and an
  `end …` tile, and a loop adds a `repeat` arrow back to its start; `choice`, `try-catch` and
  `filter` fan out into branches (a filter with a bypass for what it did not let in). Edges
  run in straight segments only and branches merge on a shared bus, so no line crosses the
  drawing. A run of `setProperty` / `setHeader` steps is one tile `set ×N` whose panel edits the
  rows in order. Conditional paths are dashed: orange where a message goes past (filter not
  passed, no match, other type, duplicate), grey with a cross where it is dropped (`sample`,
  `debounce`), red where a step raises (`→ catch`, `→ onException`, `→ error`).
  The glyph colors are groups: green where the message comes in, blue where it goes out,
  purple your code (`bean`), orange data changes, cyan routing, grey observation, red errors.
- **Mermaid diagram** command (`redb Route: Show Mermaid Diagram`) via the `redb-route-xml`
  dotnet tool, when installed (`dotnet tool install -g redb.Route.Xml.CodeGen`). Everything else works without .NET on the machine.

## Install

The extension is not on the Marketplace; every redb.Route release carries it as a file.

1. Open the [releases of redb-route](https://github.com/redbase-app/redb-route/releases), pick the
   version you use and download `redb-route-xml-<version>.vsix` from its **Assets**.
2. In VS Code: **Extensions** view → `…` → **Install from VSIX…** → the downloaded file. Or from a
   terminal: `code --install-extension redb-route-xml-<version>.vsix`.
3. **Developer: Reload Window**. A graph tab opened before the update keeps the old script: close
   and reopen it.

It depends on [Red Hat XML](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-xml)
(`redhat.vscode-xml`), which VS Code installs on its own when it has network access. On an offline
machine, download that extension's `.vsix` from the Marketplace page first and install it the same
way before this one.

Open a route file with **Open With…** → **redb Route Graph**, the graph button in the editor title,
or **redb Route: Open Route Graph** from the command palette.

## Development

```bash
npm install
npm run compile
npm test              # unit tests of the sniff and the route scan (node:test, no deps)
```

Press `F5` in VSCode to launch an Extension Development Host with the extension loaded.

`npm run refresh-schema` regenerates `media/redb-route-1.0.xsd` from the format's single
source of truth (the element registry) via the `redb.Route.Xml.CodeGen` tool.

## Credits

The graph glyphs follow the notation of *Enterprise Integration Patterns* by Gregor Hohpe and
Bobby Woolf ([enterpriseintegrationpatterns.com](https://www.enterpriseintegrationpatterns.com/),
pattern icons under CC BY 4.0), redrawn as 24×24 line icons; the steps the notation has no
symbol for are drawn in the same style.
