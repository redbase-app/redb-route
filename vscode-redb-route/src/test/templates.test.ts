import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import {
    findTemplatePackage, isTemplateDefinition, parseTemplateRegistry, readTemplateDefinitions,
    orderTemplates, selectTemplates, stripJsonComments, templateRegistryPath,
} from "../templates";
import { buildZip } from "./zipfixture";

/** A template.json in the agreed shape; `patch` overrides top-level fields. */
function definition(patch: Record<string, unknown> = {}): string {
    return JSON.stringify({
        identity: "RedBase.Route.Xml.Base",
        name: "redb.Route XML module: Base",
        description: "An empty Tsak module with one small XML route.",
        shortName: "redb-route",
        classifications: ["Integration", "XML", "Tsak", "RedBase", "RouteXml"],
        tags: { type: "project" },
        sourceName: "RouteModule",
        symbols: {
            context: { type: "parameter", datatype: "string", defaultValue: "", displayName: "Context",
                description: "Route context name" },
            framework: {
                type: "parameter", datatype: "choice", defaultValue: "net10.0", displayName: "Framework",
                description: "Target framework",
                choices: [
                    { choice: "net8.0", description: ".NET 8" },
                    { choice: "net9.0", description: ".NET 9" },
                    { choice: "net10.0", description: ".NET 10" },
                ],
            },
            UseSqlite: { type: "computed", value: "(db == \"sqlite\")" },
        },
        ...patch,
    });
}

const REGISTRY = "\uFEFF" + JSON.stringify({
    Packages: [
        { Details: { PackageId: "redb.Tsak.Templates", Version: "4.4.2" },
          MountPointUri: "C:\\Users\\u\\.templateengine\\packages\\redb.Tsak.Templates.4.4.2.nupkg" },
        { Details: { PackageId: "redb.Templates", Version: "4.4.3" },
          MountPointUri: "C:\\Users\\u\\.templateengine\\packages\\redb.Templates.4.4.3.nupkg" },
        { Details: { PackageId: "broken" } },
        { MountPointUri: "D:\\templates\\dev" },
    ],
});

describe("templates — the engine's registry", () => {
    it("reads packages.json with its BOM and skips incomplete entries", () => {
        const packages = parseTemplateRegistry(REGISTRY);
        assert.deepEqual(packages.map(p => p.packageId), ["redb.Tsak.Templates", "redb.Templates"]);
        assert.equal(packages[1].version, "4.4.3");
    });

    it("finds a package by id whatever the case", () => {
        const packages = parseTemplateRegistry(REGISTRY.replace("\"redb.Templates\"", "\"Redb.templates\""));
        assert.equal(findTemplatePackage(packages, "redb.Templates")?.version, "4.4.3");
        assert.equal(findTemplatePackage(packages, "redb.tsak.templates")?.version, "4.4.2");
        assert.equal(findTemplatePackage([], "redb.Templates"), null);
    });

    it("reads an empty or foreign registry as no packages", () => {
        assert.deepEqual(parseTemplateRegistry("{}"), []);
    });

    it("keeps the registry under DOTNET_CLI_HOME when it is set", () => {
        assert.equal(templateRegistryPath({}, "/home/u"), path.join("/home/u", ".templateengine", "packages.json"));
        assert.equal(templateRegistryPath({ DOTNET_CLI_HOME: "/cli" }, "/home/u"),
            path.join("/cli", ".templateengine", "packages.json"));
        assert.equal(templateRegistryPath({ DOTNET_CLI_HOME: " " }, "/home/u"),
            path.join("/home/u", ".templateengine", "packages.json"));
    });

    it("recognises template definitions inside a package", () => {
        assert.equal(isTemplateDefinition("content/redb-route/.template.config/template.json"), true);
        assert.equal(isTemplateDefinition("content\\redb-route\\.template.config\\template.json"), true);
        assert.equal(isTemplateDefinition("content/redb-route/template.json"), false);
        assert.equal(isTemplateDefinition("content/redb-route/.template.config/dotnetcli.host.json"), false);
    });
});

