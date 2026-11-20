import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { attr, childElements, decodeXmlEntities, parseXml, textContent, XmlElement } from "../xmlmodel";
import { applyReplacement, computeSetAttribute, computeSetTextContent, escapeAttributeValue } from "../textedit";
import { setUriOption } from "../endpointuri";
import { buildFileGraph, buildIndex, ElementInfo, LeafView } from "../graphmodel";

const ELEMENTS: ElementInfo[] = JSON.parse(fs.readFileSync(
    path.join(__dirname, "..", "..", "media", "redb-route-elements.json"), "utf8"));
const INDEX = buildIndex(ELEMENTS);

/** The innermost first element: routes > route > the step (> its first child). */
function deepest(root: XmlElement): XmlElement {
    let current = root;
    for (;;) {
        const next = childElements(current)[0];
        if (!next) return current;
        current = next;
    }
}

const SAMPLE = "header.a &gt; 1 &amp;&amp; header.b == &quot;x&quot;";
const filterDoc = (attribute: string): string =>
    `<routes xmlns="urn:redb:route:1.0">\n  <route id="r">\n    <filter ${attribute}/>\n  </route>\n</routes>\n`;
const exprOf = (text: string) => attr(deepest(parseXml(text).root), "expr")!;

describe("entities: the panel shows decoded values and writes them back escaped once", () => {
    it("decodes the five named entities and character references, leaves the rest as written", () => {
        assert.equal(decodeXmlEntities(SAMPLE), `header.a > 1 && header.b == "x"`);
        assert.equal(decodeXmlEntities("&lt;&gt;&amp;&quot;&apos;"), `<>&"'`);
        assert.equal(decodeXmlEntities("&#60;&#x3C;&#38;"), "<<&");
        // XML spells the hex form with a lower-case x only.
        assert.equal(decodeXmlEntities("&#X3c;"), "&#X3c;");
        assert.equal(decodeXmlEntities("&nbsp; & &unknown; &#xZZ;"), "&nbsp; & &unknown; &#xZZ;");
    });

    it("parses an attribute to its decoded value, keeping the raw text and the delimiter", () => {
        const expr = exprOf(filterDoc(`expr="${SAMPLE}"`));
        assert.equal(expr.value, `header.a > 1 && header.b == "x"`);
        assert.equal(expr.raw, SAMPLE);
        assert.equal(expr.quote, '"');
    });

    it("round trip of the sample with an edit: one escape, not two", () => {
        const text = filterDoc(`expr="${SAMPLE}"`);
        const shown = exprOf(text).value;
        const after = applyReplacement(text, computeSetAttribute(text, [0, 0], "expr", shown.replace("1", "2"))!);
        assert.match(after, /expr="header\.a > 2 &amp;&amp; header\.b == &quot;x&quot;"/);
        assert.equal(exprOf(after).value, `header.a > 2 && header.b == "x"`);
    });

    it("round trip of the sample without an edit: no edit at all", () => {
        const text = filterDoc(`expr="${SAMPLE}"`);
        assert.equal(computeSetAttribute(text, [0, 0], "expr", exprOf(text).value), null);
    });

    it("an unchanged value keeps the file's own spelling (&#60;, &gt;)", () => {
        assert.equal(computeSetAttribute(filterDoc(`expr="a &#60; b &gt; c"`), [0, 0], "expr", "a < b > c"), null);
    });

    it("a value with each of & < > \" ' in a double-quoted attribute", () => {
        const text = filterDoc(`expr="old"`);
        const value = `a & b < c > d "q" 'p'`;
        const after = applyReplacement(text, computeSetAttribute(text, [0, 0], "expr", value)!);
        assert.match(after, /expr="a &amp; b &lt; c > d &quot;q&quot; 'p'"/);
        assert.equal(exprOf(after).value, value);
    });

    it("a value with each of & < > \" ' in a single-quoted attribute keeps the author's quotes", () => {
        const text = filterDoc(`expr='a == "x"'`);
        assert.equal(exprOf(text).quote, "'");
        const value = `a & b < c > d "q" 'p'`;
        const after = applyReplacement(text, computeSetAttribute(text, [0, 0], "expr", value)!);
        assert.match(after, /expr='a &amp; b &lt; c > d "q" &apos;p&apos;'/);
        assert.equal(exprOf(after).value, value);
    });

    it("escapes by the delimiter only", () => {
        assert.equal(escapeAttributeValue(`"a" & 'b' <`), `&quot;a&quot; &amp; 'b' &lt;`);
        assert.equal(escapeAttributeValue(`"a" & 'b' <`, "'"), `"a" &amp; &apos;b&apos; &lt;`);
    });
});

