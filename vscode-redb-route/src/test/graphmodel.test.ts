import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { parseXml } from "../xmlmodel";
import { categoryOf } from "../category";
import {
    buildFileGraph, buildIndex, endpointLabel, redactUri, trimLabel,
    ElementInfo, BranchingView, ScopeView, LeafView, FileGraph,
} from "../graphmodel";

const ELEMENTS: ElementInfo[] = JSON.parse(fs.readFileSync(
    path.join(__dirname, "..", "..", "media", "redb-route-elements.json"), "utf8"));
const INDEX = buildIndex(ELEMENTS);

function graphOf(xml: string): FileGraph {
    return buildFileGraph(parseXml(xml).root, INDEX);
}

describe("category — mechanical completeness over the generated list", () => {
    it("EVERY registry element has a category (drift fails the build)", () => {
        const missing = ELEMENTS
            .filter(e => e.kind === "Step" || e.kind === "Scope" || e.kind === "Branching")
            .filter(e => !["to", "toD", "wireTap", "enrich", "pollEnrich"].includes(e.name))
            .filter(e => categoryOf(e.name) === null)
            .map(e => e.name);
        assert.deepEqual(missing, []);
    });
});

describe("labels (§5)", () => {
    it("endpoint label is scheme + last path segment", () => {
        assert.equal(endpointLabel("kafka://orders-vip?brokers=x"), "kafka orders-vip");
        assert.equal(endpointLabel("direct://orders"), "direct orders");
        assert.equal(endpointLabel("https://api.example.com/v1/orders?method=POST"), "https orders");
    });

    it("secrets never reach a label or a tooltip", () => {
        assert.equal(
            redactUri("amqp://admin:hunter2@broker:5672/vh?password=p%40ss&heartbeat=30"),
            "amqp://admin:***@broker:5672/vh?password=***&heartbeat=30");
        assert.equal(
            redactUri("sql://orders?connection=Server=x;AccessKey=abc&timeout=5"),
            "sql://orders?connection=Server=x;AccessKey=***&timeout=5",
            "a secret-looking key is masked even nested inside a connection string — over-redaction is the safe direction");
        assert.equal(redactUri("kafka://t?sharedAccessKeyName=k&sharedAccessKey=s3cr3t"),
            "kafka://t?sharedAccessKeyName=***&sharedAccessKey=***");
    });

    it("labels trim with an ellipsis", () => {
        assert.equal(trimLabel("short"), "short");
        assert.equal(trimLabel("a-very-long-endpoint-name-indeed"), "a-very-long-endpoint-…");
    });

    it("a name-carrying step reads as name = value (§3.2)", () => {
        const graph = graphOf(`<routes xmlns="urn:redb:route:1.0"><route id="r">
          <from uri="direct://in"/>
          <setHeader name="priority" expr="'high'"/>
          <removeHeader name="x-internal"/>
        </route></routes>`);
        const set = graph.routes[0].steps[0] as LeafView;
        assert.equal(set.label, "priority = 'high'");
        const remove = graph.routes[0].steps[1] as LeafView;
        assert.equal(remove.label, "x-internal");
    });
});