describe("templates — which templates the page offers", () => {
    const select = (definitions: string[]) => selectTemplates(definitions, "redb.Templates");

    it("takes project and solution templates and groups them", () => {
        const templates = select([
            definition(),
            definition({ shortName: "redb-app", name: "RedBase Blazor WebAssembly App", classifications: ["Web", "RedBase"],
                tags: { language: "C#", type: "solution" } }),
            definition({ shortName: "tsak-worker", name: "redb.Tsak Worker Host", classifications: ["Worker", "redb.Tsak"] }),
        ]);
        assert.deepEqual(templates.map(t => [t.shortName, t.type, t.group, t.packageId]), [
            ["redb-route", "project", "route-xml", "redb.Templates"],
            ["redb-app", "solution", "apps", "redb.Templates"],
            ["tsak-worker", "project", "tsak", "redb.Templates"],
        ]);
    });

    it("orders by group, the default first, then by name", () => {
        const ordered = orderTemplates([
            ...select([definition({ shortName: "redb", name: "RedBase Console Application", classifications: ["Console"] })]),
            ...select([definition({ shortName: "redb-route-kafka", name: "redb.Route XML module: Kafka" })]),
            ...selectTemplates([definition({ shortName: "tsak-worker", classifications: ["redb.Tsak"] })], "redb.Tsak.Templates"),
            ...select([definition()]),
            ...select([definition({ shortName: "redb-route-beans", name: "redb.Route XML module: Beans" })]),
        ]);
        assert.deepEqual(ordered.map(t => t.shortName), ["redb-route", "redb-route-beans", "redb-route-kafka", "tsak-worker", "redb"]);
    });

    it("skips item templates, unnamed ones and broken definitions", () => {
        const templates = select([
            definition({ tags: { type: "item" } }),
            definition({ tags: {} }),
            definition({ shortName: [] }),
            "not json",
        ]);
        assert.deepEqual(templates, []);
    });

    it("calls a template by the first of its short names", () => {
        const [template] = select([definition({ shortName: ["redb-route-sql", "xml-sql"] })]);
        assert.equal(template.shortName, "redb-route-sql");
    });

    it("reads the parameters, not the computed symbols", () => {
        const [template] = select([definition()]);
        assert.deepEqual(template.parameters.map(p => [p.name, p.kind]), [["context", "text"], ["framework", "choice"]]);
        const framework = template.parameters[1];
        assert.equal(framework.defaultValue, "net10.0");
        assert.equal(framework.displayName, "Framework");
        assert.deepEqual(framework.choices.map(c => c.value), ["net8.0", "net9.0", "net10.0"]);
        assert.equal(framework.choices[2].description, ".NET 10");
        assert.equal(template.parameters[0].defaultValue, "");
    });

    it("maps bool, required and typed text parameters", () => {
        const [template] = select([definition({
            symbols: {
                pro: { type: "parameter", datatype: "bool", defaultValue: true },
                port: { type: "parameter", datatype: "int", isRequired: "true" },
            },
        })]);
        assert.deepEqual(template.parameters.map(p => [p.name, p.kind, p.datatype, p.defaultValue, p.isRequired]), [
            ["pro", "bool", "bool", "true", false],
            ["port", "text", "int", null, true],
        ]);
    });

    it("falls back to the short name when a template has no name", () => {
        const [template] = select([definition({ name: undefined, description: "" })]);
        assert.equal(template.name, "redb-route");
        assert.equal(template.description, null);
    });

    it("reads template.json with comments, keeping // inside strings", () => {
        const text = `{
  // the base template
  "shortName": "redb-route", /* block */
  "description": "See https://redbase.app // not a comment",
  "classifications": ["RouteXml"], "tags": { "type": "project" }
}`;
        assert.equal(JSON.parse(stripJsonComments(text)).description, "See https://redbase.app // not a comment");
        assert.equal(selectTemplates([text], "redb.Templates").length, 1);
    });
});

describe("templates — reading an installed package", () => {
    it("reads the definitions of a .nupkg", () => {
        const dir = fs.mkdtempSync(path.join(os.tmpdir(), "redb-templates-"));
        try {
            const nupkg = path.join(dir, "redb.Templates.4.4.3.nupkg");
            fs.writeFileSync(nupkg, buildZip([
                { name: "redb.Templates.nuspec", content: "<package/>" },
                { name: "content/redb-route/.template.config/template.json", content: definition() },
                { name: "content/redb-route/routes/main.route.xml", content: "<routes/>" },
                { name: "content/redb-app/.template.config/template.json",
                  content: definition({ shortName: "redb-app", classifications: ["Web"] }) },
            ]));
            const definitions = readTemplateDefinitions(nupkg);
            assert.equal(definitions.length, 2);
            assert.deepEqual(selectTemplates(definitions, "redb.Templates").map(t => t.shortName), ["redb-route", "redb-app"]);
        } finally {
            fs.rmSync(dir, { recursive: true, force: true });
        }
    });

    it("reads the definitions of a template folder installed from disk", () => {
        const dir = fs.mkdtempSync(path.join(os.tmpdir(), "redb-templates-"));
        try {
            const config = path.join(dir, "templates", "redb-route", ".template.config");
            fs.mkdirSync(config, { recursive: true });
            fs.writeFileSync(path.join(config, "template.json"), definition());
            const stale = path.join(dir, "templates", "redb-route", "bin", ".template.config");
            fs.mkdirSync(stale, { recursive: true });
            fs.writeFileSync(path.join(stale, "template.json"), definition({ shortName: "redb-route-stale" }));
            assert.deepEqual(selectTemplates(readTemplateDefinitions(dir), "redb.Templates").map(t => t.shortName), ["redb-route"]);
        } finally {
            fs.rmSync(dir, { recursive: true, force: true });
        }
    });
});
