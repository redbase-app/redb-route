import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import {
    computeSetAttribute, computeSetTextContent, computeRemoveElement, computeInsertChild,
    computeInsertAt, computeMoveElement, blockSpan,
    applyReplacement, escapeAttributeValue, resolveByPath, TextReplacement,
} from "../textedit";
import { parseXml } from "../xmlmodel";

const DOC = `<?xml version="1.0"?>
<routes xmlns="urn:redb:route:1.0">
  <!-- the pipeline -->
  <route id="main" description="the pipeline">
    <from uri="direct://in"/>
    <setHeader name="k" expr="\${header.a}"/>
    <filter expr="header.ok == true">
      <to uri="kafka://out"/>
    </filter>
  </route>
</routes>
`;

/** The §8.3 contract: exactly ONE line differs. */
function assertOneLineDiff(before: string, after: string): void {
    const beforeLines = before.split("\n");
    const afterLines = after.split("\n");
    assert.equal(afterLines.length, beforeLines.length, "no lines added or removed");
    const changed = beforeLines.filter((line, i) => line !== afterLines[i]);
    assert.equal(changed.length, 1, "exactly one line changed");
}

describe("computeSetAttribute — the one-line surgical diff (§8.3)", () => {
    it("replaces an existing value inside its quotes", () => {
        const edit = computeSetAttribute(DOC, [0, 1], "name", "priority")!;
        const after = applyReplacement(DOC, edit);

        assertOneLineDiff(DOC, after);
        assert.match(after, /<setHeader name="priority" expr=/);
    });

    it("adds a missing attribute before the tag closer", () => {
        const edit = computeSetAttribute(DOC, [0, 2], "id", "guard")!;
        const after = applyReplacement(DOC, edit);

        assertOneLineDiff(DOC, after);
        assert.match(after, /<filter expr="header.ok == true" id="guard">/);
    });

    it("adds onto a self-closing tag before the />", () => {
        const edit = computeSetAttribute(DOC, [0, 0], "id", "src")!;
        const after = applyReplacement(DOC, edit);

        assertOneLineDiff(DOC, after);
        assert.match(after, /<from uri="direct:\/\/in" id="src"\/>/);
    });

    it("removes an attribute together with its leading space", () => {
        const edit = computeSetAttribute(DOC, [0], "description", null)!;
        const after = applyReplacement(DOC, edit);

        assertOneLineDiff(DOC, after);
        assert.match(after, /<route id="main">/);
    });

    it("escapes XML metacharacters in the new value", () => {
        assert.equal(escapeAttributeValue(`a < b & c == "x"`), "a &lt; b &amp; c == &quot;x&quot;");
        const edit = computeSetAttribute(DOC, [0, 2], "expr", `total < 100 & ok`)!;
        const after = applyReplacement(DOC, edit);

        assertOneLineDiff(DOC, after);
        assert.match(after, /expr="total &lt; 100 &amp; ok"/);
        // and the edited document still parses
        parseXml(after);
    });

    it("returns null when nothing changes", () => {
        assert.equal(computeSetAttribute(DOC, [0], "id", "main"), null);
        assert.equal(computeSetAttribute(DOC, [0], "nonexistent", null), null);
    });

    it("COMMENTS and everything else stay byte-identical outside the one line", () => {
        const edit = computeSetAttribute(DOC, [0, 1], "expr", "${header.b}")!;
        const after = applyReplacement(DOC, edit);

        assert.equal(after.includes("<!-- the pipeline -->"), true);
        assertOneLineDiff(DOC, after);
    });

    it("resolveByPath addresses nested elements by child-element indices", () => {
        const root = parseXml(DOC).root;
        assert.equal(resolveByPath(root, [0])!.local, "route");
        assert.equal(resolveByPath(root, [0, 2, 0])!.local, "to");
        assert.equal(resolveByPath(root, [0, 9]), null);
    });
});

const RICH = `<routes xmlns="urn:redb:route:1.0">
  <route id="r">
    <from uri="direct://in"/>
    <log level="Info">
      <message>hello</message>
      <header name="traceId"/>
    </log>
  </route>
</routes>
`;

