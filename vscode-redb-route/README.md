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
- **Mermaid diagram** command (`redb Route: Show Mermaid Diagram`) via the `redb-route-xml`
  dotnet tool, when installed (`dotnet tool install -g redb.Route.Xml.CodeGen`). Everything else works without .NET on the machine.

## Development

```bash
npm install
npm run compile
npm test              # unit tests of the sniff and the route scan (node:test, no deps)
```

Press `F5` in VSCode to launch an Extension Development Host with the extension loaded.

`npm run refresh-schema` regenerates `media/redb-route-1.0.xsd` from the format's single
source of truth (the element registry) via the `redb.Route.Xml.CodeGen` tool.