describe("the render tree (§3)", () => {
    const graph = graphOf(`<routes xmlns="urn:redb:route:1.0">
      <onException exceptions="System.Exception" maximumRedeliveries="2">
        <log level="Error">boom</log>
      </onException>
      <route id="main" description="the pipeline">
        <from uri="kafka://orders?groupId=g"/>
        <setHeader name="k" expr="\${header.a}"/>
        <filter expr="header.ok == true">
          <to uri="direct://inner"/>
        </filter>
        <choice>
          <when expr="header.type == 'order'"><to uri="kafka://out"/></when>
          <otherwise><to uri="direct://dlq"/></otherwise>
        </choice>
        <tryCatch>
          <to uri="https://api/x"/>
          <catch exception="System.TimeoutException, mscorlib"><log>t</log></catch>
          <finally><log>f</log></finally>
        </tryCatch>
        <split expr="body">
          <to uri="bean:#handler?method=go"/>
        </split>
        <acme:custom xmlns:acme="urn:acme"/>
      </route>
    </routes>`);
    const route = graph.routes[0];

    it("file strip carries the container onException", () => {
        assert.equal(graph.strips.length, 1);
        assert.equal(graph.strips[0].type, "onException");
        assert.equal(graph.strips[0].category, "errors");
    });

    it("from is the source with an endpoint label", () => {
        assert.equal(route.from!.category, "source");
        assert.equal(route.from!.label, "kafka orders");
    });

    it("leaf, scope, branching, unknown all come out", () => {
        const kinds = route.steps.map(s => s.kind);
        assert.deepEqual(kinds, ["leaf", "scope", "branching", "branching", "scope", "unknown"]);
    });

    it("filter is a bracket with its condition on the schema (§3.3, principle 3)", () => {
        const filter = route.steps[1] as ScopeView;
        assert.equal(filter.type, "filter");
        assert.equal(filter.label.includes("header.ok"), true);
        assert.equal((filter.steps[0] as LeafView).category, "sendInternal");
    });

    it("choice branches carry when-conditions and otherwise (§3.4)", () => {
        const choice = route.steps[2] as BranchingView;
        assert.equal(choice.branches.length, 2);
        assert.equal(choice.branches[0].label.startsWith("when "), true);
        assert.equal(choice.branches[1].label, "otherwise");
    });

    it("tryCatch: body branch plus warn-marked catch/finally (§3.6)", () => {
        const tc = route.steps[3] as BranchingView;
        assert.deepEqual(tc.branches.map(b => b.label), ["try", "catch TimeoutException", "finally"]);
        assert.deepEqual(tc.branches.map(b => b.warn), [false, true, true]);
        assert.equal(tc.branches[0].steps.length, 1, "the try body is the element's own steps");
    });

    it("split is a repeating bracket; bean target is user code (§3.5, §3.10)", () => {
        const split = route.steps[4] as ScopeView;
        assert.equal(split.repeats, true);
        assert.equal((split.steps[0] as LeafView).category, "userCode");
    });

    it("the unknown node keeps its raw name and the span (Р10)", () => {
        const unknown = route.steps[5];
        assert.equal(unknown.kind, "unknown");
        assert.equal(unknown.type, "acme:custom");
    });
});

describe("special renders", () => {
    it("tryCatch with the EXPLICIT <try> wrapper (the format's own shape)", () => {
        const graph = graphOf(`<routes xmlns="urn:redb:route:1.0"><route id="r">
          <from uri="direct://in"/>
          <tryCatch>
            <try><to uri="https://api/x"/><log>ok</log></try>
            <catch exception="System.Exception"><log>e</log></catch>
          </tryCatch>
        </route></routes>`);
        const tc = graph.routes[0].steps[0] as BranchingView;
        assert.deepEqual(tc.branches.map(b => b.label), ["try", "catch Exception"]);
        assert.equal(tc.branches[0].steps.length, 2, "the <try> wrapper's children are the body");
        assert.equal(tc.branches[0].steps.every(s => s.kind === "leaf"), true, "no unknown <try> node");
    });

    it("top-level bean declarations are a registry row, not unknown nodes", () => {
        const graph = graphOf(`<routes xmlns="urn:redb:route:1.0">
          <bean name="stamps" type="Acme.DemoStamps, Acme.Demo"/>
          <route id="r"><from uri="direct://in"/></route>
        </routes>`);
        assert.equal(graph.beans.length, 1);
        assert.equal(graph.beans[0].name, "stamps");
        assert.equal(graph.others.length, 0);
    });

    it("rich log is ONE node with a summary (§3.8)", () => {
        const graph = graphOf(`<routes xmlns="urn:redb:route:1.0"><route id="r">
          <from uri="direct://in"/>
          <log level="Info">
            <message>a</message><message>b</message>
            <header name="h"/><property name="p"/>
          </log>
        </route></routes>`);
        const log = graph.routes[0].steps[0] as LeafView;
        assert.equal(log.kind, "leaf");
        assert.equal(log.tooltip, "log: 2 message(s), 1 header(s), 1 propert(ies)");
    });

    it("multicast is unconditioned branches (§3.4)", () => {
        const graph = graphOf(`<routes xmlns="urn:redb:route:1.0"><route id="r">
          <from uri="direct://in"/>
          <multicast parallel="true">
            <to uri="direct://a"/>
            <to uri="direct://b"/>
          </multicast>
        </route></routes>`);
        const multicast = graph.routes[0].steps[0] as BranchingView;
        assert.equal(multicast.kind, "branching");
        assert.equal(multicast.branches.length, 2);
    });

    it("a secret in a to-uri is redacted in both label and tooltip", () => {
        const graph = graphOf(`<routes xmlns="urn:redb:route:1.0"><route id="r">
          <from uri="direct://in"/>
          <to uri="amqp://admin:hunter2@broker/q?password=zzz"/>
        </route></routes>`);
        const to = graph.routes[0].steps[0] as LeafView;
        assert.equal(to.tooltip.includes("hunter2"), false);
        assert.equal(to.tooltip.includes("zzz"), false);
        assert.equal(to.label.includes("hunter2"), false);
    });
});
