import * as vscode from "vscode";
import * as path from "path";
import { execFile } from "child_process";
import { sniff } from "./sniff";
import { scanRoutes, RouteEntry } from "./routescan";
import { elementPosition } from "./elementposition";
import { SKELETONS } from "./skeletons";
import { RouteGraphEditorProvider } from "./grapheditor";

const FILE_GLOBS = ["**/*.route.xml", "**/*.redb-route.xml"];

/**
 * Ф7, text mode. Completion, validation and enum hints come from redhat.vscode-xml reading
 * our shipped XSD; the binding is NAMESPACE-driven through an XML catalog, so only documents
 * whose root says urn:redb:route get the schema — a foreign route.xml is untouched (the
 * activation contract, owner decision 2026-09-04). This extension adds the catalog, the
 * route tree, snippets and the Mermaid command. No canvas, no webview, no parser of the
 * format.
 */
export async function activate(context: vscode.ExtensionContext): Promise<void> {
    await wireSchemaCatalog(context);

    const tree = new RouteTreeProvider();
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider("redbRoutes", tree),
        vscode.commands.registerCommand("redbRoute.refreshTree", () => tree.refresh()),
        vscode.commands.registerCommand("redbRoute.open", openAtLine),
        vscode.commands.registerCommand("redbRoute.showMermaid", showMermaid),
        vscode.languages.registerCompletionItemProvider(
            "xml", new SkeletonCompletionProvider(), "<"),
        RouteGraphEditorProvider.register(context),
        vscode.commands.registerCommand("redbRoute.openGraph", async (uri?: vscode.Uri) => {
            const target = uri ?? vscode.window.activeTextEditor?.document.uri;
            if (target)
                await vscode.commands.executeCommand("vscode.openWith", target, RouteGraphEditorProvider.viewType);
        }),
    );
    for (const glob of FILE_GLOBS) {
        const watcher = vscode.workspace.createFileSystemWatcher(glob);
        context.subscriptions.push(watcher,
            watcher.onDidChange(() => tree.refresh()),
            watcher.onDidCreate(() => tree.refresh()),
            watcher.onDidDelete(() => tree.refresh()));
    }
    await tree.refresh();
}

/**
 * Registers the shipped XML catalog with redhat.vscode-xml — its extension API when present,
 * the user setting as the fallback. The catalog binds the NAMESPACE to the XSD (see
 * media/catalog.xml), which is the whole point: no file-pattern association anywhere.
 */
async function wireSchemaCatalog(context: vscode.ExtensionContext): Promise<void> {
    const catalogPath = context.asAbsolutePath(path.join("media", "catalog.xml"));
    const xmlExtension = vscode.extensions.getExtension("redhat.vscode-xml");
    if (xmlExtension) {
        const api = await xmlExtension.activate();
        if (api && typeof api.addXMLCatalogs === "function") {
            api.addXMLCatalogs([catalogPath]);
            return;
        }
    }
    // Older vscode-xml without the API: merge into the user's xml.catalogs once.
    const configuration = vscode.workspace.getConfiguration("xml");
    const catalogs = configuration.get<string[]>("catalogs") ?? [];
    if (!catalogs.includes(catalogPath)) {
        await configuration.update("catalogs", [...catalogs, catalogPath],
            vscode.ConfigurationTarget.Global);
    }
}

// ── composite skeletons (our completion provider, not declarative snippets) ──

/**
 * Declarative snippets appear in EVERY completion list — attribute positions included, any
 * XML file included (live finding, 2026-09-04). This provider serves the skeletons only in
 * OUR documents (the sniff) and only at a new-element position.
 */
class SkeletonCompletionProvider implements vscode.CompletionItemProvider {
    provideCompletionItems(
        document: vscode.TextDocument, position: vscode.Position): vscode.CompletionItem[] {
        if (sniff(document.getText())?.kind !== "routes")
            return [];
        const where = elementPosition(document.getText(), document.offsetAt(position));
        if (!where.ok)
            return [];

        const range = new vscode.Range(document.positionAt(where.replaceFrom), position);
        return SKELETONS.map(skeleton => {
            const item = new vscode.CompletionItem(
                skeleton.label, vscode.CompletionItemKind.Snippet);
            item.detail = skeleton.detail;
            item.insertText = new vscode.SnippetString(skeleton.body);
            item.range = range;
            return item;
        });
    }
}

