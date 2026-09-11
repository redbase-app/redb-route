# XmlDemo — the redb storage bridge in XML markup

A minimal, self-limiting demo of the redb bridge elements (`<redbSave>`, `<redbQuery>`,
`<redb><syncScheme/></redb>`) as a deployable XML route package for the Tsak worker.
Every 5 seconds the route upserts ONE row (`SaveByUniqueAsync` by the object's
`value_unique`), queries it back server-side and logs `REDBDEMO total=1` — the count
stays flat no matter how long the worker runs.

That flatness is the point. The first cut of this demo saved a new `${uuid()}` row per
tick: the table grew forever, the query slowed with it, and on SQLite the 5s cycle
degraded to ~50s with `database is locked` storms. A demo that runs unattended must be
bounded by construction, not by somebody remembering to clean up.

The layout is the packaging convention:

| Path | What |
|---|---|
| `routes/*.route.xml` | the route artifacts (explicit manifest order) |
| `context.xml` | the `<redb>` contribution: scheme sync at context start |
| `config/XmlDemo.config.json` | L4: module identity ONLY (`ContextName: xmldemo`) |
| `config/context.sample.json` | L3 sample for deployment |
| `schema/` | generated XSD — OPTIONAL, see below |

The `schema/` folder is **not required** — neither the worker nor the pack gate reads it
(both validate against their own registry). It exists only so the editor works when the
`redb Route XML` VS Code extension is NOT around: open this folder as its own workspace
root and the plain RedHat XML extension picks the schema up through `.vscode/settings.json`.
With our extension installed the namespace catalog already validates every `*.route.xml`
and `context.xml`, and this local copy is ignored. Delete it if you never open the project
standalone; regenerate it (with the package contributions) via:

    redb-route-xml xsd <worker>/Libs/shared --out schema

Build the package — `--bin` points at the worker's shared assemblies so the pack gate
sees the same markup contributions (`redbSave`, `redbQuery`, …) the worker's own
discovery will load; without it the redb elements fail the XSD check:

    redb-route-xml pack . --name xmldemo --version 3.0.1 --bin <worker>/Libs/shared

Deploy: drop `pkg/xmldemo-<version>.tpkg` into the worker's `modules/` — it hot-reloads
in a few seconds and the log shows the `REDBDEMO total=1` ticks.

Regenerate C# or a diagram from the route:

    redb-route-xml csharp routes/main.route.xml --namespace XmlDemo.Routes
    redb-route-xml mermaid routes/main.route.xml
