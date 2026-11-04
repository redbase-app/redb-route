import { describe, it, before, after } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { findModuleDir, moduleContextText } from "../moduledir";
import { beanCandidates } from "../beanref";

const CONTEXT = `<?xml version="1.0" encoding="utf-8"?>
<context xmlns="urn:redb:route:1.0">
  <bean name="orders" type="Module.Orders, Module"/>
  <!-- <bean name="retired" type="Module.Retired, Module"/> -->
  <bean name="audit" type="Module.Audit, Module"/>
</context>
`;

const ROUTES = `<routes xmlns="urn:redb:route:1.0">
  <bean name="local" type="Module.Local, Module"/>
  <route id="r"><from uri="direct:a"/><to uri="bean:#"/></route>
</routes>
`;

const NO_FILE_OPEN = (): string | undefined => undefined;

describe("module context.xml for # bean hints", () => {
    let root: string;
    let module: string;
    let route: string;

    before(() => {
        root = fs.mkdtempSync(path.join(os.tmpdir(), "redb-moduledir-"));
        module = path.join(root, "OrdersModule");
        fs.mkdirSync(path.join(module, "routes"), { recursive: true });
        fs.writeFileSync(path.join(module, "OrdersModule.csproj"), "<Project/>");
        fs.writeFileSync(path.join(module, "context.xml"), CONTEXT);
        route = path.join(module, "routes", "orders.route.xml");
        fs.writeFileSync(route, ROUTES);
    });

    after(() => fs.rmSync(root, { recursive: true, force: true }));

    it("finds the module root above routes/ by its .csproj", () => {
        assert.deepEqual(findModuleDir(path.dirname(route)), { dir: module, csproj: "OrdersModule.csproj" });
    });

    it("offers the route file's own beans first, then the beans of context.xml", () => {
        const candidates = beanCandidates(ROUTES, moduleContextText(route, NO_FILE_OPEN));
        assert.deepEqual(candidates, [
            { name: "local", fromContext: false },
            { name: "orders", fromContext: true },
            { name: "audit", fromContext: true },
        ]);
    });

    it("skips a commented-out bean of context.xml", () => {
        const names = beanCandidates(ROUTES, moduleContextText(route, NO_FILE_OPEN)).map(c => c.name);
        assert.equal(names.includes("retired"), false);
    });

    it("lists a name declared in both files once, as the document's own", () => {
        const routes = ROUTES.replace(`name="local"`, `name="orders"`);
        const candidates = beanCandidates(routes, moduleContextText(route, NO_FILE_OPEN));
        assert.deepEqual(candidates, [
            { name: "orders", fromContext: false },
            { name: "audit", fromContext: true },
        ]);
    });

    it("reads the unsaved text of an open context.xml before the file on disk", () => {
        const contextFile = path.join(module, "context.xml");
        const edited = CONTEXT.replace(`name="audit"`, `name="auditTrail"`);
        const openText = (file: string) => (path.resolve(file) === path.resolve(contextFile) ? edited : undefined);
        const names = beanCandidates(ROUTES, moduleContextText(route, openText)).map(c => c.name);
        assert.deepEqual(names, ["local", "orders", "auditTrail"]);
    });

    it("ignores a context.xml that is not a redb context document", () => {
        const foreign = `<context xmlns="urn:other:1.0"><bean name="foreign"/></context>`;
        const names = beanCandidates(ROUTES, foreign).map(c => c.name);
        assert.deepEqual(names, ["local"]);
    });

    it("gives no context beans to a file outside any module", () => {
        const loose = path.join(root, "loose", "a", "b", "c", "d", "loose.route.xml");
        fs.mkdirSync(path.dirname(loose), { recursive: true });
        fs.writeFileSync(loose, ROUTES);
        assert.equal(moduleContextText(loose, NO_FILE_OPEN), null);
    });

    it("gives no context beans when the module has no context.xml", () => {
        const bare = path.join(root, "BareModule");
        fs.mkdirSync(path.join(bare, "routes"), { recursive: true });
        fs.writeFileSync(path.join(bare, "BareModule.csproj"), "<Project/>");
        assert.equal(moduleContextText(path.join(bare, "routes", "x.route.xml"), NO_FILE_OPEN), null);
    });

    it("offers context.xml its own beans and does not read itself a second time", () => {
        const contextFile = path.join(module, "context.xml");
        assert.equal(moduleContextText(contextFile, NO_FILE_OPEN), null);
        assert.deepEqual(beanCandidates(CONTEXT, null).map(c => c.name), ["orders", "audit"]);
    });
});
