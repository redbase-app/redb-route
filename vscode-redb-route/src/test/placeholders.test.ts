import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { resolvePlaceholders, flattenConfig, ConfigMap } from "../placeholders";

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

    it("flattens nested and dotted configs the same way", () => {
        const map: ConfigMap = new Map();
        flattenConfig({ demo: { tag: "x" }, "a.b": 5, on: true }, map);
        assert.equal(map.get("demo.tag"), "x");
        assert.equal(map.get("a.b"), "5");
        assert.equal(map.get("on"), "true");
    });
});
