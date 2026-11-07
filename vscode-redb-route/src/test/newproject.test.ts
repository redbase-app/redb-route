import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import {
    displayCommand, dotnetNewArguments, formErrors, markMissingFrameworks, pagePackage, pageState, parameterPrompt, templateIcon,
    parameterValueError, projectNameError,
} from "../newproject";
import { RedbTemplate, TemplateParameter } from "../templates";

function parameter(patch: Partial<TemplateParameter> & { name: string }): TemplateParameter {
    return {
        displayName: null, description: null, kind: "text", datatype: "string",
        defaultValue: null, isRequired: false, choices: [], ...patch,
    };
}

const FRAMEWORK = parameter({
    name: "framework", displayName: "Framework", description: "Target framework", kind: "choice",
    datatype: "choice", defaultValue: "net10.0",
    choices: [
        { value: "net8.0", description: ".NET 8" },
        { value: "net9.0", description: null },
        { value: "net10.0", description: ".NET 10" },
    ],
});
const CONTEXT = parameter({ name: "context", displayName: "Context", description: "Route context name", defaultValue: "" });
const DB = parameter({
    name: "db", kind: "choice", datatype: "choice", defaultValue: "sqlite",
    choices: [{ value: "sqlite", description: null }, { value: "postgres", description: null }],
});
const PRO = parameter({ name: "pro", kind: "bool", datatype: "bool", defaultValue: "true" });

describe("newproject — the project name", () => {
    it("accepts names dotnet new and the file system take", () => {
        for (const name of ["Orders", "orders.intake", "Orders_Intake-2", "_tmp"])
            assert.equal(projectNameError(name), null, name);
    });

    it("refuses empty, spaced, odd and reserved names", () => {
        for (const name of ["", "  ", "my module", "1orders", "orders.", "a/b", "CON", "lpt1.route"])
            assert.notEqual(projectNameError(name), null, JSON.stringify(name));
    });
});

