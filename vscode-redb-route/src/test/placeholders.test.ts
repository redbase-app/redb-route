import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import {
    resolvePlaceholders, mergeContextLayer, moduleContextName, packageName, ConfigMap,
} from "../placeholders";

describe("placeholder resolution (§8, Р18 — display only)", () => {
    const config: ConfigMap = new Map([["demo.rabbit.producer", "rabbitmq://demo-out?durable=true"]]);

    it("substitutes a full-uri placeholder", () => {
        const r = resolvePlaceholders("{{demo.rabbit.producer}}", config);
        assert.equal(r.resolved, "rabbitmq://demo-out?durable=true");
        assert.equal(r.fromConfig, true);
    });

    it("uses the inline default when the key is missing", () => {
        const r = resolvePlaceholders("http:{{demo.http.listen:0.0.0.0:5088}}/api", new Map());
        assert.equal(r.resolved, "http:0.0.0.0:5088/api");
        assert.equal(r.fromConfig, true);
    });

    it("keeps an unresolvable placeholder as-is and reports it", () => {
        const r = resolvePlaceholders("kafka://{{topic}}", new Map());
        assert.equal(r.resolved, "kafka://{{topic}}");
        assert.deepEqual(r.missing, ["topic"]);
        assert.equal(r.fromConfig, false);
    });

    it("resolves a placeholder nested inside a config value", () => {
        const nested: ConfigMap = new Map([
            ["demo.sql.insert", "sql://INSERT?connection={{db.main.connection}}"],
            ["db.main.connection", "Host=x;Database=demo"],
        ]);
        const r = resolvePlaceholders("{{demo.sql.insert}}", nested);
        assert.equal(r.resolved, "sql://INSERT?connection=Host=x;Database=demo");
    });

    it("looks keys up case-insensitively, as the runtime's context properties are", () => {
        const r = resolvePlaceholders("{{Demo.Rabbit.Producer}}", config);
        assert.equal(r.resolved, "rabbitmq://demo-out?durable=true");
    });

    it("treats an empty value as none: the default applies, else the key is missing", () => {
        const empty: ConfigMap = new Map([["port", ""]]);
        assert.equal(resolvePlaceholders("{{port:8080}}", empty).resolved, "8080");
        assert.deepEqual(resolvePlaceholders("{{port}}", empty).missing, ["port"]);
    });
});

describe("config layers as the Tsak runtime reads them", () => {
    const resolve = (text: string, map: ConfigMap) => resolvePlaceholders(text, map).resolved;

    it("takes Contexts.default and the module's own context over it", () => {
        const map: ConfigMap = new Map();
        mergeContextLayer({
            Contexts: {
                default: { "hello.period": "1000", "db.main": "Host=shared" },
                orders: { "hello.period": "5000" },
                other: { "db.main": "Host=other" },
            },
        }, "orders", map);
        assert.equal(resolve("{{hello.period}}", map), "5000");
        assert.equal(resolve("{{db.main}}", map), "Host=shared");
    });

    it("does not read into nested objects: the runtime keeps them as one dictionary property", () => {
        const map: ConfigMap = new Map();
        mergeContextLayer({ Contexts: { orders: { hello: { period: "5000" } } } }, "orders", map);
        const r = resolvePlaceholders("timer://t?period={{hello.period}}", map);
        assert.deepEqual(r.missing, ["hello.period"]);
        assert.equal(resolve("{{hello.period:250}}", map), "250");
    });

    it("reads root keys when there is no Contexts section, nested objects still not flattened", () => {
        const map: ConfigMap = new Map();
        mergeContextLayer({ "a.b": 5, on: true, demo: { tag: "x" } }, "orders", map);
        assert.equal(map.get("a.b"), "5");
        assert.equal(map.get("on"), "True");
        assert.equal(map.has("demo.tag"), false);
        assert.equal(map.has("demo"), false);
    });

    it("matches the section and context names case-insensitively", () => {
        const map: ConfigMap = new Map();
        mergeContextLayer({ contexts: { DEFAULT: { a: "1" }, Orders: { b: "2" } } }, "orders", map);
        assert.equal(map.get("a"), "1");
        assert.equal(map.get("b"), "2");
    });

    it("takes only Contexts.default when the context is not known", () => {
        const map: ConfigMap = new Map();
        mergeContextLayer({ Contexts: { default: { a: "1" }, orders: { b: "2" } } }, null, map);
        assert.deepEqual([...map.keys()], ["a"]);
    });

    it("lets the context's object or null hide the shared value, as DeepMerge leaves no string there", () => {
        const map: ConfigMap = new Map();
        mergeContextLayer({ Contexts: { default: { a: "1", b: "2" }, orders: { a: { x: "y" }, b: null } } }, "orders", map);
        assert.equal(map.size, 0);
    });

    it("reads the module's own config by the same rule, over the context file", () => {
        const map: ConfigMap = new Map();
        mergeContextLayer({ Contexts: { orders: { "hello.period": "5000", "db.main": "Host=a" } } }, "orders", map);
        mergeContextLayer({ ContextName: "orders", Contexts: {
            default: { "db.main": "Host=b" },
            orders: { "hello.period": 7000 },
            other: { "hello.period": 1 },
        } }, "orders", map);
        assert.equal(map.get("hello.period"), "7000");
        assert.equal(map.get("db.main"), "Host=b");
        assert.equal(map.has("contexts"), false);
        assert.equal(map.has("contextname"), false);

        const flat: ConfigMap = new Map();
        mergeContextLayer({ ContextName: "orders", "hello.period": 7000 }, "orders", flat);
        assert.equal(flat.get("hello.period"), "7000");
        assert.equal(flat.get("contextname"), "orders");
    });
});

describe("the module's package and context names", () => {
    it("takes the package name from RoutePackageName, else the csproj's file name", () => {
        assert.equal(packageName("Orders.csproj", "<Project><PropertyGroup><RoutePackageName> orders </RoutePackageName>"), "orders");
        assert.equal(packageName("Orders.Module.csproj", "<Project/>"), "Orders.Module");
    });

    it("takes ContextName from the module config, else RouteContextName, else the package", () => {
        const csproj = "<RouteContextName>from-csproj</RouteContextName>";
        assert.equal(moduleContextName({ ContextName: "from-config" }, csproj, "pkg"), "from-config");
        assert.equal(moduleContextName({ contextname: " spaced " }, csproj, "pkg"), "spaced");
        assert.equal(moduleContextName(undefined, csproj, "pkg"), "from-csproj");
        assert.equal(moduleContextName({ ContextName: "" }, "<Project/>", "pkg"), "pkg");
    });
});