describe("rich-content surgery (§3.8 panel editing)", () => {
    it("replaces a message's text", () => {
        const edit = computeSetTextContent(RICH, [0, 1, 0], "goodbye")!;
        const after = applyReplacement(RICH, edit);

        assert.match(after, /<message>goodbye<\/message>/);
        assert.equal(after.split("\n").length, RICH.split("\n").length);
    });

    it("escapes text content and keeps the document parseable", () => {
        const after = applyReplacement(RICH, computeSetTextContent(RICH, [0, 1, 0], "a < b & c")!);
        assert.match(after, /<message>a &lt; b &amp; c<\/message>/);
        parseXml(after);
    });

    it("grows a body on a self-closing element", () => {
        const doc = RICH.replace("<message>hello</message>", "<message/>");
        const after = applyReplacement(doc, computeSetTextContent(doc, [0, 1, 0], "hi")!);
        assert.match(after, /<message>hi<\/message>/);
    });

    it("removes a child together with its whole line", () => {
        const after = applyReplacement(RICH, computeRemoveElement(RICH, [0, 1, 1]));

        assert.equal(after.includes("header"), false);
        assert.equal(after.split("\n").length, RICH.split("\n").length - 1, "the line is gone entirely");
        parseXml(after);
    });

    it("inserts a child on its own line, indented like the siblings", () => {
        const after = applyReplacement(RICH,
            computeInsertChild(RICH, [0, 1], `<property name=""/>`));

        assert.match(after, /      <header name="traceId"\/>\n      <property name=""\/>\n    <\/log>/);
        parseXml(after);
    });

    it("opens a body when the parent closes inline", () => {
        const doc = `<routes xmlns="urn:redb:route:1.0">\n  <route id="r">\n    <log>text</log>\n  </route>\n</routes>`;
        const after = applyReplacement(doc, computeInsertChild(doc, [0, 0], `<message></message>`));
        assert.match(after, /<log>text\n      <message><\/message>\n    <\/log>/);
        parseXml(after);
    });

    it("expands a self-closing parent into a body", () => {
        const doc = `<routes xmlns="urn:redb:route:1.0">\n  <route id="r">\n    <log level="Info"/>\n  </route>\n</routes>`;
        const after = applyReplacement(doc, computeInsertChild(doc, [0, 0], `<message></message>`));
        assert.match(after, /<log level="Info">\n      <message><\/message>\n    <\/log>/);
        parseXml(after);
    });

    it("resolveByPath addresses nested elements by child-element indices (dup guard)", () => {
        const root = parseXml(DOC).root;
        assert.equal(resolveByPath(root, [0])!.local, "route");
        assert.equal(resolveByPath(root, [0, 2, 0])!.local, "to");
        assert.equal(resolveByPath(root, [0, 9]), null);
    });
});

const MOVABLE = `<routes xmlns="urn:redb:route:1.0">
  <route id="r">
    <from uri="direct://in"/>
    <!-- why the log is here -->
    <!-- second line of the why -->
    <log level="Info">step one</log>
    <setHeader name="k" expr="v"/>
    <filter expr="header.ok == true">
      <to uri="direct://inner"/>
    </filter>
  </route>
</routes>
`;

function applyAll(text: string, replacements: TextReplacement[]): string {
    let out = text;
    for (const r of [...replacements].sort((a, b) => b.start - a.start))
        out = applyReplacement(out, r);
    return out;
}

describe("step operations (такт 3, слой A)", () => {
    it("blockSpan takes the contiguous comments above; a blank line breaks the chain", () => {
        const model = parseXml(MOVABLE);
        const log = resolveByPath(model.root, [0, 1])!;
        const block = MOVABLE.slice(blockSpan(model, log).start, blockSpan(model, log).end);
        assert.equal(block.includes("why the log is here"), true);
        assert.equal(block.includes("second line"), true);
        assert.equal(block.includes("from uri"), false);

        const gapped = MOVABLE.replace("    <!-- why the log is here -->", "    <!-- why the log is here -->\n");
        const gappedModel = parseXml(gapped);
        const gappedLog = resolveByPath(gappedModel.root, [0, 1])!;
        const gappedBlock = gapped.slice(blockSpan(gappedModel, gappedLog).start, blockSpan(gappedModel, gappedLog).end);
        assert.equal(gappedBlock.includes("second line"), true);
        assert.equal(gappedBlock.includes("why the log is here"), false,
            "the blank line detached the first comment");
    });

    it("insertAt puts the fragment before the indexed child, sibling indentation", () => {
        const after = applyReplacement(MOVABLE, computeInsertAt(MOVABLE, [0], 1, `<delay period="00:00:01"/>`));
        assert.match(after, /from uri="direct:\/\/in"\/>\n    <delay period="00:00:01"\/>\n    <!-- why/);
        parseXml(after);
    });

    it("insertAt appends when the index is past the end", () => {
        const after = applyReplacement(MOVABLE, computeInsertAt(MOVABLE, [0], 99, `<stop/>`));
        assert.match(after, /<\/filter>\n    <stop\/>\n  <\/route>/);
        parseXml(after);
    });

    it("MOVE carries the block WITH its comments and re-indents into the scope", () => {
        const edits = computeMoveElement(MOVABLE, [0, 1], [0, 3], 0)!;
        const after = applyAll(MOVABLE, edits);

        assert.match(after, /<filter expr="header.ok == true">\n      <!-- why the log is here -->\n      <!-- second line of the why -->\n      <log level="Info">step one<\/log>\n      <to uri="direct:\/\/inner"\/>/,
            "comments moved with the step, everything re-indented to the scope's depth");
        assert.equal(after.split("why the log is here").length, 2, "no duplicate");
        parseXml(after);
    });

    it("MOVE out of a scope re-indents back", () => {
        const edits = computeMoveElement(MOVABLE, [0, 3, 0], [0], 1)!;
        const after = applyAll(MOVABLE, edits);

        assert.match(after, /from uri="direct:\/\/in"\/>\n    <to uri="direct:\/\/inner"\/>\n    <!-- why/);
        assert.match(after, /<filter expr="header.ok == true">\n    <\/filter>/, "the scope is now empty");
        parseXml(after);
    });

    it("dropping into its own subtree refuses quietly", () => {
        assert.equal(computeMoveElement(MOVABLE, [0, 3], [0, 3], 0), null);
    });

    it("reorder along the line: everything else byte-identical", () => {
        const edits = computeMoveElement(MOVABLE, [0, 2], [0], 1)!;
        const after = applyAll(MOVABLE, edits);

        assert.match(after, /from uri="direct:\/\/in"\/>\n    <setHeader name="k" expr="v"\/>\n    <!-- why/);
        parseXml(after);
        assert.equal(after.length, MOVABLE.length, "a pure reorder changes no byte count");
    });
});
