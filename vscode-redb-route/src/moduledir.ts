import * as fs from "fs";
import * as path from "path";

/**
 * The module a route document belongs to: the nearest folder with a .csproj, at most three levels
 * above the document's folder. This is the project root `redb-route-xml pack` builds from, with
 * `context.xml` beside the .csproj and the routes under `routes/` (RoutePackage).
 */
export function findModuleDir(start: string): { dir: string; csproj: string } | null {
    let dir = start;
    for (let level = 0; level < 4; level++) {
        const csproj = readDir(dir).find(f => f.toLowerCase().endsWith(".csproj"));
        if (csproj) return { dir, csproj };
        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }
    return null;
}

/** The context document's file name at the module root (RoutePackage.ContextFile). */
export const CONTEXT_FILE = "context.xml";

/**
 * The text of the module's `context.xml` for a document, or null when the document has no module,
 * the module has no context file, or the document is that file itself. `openText` returns the text
 * of a file open in an editor, so unsaved edits are seen before the file on disk.
 */
export function moduleContextText(
    documentPath: string, openText: (file: string) => string | undefined): string | null {
    const module = findModuleDir(path.dirname(documentPath));
    if (!module)
        return null;
    const file = path.join(module.dir, CONTEXT_FILE);
    if (samePath(file, documentPath))
        return null;
    const open = openText(file);
    if (open !== undefined)
        return open;
    try {
        return fs.readFileSync(file, "utf8");
    } catch {
        return null; // no context file in this module
    }
}

function readDir(dir: string): string[] {
    try {
        return fs.readdirSync(dir);
    } catch {
        return [];
    }
}

function samePath(a: string, b: string): boolean {
    const left = path.resolve(a);
    const right = path.resolve(b);
    return process.platform === "win32" ? left.toLowerCase() === right.toLowerCase() : left === right;
}
