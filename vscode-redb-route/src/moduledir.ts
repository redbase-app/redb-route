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

/** The resources directory name at the module root (RoutePackage.ResourcesDir), shipped by `pack`. */
export const RESOURCES_DIR = "resources";

/** A walk stops here: a resources folder is a handful of schemas and stylesheets, not a tree to crawl. */
const RESOURCE_WALK_LIMIT = 2000;

/**
 * The files and folders under the module's `resources/` for a document, relative to that folder with
 * '/' separators (the folder itself is the dir ""), or null when the document has no module or the
 * module has no resources folder. Sorted, at most RESOURCE_WALK_LIMIT entries.
 */
export function moduleResources(documentPath: string): { files: string[]; dirs: string[] } | null {
    const module = findModuleDir(path.dirname(documentPath));
    if (!module)
        return null;
    const root = path.join(module.dir, RESOURCES_DIR);
    if (!isDirectory(root))
        return null;
    const files: string[] = [];
    const dirs: string[] = [""];
    const pending: string[] = [""];
    while (pending.length > 0 && files.length + dirs.length < RESOURCE_WALK_LIMIT) {
        const relative = pending.shift()!;
        let entries: fs.Dirent[];
        try {
            entries = fs.readdirSync(path.join(root, relative), { withFileTypes: true });
        } catch {
            continue;
        }
        for (const entry of entries) {
            const name = relative === "" ? entry.name : `${relative}/${entry.name}`;
            if (entry.isDirectory()) {
                dirs.push(name);
                pending.push(name);
            } else if (entry.isFile()) {
                files.push(name);
            }
        }
    }
    return { files: files.sort(), dirs: dirs.sort() };
}

function isDirectory(dir: string): boolean {
    try {
        return fs.statSync(dir).isDirectory();
    } catch {
        return false;
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
