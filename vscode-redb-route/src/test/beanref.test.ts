import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { beanCallPosition, beanNames, referencePosition, typedReferencePosition } from "../beanref";
import { buildIndex, ElementInfo } from "../graphmodel";

const INDEX = buildIndex(JSON.parse(fs.readFileSync(
    path.join(__dirname, "..", "..", "media", "redb-route-elements.json"), "utf8")) as ElementInfo[]);

function typedAt(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return typedReferencePosition(textWithCursor.replace("|", ""), offset, INDEX);
}

function at(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return referencePosition(textWithCursor.replace("|", ""), offset);
}

describe("referencePosition — where `#` offers bean names", () => {
    it("takes a URI option value", () => {
        const text = `<from uri="rabbitmq:orders?headerFilterStrategy=#|"/>`;
        assert.deepEqual(at(text), { ok: true, replaceFrom: text.indexOf("|") });
    });

    it("takes a later option and replaces the half-typed name", () => {
        const text = `<to uri="kafka:t?brokers=b&headerFilterStrategy=#fil|"/>`;
        assert.deepEqual(at(text), { ok: true, replaceFrom: text.indexOf("fil|") });
    });

    it("takes the whole value of a structured option", () => {
        assert.equal(at(`<sql dataSource="#|">`).ok, true);
    });

    it("takes the path right after the scheme", () => {
        assert.equal(at(`<to uri="bean:#|"/>`).ok, true);
        assert.equal(at(`<to uri='bean://#my|'/>`).ok, true);
    });

    it("refuses a `#` inside text of a value — the sql parameter", () => {
        assert.equal(at(`<to uri="sql:select * from t where id = :#|"/>`).ok, false);
    });

    it("refuses a `#` in the middle of an option value", () => {
        assert.equal(at(`<to uri="http://h/p?q=a#|"/>`).ok, false);
    });

    it("refuses a `#` after a path that is not the scheme's", () => {
        assert.equal(at(`<to uri="direct:a#|"/>`).ok, false);
    });

    it("refuses element content, a closed value and comments", () => {
        assert.equal(at(`<log>#|</log>`).ok, false);
        assert.equal(at(`<to uri="bean:x" #|`).ok, false);
        assert.equal(at(`<!-- <to uri="bean:#| -->`).ok, false);
    });

    it("refuses a value after the tag was closed", () => {
        assert.equal(at(`<to uri="bean:x"/> "#|`).ok, false);
    });

    it("takes a reference after an attribute whose value holds a >", () => {
        assert.equal(at(`<filter expr="header.amount>1000" predicate="#|"`).ok, true);
        assert.equal(typedAt(`<routes><route><filter expr="a>b" predicate="|"`).ok, true);
    });
});

describe("typedReferencePosition — a reference attribute before the `#`", () => {
    it("takes an empty or half-typed value of an attribute the element list types as a reference", () => {
        const text = `<routes><route><filter predicate="is|"/></route></routes>`;
        assert.deepEqual(typedAt(text), { ok: true, replaceFrom: text.indexOf("is|") });
        assert.equal(typedAt(`<routes><route><idempotentConsumer key="k" repository="|"`).ok, true);
    });

    it("finds a child entry under its parent", () => {
        assert.equal(typedAt(`<routes><route><choice><when predicate="|"`).ok, true);
        assert.equal(typedAt(`<routes><route><saga><step processor="|"`).ok, true);
    });

    it("refuses an attribute of another type and a value a bare name cannot be", () => {
        assert.equal(typedAt(`<routes><route><filter expr="|"`).ok, false);
        assert.equal(typedAt(`<routes><route><to uri="|"`).ok, false);
        assert.equal(typedAt(`<routes><route><filter predicate="#|"`).ok, false, "the # rule serves this one");
        assert.equal(typedAt(`<routes><route><filter predicate="a b|"`).ok, false);
    });

    it("refuses a comment and a closed tag", () => {
        assert.equal(typedAt(`<routes><!-- <filter predicate="|`).ok, false);
        assert.equal(typedAt(`<routes><route><filter predicate="x"/> |`).ok, false);
    });
});

describe("beanNames — the beans a `#` may name", () => {
    it("lists every named bean once, in document order", () => {
        const text = `<routes>
  <bean name="filter" type="A, a"/>
  <bean type="B, b" name='db'><property key="x" value="1"/></bean>
  <bean name="filter" type="C, c"/>
  <route><to><bean path="#filter"/></to></route>
</routes>`;
        assert.deepEqual(beanNames(text), ["filter", "db"]);
    });

    it("takes declarations under the root only, not a step or endpoint bean in a route", () => {
        const text = `<routes xmlns="urn:redb:route:1.0">
  <bean name="svc" type="A, a"/>
  <route><from uri="direct:a"/><bean ref="#svc" method="Go" name="step"/>
    <filter expr="true"><bean beanType="B, b" name="deep"/></filter></route>
</routes>`;
        assert.deepEqual(beanNames(text), ["svc"]);
    });

    it("skips commented-out beans and elements that only start with bean", () => {
        const text = `<!-- <bean name="old" type="A, a"/> --><beanish name="no"/><bean name="yes" type="A, a"/>`;
        assert.deepEqual(beanNames(text), ["yes"]);
    });
});

