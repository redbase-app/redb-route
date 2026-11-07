import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { parseXml, childElements, attr, textContent, elementAt, XmlParseError } from "../xmlmodel";

const SAMPLE = `<?xml version="1.0" encoding="utf-8"?>
<!-- header comment -->
<routes xmlns="urn:redb:route:1.0">
  <route id="a" description="first">
    <from uri="direct://in"/>
    <!-- step comment -->
    <log level="Debug">hello</log>
    <redbQuery type="T"><![CDATA[ a < b ]]></redbQuery>
    <acme:custom xmlns:acme="urn:acme" weird=">quoted>"/>
  </route>
</routes>
`;

describe("parseXml — the position-exact tree", () => {
    const doc = parseXml(SAMPLE);

    it("EVERY span reproduces its source verbatim — the byte-preservation foundation", () => {
        const walk = (nodes: readonly { span: { start: number; end: number } }[]): void => {
            for (const node of nodes) {
                assert.equal(
                    SAMPLE.slice(node.span.start, node.span.end).length,
                    node.span.end - node.span.start);
                if ("children" in node)
                    walk((node as { children: { span: { start: number; end: number } }[] }).children);
            }
        };
        walk(doc.prolog);

        const route = childElements(doc.root)[0];
        assert.equal(SAMPLE.slice(route.span.start, route.span.end).startsWith("<route id=\"a\""), true);
        assert.equal(SAMPLE.slice(route.span.start, route.span.end).endsWith("</route>"), true);
    });

    it("attribute spans point at the exact value", () => {
        const route = childElements(doc.root)[0];
        const id = attr(route, "id")!;
        assert.equal(SAMPLE.slice(id.valueSpan.start, id.valueSpan.end), "a");
        assert.equal(SAMPLE.slice(id.span.start, id.span.end), `id="a"`);
    });

    it("a '>' inside a quoted attribute does not end the tag", () => {
        const route = childElements(doc.root)[0];
        const custom = childElements(route)[3];
        assert.equal(custom.name, "acme:custom");
        assert.equal(custom.local, "custom");
        assert.equal(custom.prefix, "acme");
        assert.equal(attr(custom, "weird")!.value, ">quoted>");
        assert.equal(custom.selfClosing, true);
    });

    it("comments and CDATA are nodes with exact spans", () => {
        const route = childElements(doc.root)[0];
        const comment = route.children.find(c => c.kind === "comment")!;
        assert.equal((comment as { text: string }).text, " step comment ");
        const query = childElements(route)[2];
        assert.equal(textContent(query), " a < b ");
    });

    it("open/close tag spans split the element correctly", () => {
        const route = childElements(doc.root)[0];
        const log = childElements(route)[1];
        assert.equal(SAMPLE.slice(log.openTag.start, log.openTag.end), `<log level="Debug">`);
        assert.equal(SAMPLE.slice(log.closeTag!.start, log.closeTag!.end), "</log>");
        assert.equal(SAMPLE.slice(log.content!.start, log.content!.end), "hello");
    });

    it("elementAt finds the deepest node under the cursor", () => {
        const offset = SAMPLE.indexOf("level=");
        const found = elementAt(doc.root, offset)!;
        assert.equal(found.local, "log");
        assert.equal(elementAt(doc.root, 0), null, "the prolog is outside the root");
    });

    it("parent links go up to the root", () => {
        const route = childElements(doc.root)[0];
        const log = childElements(route)[1];
        assert.equal(log.parent, route);
        assert.equal(route.parent, doc.root);
        assert.equal(doc.root.parent, null);
    });

    it("mismatched closing tag fails with the offset", () => {
        assert.throws(
            () => parseXml("<a><b></a>"),
            (e: unknown) => e instanceof XmlParseError && e.offset === 6);
    });

    it("unclosed element names the element", () => {
        assert.throws(
            () => parseXml("<routes><route>"),
            (e: unknown) => e instanceof XmlParseError && /route/.test((e as Error).message));
    });
});