// ── the route tree ───────────────────────────────────────────────────

type TreeNode =
    | { kind: "file"; uri: vscode.Uri; routes: RouteEntry[] }
    | { kind: "route"; uri: vscode.Uri; entry: RouteEntry };

class RouteTreeProvider implements vscode.TreeDataProvider<TreeNode> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    private files: { uri: vscode.Uri; routes: RouteEntry[] }[] = [];

    async refresh(): Promise<void> {
        const found = new Map<string, vscode.Uri>();
        for (const glob of FILE_GLOBS)
            for (const uri of await vscode.workspace.findFiles(glob, "**/{node_modules,bin,obj}/**"))
                found.set(uri.toString(), uri);

        const files: { uri: vscode.Uri; routes: RouteEntry[] }[] = [];
        for (const uri of found.values()) {
            const text = Buffer.from(await vscode.workspace.fs.readFile(uri)).toString("utf8");
            // The negative half of the activation contract: a file that does not carry our
            // namespace — someone else's route.xml — never enters the tree.
            if (sniff(text)?.kind !== "routes") continue;
            files.push({ uri, routes: scanRoutes(text) });
        }
        files.sort((a, b) => a.uri.fsPath.localeCompare(b.uri.fsPath));
        this.files = files;
        await vscode.commands.executeCommand(
            "setContext", "redbRoute.hasRoutes", files.length > 0);
        this.changed.fire();
    }

    getTreeItem(node: TreeNode): vscode.TreeItem {
        if (node.kind === "file") {
            const item = new vscode.TreeItem(
                path.basename(node.uri.fsPath), vscode.TreeItemCollapsibleState.Expanded);
            item.resourceUri = node.uri;
            item.description = vscode.workspace.asRelativePath(path.dirname(node.uri.fsPath));
            item.iconPath = vscode.ThemeIcon.File;
            return item;
        }
        const item = new vscode.TreeItem(
            node.entry.id ?? "(no id)", vscode.TreeItemCollapsibleState.None);
        item.description = node.entry.from ?? "";
        item.tooltip = node.entry.description ?? undefined;
        item.iconPath = new vscode.ThemeIcon("symbol-event");
        item.command = {
            command: "redbRoute.open",
            title: "Open route",
            arguments: [node.uri, node.entry.line],
        };
        return item;
    }

    getChildren(node?: TreeNode): TreeNode[] {
        if (!node)
            return this.files.map(f => ({ kind: "file", uri: f.uri, routes: f.routes }));
        if (node.kind === "file")
            return node.routes.map(entry => ({ kind: "route", uri: node.uri, entry }));
        return [];
    }
}

async function openAtLine(uri: vscode.Uri, line: number): Promise<void> {
    const document = await vscode.workspace.openTextDocument(uri);
    const editor = await vscode.window.showTextDocument(document);
    const position = new vscode.Position(line, 0);
    editor.selection = new vscode.Selection(position, position);
    editor.revealRange(new vscode.Range(position, position),
        vscode.TextEditorRevealType.InCenter);
}

// ── Mermaid via the Ф6 generator, when the dotnet tool is around ─────

async function showMermaid(): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    if (!editor || sniff(editor.document.getText())?.kind !== "routes") {
        void vscode.window.showInformationMessage(
            "redb Route: open a routes document (urn:redb:route) first.");
        return;
    }
    if (editor.document.isDirty) await editor.document.save();

    // No shell on purpose: a dotnet global tool is a plain executable shim, and only a
    // shell-less spawn reports a missing tool as ENOENT (through a shell it is exit code 1
    // plus a CP866 complaint on Windows — mojibake in the warning, live finding 2026-09-08).
    execFile("redb-route-xml", ["mermaid", editor.document.uri.fsPath],
        async (error, stdout, stderr) => {
        if (error) {
            const notFound = (error as NodeJS.ErrnoException).code === "ENOENT";
            void vscode.window.showWarningMessage(notFound
                ? "redb Route: install the generator to render diagrams: dotnet tool install -g redb.Route.Xml.CodeGen"
                : `redb Route: the generator failed: ${stderr || error.message}`);
            return;
        }
        const document = await vscode.workspace.openTextDocument({
            content: stdout, language: "markdown" });
        await vscode.window.showTextDocument(document, { preview: true });
    });
}

export function deactivate(): void { /* nothing to release */ }