function callAt(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return beanCallPosition(textWithCursor.replace("|", ""), offset, INDEX);
}

describe("beanCallPosition — the bean name of an expression's bean call", () => {
    it("takes the template hole in any attribute value, element text and CDATA", () => {
        const text = `<routes><route><setBody value="Total: \${bean:pri|"/>`;
        assert.deepEqual(callAt(text), { ok: true, replaceFrom: text.indexOf("pri|") });
        assert.equal(callAt(`<routes><route><toD uri="http://h/\${bean:|"`).ok, true);
        assert.equal(callAt(`<routes><route><log>sum \${bean:|`).ok, true);
        assert.equal(callAt(`<routes><route><setBody><![CDATA[a < b \${bean:|`).ok, true);
    });

    it("takes a name with dashes and a nested hole inside the arguments", () => {
        const text = `<routes><route><filter expr="bean:seen-or|"`;
        assert.deepEqual(callAt(text), { ok: true, replaceFrom: text.indexOf("seen-or|") });
        assert.equal(callAt(`<routes><route><setBody value="\${bean:a.M(\${bean:|"`).ok, true);
    });

    it("takes a bare bean: at the start of an expression attribute or after an operator", () => {
        assert.equal(callAt(`<routes><route><filter expr="bean:|"`).ok, true);
        assert.equal(callAt(`<routes><route><filter expr="header.vip == true && !bean:|"`).ok, true);
        assert.equal(callAt(`<routes><route><choice><when expr="x(bean:|"`).ok, true);
    });

    it("takes a bare bean: or ref: right after =~ with no space", () => {
        assert.equal(callAt(`<routes><route><filter expr="header.tier=~bean:|"`).ok, true);
        assert.equal(callAt(`<routes><route><filter expr="header.tier=~ref:|"`).ok, true);
    });

    it("offers nothing after the dot: the method is not known to the editor", () => {
        assert.equal(callAt(`<routes><route><filter expr="bean:pricing.|"`).ok, false);
        assert.equal(callAt(`<routes><route><setBody value="\${bean:pricing.Gr|"`).ok, false);
    });

    it("refuses a bare bean: outside an expression attribute, glued to a term, or in a literal", () => {
        assert.equal(callAt(`<routes><route><to uri="bean:|"`).ok, false, "the bean: URI scheme");
        assert.equal(callAt(`<routes><route><setBody value="bean:|"`).ok, false, "a String attribute");
        assert.equal(callAt(`<routes><route><filter expr="xbean:|"`).ok, false);
        assert.equal(callAt(`<routes><route><filter expr="body == 'bean:|"`).ok, false);
        assert.equal(callAt(`<routes><route><log>bean:|`).ok, false, "a bare call in element text is text");
    });

    it("refuses a comment and an attribute-name position", () => {
        assert.equal(callAt(`<routes><!-- \${bean:|`).ok, false);
        assert.equal(callAt(`<routes><route><filter \${bean:|`).ok, false);
    });
});

describe("beanCallPosition — the object name of an expression's ref: read", () => {
    it("takes the template hole and a bare ref: in an expression attribute", () => {
        const text = `<routes><route><setBody value="Rate: \${ref:ra|"/>`;
        assert.deepEqual(callAt(text), { ok: true, replaceFrom: text.indexOf("ra|") });
        assert.equal(callAt(`<routes><route><log>\${ref:|`).ok, true);
        assert.equal(callAt(`<routes><route><filter expr="ref:|"`).ok, true);
        assert.equal(callAt(`<routes><route><filter expr="body != null && ref:|"`).ok, true);
    });

    it("reads the whole run as the name, dots and dashes included", () => {
        const text = `<routes><route><filter expr="ref:rates.eu-west|"`;
        assert.deepEqual(callAt(text), { ok: true, replaceFrom: text.indexOf("rates.eu-west|") });
    });

    it("offers nothing after the name", () => {
        assert.equal(callAt(`<routes><route><setBody value="\${ref:rates}|"`).ok, false);
        assert.equal(callAt(`<routes><route><filter expr="ref:rates |"`).ok, false);
        assert.equal(callAt(`<routes><route><filter expr="ref:rates(|"`).ok, false);
    });

    it("refuses a bare ref: outside an expression attribute, glued to a term, or in a literal", () => {
        assert.equal(callAt(`<routes><route><setBody value="ref:|"`).ok, false, "a String attribute");
        assert.equal(callAt(`<routes><route><filter expr="xref:|"`).ok, false);
        assert.equal(callAt(`<routes><route><filter expr="body == 'ref:|"`).ok, false);
        assert.equal(callAt(`<routes><route><log>ref:|`).ok, false, "a bare read in element text is text");
    });
});