describe("newproject — asking for a parameter", () => {
    it("asks a choice as a pick list with the default picked", () => {
        const prompt = parameterPrompt(FRAMEWORK);
        assert.equal(prompt.kind, "pick");
        if (prompt.kind !== "pick") return;
        assert.equal(prompt.title, "Framework");
        assert.equal(prompt.placeholder, "Target framework");
        assert.deepEqual(prompt.items.filter(i => i.picked).map(i => i.value), ["net10.0"]);
        assert.equal(prompt.items[0].description, ".NET 8");
        assert.equal(prompt.items[1].description, undefined);
    });

    it("picks the first choice when a choice has no default", () => {
        const prompt = parameterPrompt({ ...DB, defaultValue: null });
        assert.equal(prompt.kind === "pick" && prompt.items[0].picked, true);
    });

    it("asks a bool as yes or no", () => {
        const prompt = parameterPrompt(PRO);
        assert.equal(prompt.kind, "pick");
        if (prompt.kind !== "pick") return;
        assert.deepEqual(prompt.items.map(i => [i.label, i.value, i.picked]), [["Yes", "true", true], ["No", "false", false]]);
    });

    it("asks text in an input box, telling that an empty box keeps the template's default", () => {
        const prompt = parameterPrompt(CONTEXT);
        assert.equal(prompt.kind, "input");
        if (prompt.kind !== "input") return;
        assert.equal(prompt.value, "");
        assert.match(prompt.placeholder, /leave empty for the template's default/);
        const named = parameterPrompt(parameter({ name: "port", defaultValue: "8080" }));
        assert.equal(named.kind === "input" && named.value, "8080");
        assert.equal(named.title, "port");
    });

    it("marks frameworks without an SDK and leaves the rest", () => {
        const prompt = parameterPrompt(FRAMEWORK);
        if (prompt.kind !== "pick") return assert.fail();
        const marked = markMissingFrameworks(prompt.items, [8, 9]);
        assert.deepEqual(marked.map(i => i.description), [".NET 8", undefined, ".NET 10, no .NET SDK for it on this machine"]);
        assert.deepEqual(markMissingFrameworks(prompt.items, [10]).map(i => i.description), [".NET 8", undefined, ".NET 10"]);
        const odd = [{ label: "netstandard2.0", value: "netstandard2.0", picked: false }];
        assert.deepEqual(markMissingFrameworks(odd, []), odd);
    });

    it("checks required and numeric text values", () => {
        assert.equal(parameterValueError(CONTEXT, ""), null);
        assert.match(parameterValueError({ ...CONTEXT, isRequired: true }, "")!, /Context is required/);
        const port = parameter({ name: "port", datatype: "int" });
        assert.equal(parameterValueError(port, "8080"), null);
        assert.equal(parameterValueError(port, "80a"), "Enter a whole number.");
    });
});

describe("newproject — the dotnet new command", () => {
    const base = {
        shortName: "redb-route-sql", name: "Orders", outputDirectory: "C:\\My Projects\\Orders",
        parameters: [CONTEXT, FRAMEWORK, DB, PRO],
    };

    it("passes only what differs from the template's defaults", () => {
        const args = dotnetNewArguments({ ...base, values: new Map([
            ["context", ""], ["framework", "net9.0"], ["db", "sqlite"], ["pro", "false"],
        ]) });
        assert.deepEqual(args, ["new", "redb-route-sql", "-n", "Orders", "-o", "C:\\My Projects\\Orders",
            "--framework", "net9.0", "--pro", "false"]);
    });

    it("passes a typed context and keeps the path with spaces as one argument", () => {
        const args = dotnetNewArguments({ ...base, values: new Map([["context", "orders-ctx"]]) });
        assert.deepEqual(args.slice(4), ["-o", "C:\\My Projects\\Orders", "--context", "orders-ctx"]);
    });

    it("passes a required value even when it equals the default", () => {
        const required = parameter({ name: "region", defaultValue: "eu", isRequired: true });
        const args = dotnetNewArguments({ ...base, parameters: [required], values: new Map([["region", "eu"]]) });
        assert.deepEqual(args.slice(6), ["--region", "eu"]);
    });

    it("shows the command quoted where a shell would need it", () => {
        assert.equal(displayCommand("dotnet", ["new", "redb-route", "-o", "C:\\My Projects\\x"]),
            "dotnet new redb-route -o \"C:\\My Projects\\x\"");
    });
});

function redbTemplate(patch: Partial<RedbTemplate> & { shortName: string }): RedbTemplate {
    return {
        name: patch.shortName, description: null, identity: null, type: "project", group: "apps",
        classifications: [], packageId: "redb.Templates", parameters: [], ...patch,
    };
}

describe("newproject — the page", () => {
    const route = redbTemplate({ shortName: "redb-route", name: "redb.Route XML module", group: "route-xml",
        parameters: [CONTEXT, FRAMEWORK] });
    const consoleApp = redbTemplate({ shortName: "redb", name: "RedBase Console Application", parameters: [DB, PRO] });

    it("describes the packages as installed, outdated or missing", () => {
        const installed = { packageId: "redb.Templates", version: "4.4.2", mountPoint: "x" };
        assert.deepEqual(pagePackage("redb.Templates", { kind: "outdated", installed, latest: "4.4.3" }),
            { packageId: "redb.Templates", kind: "outdated", installed: "4.4.2", latest: "4.4.3" });
        assert.deepEqual(pagePackage("redb.Tsak.Templates", { kind: "missing", latest: null }),
            { packageId: "redb.Tsak.Templates", kind: "missing", installed: null, latest: null });
        assert.deepEqual(pagePackage("redb.Templates", { kind: "current", installed }),
            { packageId: "redb.Templates", kind: "current", installed: "4.4.2", latest: null });
    });

    it("turns parameters into form fields and preselects redb-route", () => {
        const state = pageState([8, 9], [], [consoleApp, route], "C:\work");
        assert.equal(state.selected, "redb-route");
        assert.deepEqual(state.groups.map(g => g.group), ["route-xml", "apps"]);
        const fields = state.templates[1].fields;
        assert.deepEqual(fields.map(f => [f.name, f.kind, f.value]), [["context", "text", ""], ["framework", "select", "net10.0"]]);
        assert.match(fields[1].options[2].description!, /no \.NET SDK/);
        const pro = state.templates[0].fields[1];
        assert.deepEqual(pro.options.map(o => [o.label, o.value]), [["Yes", "true"], ["No", "false"]]);
        assert.equal(pro.value, "true");
    });

    it("preselects the first template without redb-route, and nothing without templates", () => {
        assert.equal(pageState([10], [], [consoleApp], "/w").selected, "redb");
        const empty = pageState(null, [], [], "/w");
        assert.equal(empty.selected, null);
        assert.deepEqual(empty.groups, []);
        assert.equal(empty.sdks, null);
    });

    it("checks the form field by field", () => {
        const ok = { template: "redb", name: "Orders", folder: "C:\work", values: { db: "postgres", pro: "false" } };
        assert.deepEqual(formErrors(ok, consoleApp, false), {});
        assert.deepEqual(Object.keys(formErrors({ ...ok, name: "my app", folder: " " }, consoleApp, false)).sort(), ["folder", "name"]);
        assert.match(formErrors(ok, consoleApp, true).folder, /already exists/);
        assert.deepEqual(Object.keys(formErrors({ ...ok, values: { db: "oracle", pro: "maybe" } }, consoleApp, false)).sort(), ["db", "pro"]);
        assert.deepEqual(formErrors(ok, undefined, false), { template: "Choose a template." });
    });
});

describe("newproject — the card glyphs", () => {
    const icon = (shortName: string, group: RedbTemplate["group"], classifications: string[] = []) =>
        templateIcon({ shortName, group, classifications });
    const XML = ["Integration", "XML", "Tsak", "RedBase", "RouteXml"];

    it("reads the scenario from the short name of an XML module", () => {
        assert.deepEqual([
            "redb-route-as4", "redb-route-eip", "redb-route-kafka", "redb-route-rabbitmq", "redb-route-rest",
            "redb-route-scatter-gather", "redb-route-sql-batch", "redb-route-sql", "redb-route-beans", "redb-route-validator-xslt",
        ].map(n => icon(n, "route-xml", XML)), [
            "envelope", "split", "stream", "queue", "api", "fan", "database", "database", "cube", "check",
        ]);
    });

    it("gives the basic module the route glyph, not one from its classifications", () => {
        assert.equal(icon("redb-route", "route-xml", XML), "route");
    });

    it("reads applications and hosts by name, then classifications, then group", () => {
        assert.equal(icon("tsak-worker", "tsak", ["Worker", "Service", "redb.Tsak"]), "server");
        assert.equal(icon("redb-identity-app", "apps", ["Web"]), "shield");
        assert.equal(icon("redb-bff", "apps"), "browser");
        assert.equal(icon("redb-chat", "apps"), "chat");
        assert.equal(icon("redb", "apps", ["Console", "Database", "RedBase"]), "terminal");
        assert.equal(icon("redb-worker", "apps", ["Worker", "Integration"]), "server");
        assert.equal(icon("something-new", "apps", ["RedBase"]), "document");
        assert.equal(icon("tsak-something", "tsak", ["redb.Tsak"]), "server");
    });
});