describe("entities: text content (log message and the other text rows)", () => {
    const logDoc = (message: string): string =>
        `<routes xmlns="urn:redb:route:1.0">\n  <route id="r">\n    <log>\n      <message>${message}</message>\n    </log>\n  </route>\n</routes>\n`;

    it("reads decoded, writes escaped once, and an unchanged value is no edit", () => {
        const text = logDoc("a &lt; b &amp;&amp; c");
        assert.equal(textContent(deepest(parseXml(text).root)), "a < b && c");
        assert.equal(computeSetTextContent(text, [0, 0, 0], "a < b && c"), null);
        const after = applyReplacement(text, computeSetTextContent(text, [0, 0, 0], "a < b && d")!);
        assert.match(after, /<message>a &lt; b &amp;&amp; d<\/message>/);
    });

    it("text kept in CDATA stays in CDATA, a ]]> inside is split", () => {
        const text = logDoc("<![CDATA[a < b]]>");
        assert.equal(computeSetTextContent(text, [0, 0, 0], "a < b"), null);
        const after = applyReplacement(text, computeSetTextContent(text, [0, 0, 0], "x < y ]]> z")!);
        assert.ok(after.includes("<message><![CDATA[x < y ]]]]><![CDATA[> z]]></message>"));
        assert.equal(textContent(deepest(parseXml(after).root)), "x < y ]]> z");
    });
});

describe("entities: the endpoint uri, where & separates the query parameters", () => {
    it("decodes &amp; before splitting and writes one parameter back escaped once", () => {
        const text = `<routes xmlns="urn:redb:route:1.0">\n  <route id="r">\n    <to uri="sql:select 1?outputType=SelectOne&amp;dataSource=#db"/>\n  </route>\n</routes>\n`;
        const uri = attr(deepest(parseXml(text).root), "uri")!.value;
        assert.equal(uri, "sql:select 1?outputType=SelectOne&dataSource=#db");
        const rebuilt = setUriOption(uri, "dataSource", "#main")!;
        const after = applyReplacement(text, computeSetAttribute(text, [0, 0], "uri", rebuilt)!);
        assert.ok(after.includes(`uri="sql:select 1?outputType=SelectOne&amp;dataSource=#main"`));
    });
});

describe("entities: graph labels and the grouped setters panel see decoded values", () => {
    const graph = buildFileGraph(parseXml(`<routes xmlns="urn:redb:route:1.0"><route id="r">
      <from uri="direct://in"/>
      <setHeader name="big" expr="header.n &gt; 10 &amp;&amp; header.ok"/>
      <setHeader name="who" value="&quot;Tom &amp; Jerry&quot;"/>
      <setHeaders>
        <header name="lt" expr="header.n &lt; 5"/>
      </setHeaders>
    </route></routes>`).root, INDEX);
    const steps = graph.routes[0].steps as LeafView[];

    it("the assignments a joined run's panel edits are decoded", () => {
        const assigns = steps.flatMap(s => s.assign ? [s.assign] : []);
        assert.deepEqual(assigns.map(a => a.text), ["header.n > 10 && header.ok", `"Tom & Jerry"`]);
    });

    it("the setter rows tooltip is decoded", () => {
        const rows = steps.find(s => s.type === "setHeaders")!;
        assert.equal(rows.tooltip, "setHeaders\nlt = header.n < 5");
    });
});
