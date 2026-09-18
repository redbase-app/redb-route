import * as vscode from "vscode";
import * as fs from "fs";
import * as path from "path";
import { sniff } from "./sniff";
import { parseXml, XmlParseError, XmlElement, childElements, textContent } from "./xmlmodel";
import { buildFileGraph, buildIndex, ElementInfo, ElementIndex, redactUri } from "./graphmodel";
import {
    computeSetAttribute, computeSetTextContent, computeRemoveElement, computeInsertChild,
    computeInsertAt, computeMoveElement, computeRemoveBlock,
    resolveByPath, ElementPath,
} from "./textedit";
import { buildPalette } from "./palette";
import { parseEndpointUri, setUriOption, setUriPath, parameterSpelling } from "./endpointuri";
import { ConfigMap, flattenConfig, resolvePlaceholders } from "./placeholders";
import { UriResolver } from "./graphmodel";

/** One row of the properties panel (§8). */
interface PropField {
    name: string;
    type: string;
    required: boolean;
    enumValues: string[] | null;
    /** Redacted when secret. */
    value: string | null;
    /** A secret value is shown masked and not editable through the panel (§8). */
    secret: boolean;
    /** Declared by the spec (false = present in the file but unknown to it). */
    declared: boolean;
}

interface PropsPayload {
    path: ElementPath;
    type: string;
    span: { start: number; end: number };
    fields: PropField[];
    /** For uri-carrying nodes: the endpoint decomposed by the Ф4 catalog. */
    endpoint: {
        scheme: string;
        path: string;
        /** The scheme's catalog options merged with the uri's actual parameters. */
        options: PropField[];
    } | null;
    /** Config children of a rich element (log message/header/property) — editable rows. */
    content: ContentRow[];
    /** Child element names the spec allows to ADD (log → message/header/property). */
    addable: string[];
}

interface ContentRow {
    path: ElementPath;
    type: string;
    /** The element's text content, when its spec allows text (message) — editable. */
    text: string | null;
    /** The name attribute, when the child carries one (header/property) — editable. */
    name: string | null;
    /** Neither text nor name — shown as-is, edited in text. */
    opaque: string | null;
}

interface CatalogComponent {
    scheme: string;
    alternateSchemes: string[];
    pathSynonym?: string | null;
    options: { name: string; type: string; default?: string | null; sensitive: boolean; enumValues?: string[] }[];
}

/**
 * Ф8 §8.1: the text is the truth, the graph is a projection. A CustomTextEditorProvider over
 * the SAME text document — undo history and dirty state are the text editor's own; the
 * webview holds no document state. Такт 1 scope: render + cursor↔node sync; editing lands in
 * the following тактs as surgical text edits.
 */
export class RouteGraphEditorProvider implements vscode.CustomTextEditorProvider {
    static readonly viewType = "redbRoute.graph";

    private readonly index: ElementIndex;
    private readonly catalog = new Map<string, CatalogComponent>();

    constructor(private readonly context: vscode.ExtensionContext) {
        const elements: ElementInfo[] = JSON.parse(fs.readFileSync(
            context.asAbsolutePath(path.join("media", "redb-route-elements.json")), "utf8"));
        this.index = buildIndex(elements);
        const components: CatalogComponent[] = JSON.parse(fs.readFileSync(
            context.asAbsolutePath(path.join("media", "redb-route-catalog.json")), "utf8"));
        for (const component of components) {
            this.catalog.set(component.scheme, component);
            for (const alternate of component.alternateSchemes ?? [])
                this.catalog.set(alternate, component);
        }
        // The catalog declares no alternates; the everyday spelling pairs still must find
        // their options (a from uri="http://…" is the https component's endpoint).
        for (const [alias, canonical] of [["http", "https"], ["ws", "wss"]] as const) {
            const component = this.catalog.get(canonical);
            if (component && !this.catalog.has(alias)) this.catalog.set(alias, component);
        }
    }

    /**
     * §8: the placeholder is shown together with its resolved value. The values come from
     * the scaffold's own convention — `config/*.json` and `context.sample.json` found by
     * walking up from the route document (sample first, named configs override).
     */
    private loadConfig(documentPath: string): { config: ConfigMap; sources: string[] } {
        const config: ConfigMap = new Map();
        const sources: string[] = [];
        const read = (file: string): void => {
            try {
                flattenConfig(JSON.parse(fs.readFileSync(file, "utf8")), config);
                sources.push(path.basename(file));
            } catch { /* not JSON or unreadable — the view just resolves less */ }
        };
        let dir = path.dirname(documentPath);
        for (let level = 0; level < 4; level++) {
            const sample = path.join(dir, "context.sample.json");
            if (fs.existsSync(sample)) read(sample);
            const configDir = path.join(dir, "config");
            if (fs.existsSync(configDir))
                for (const file of fs.readdirSync(configDir).filter(f => f.endsWith(".json")).sort())
                    read(path.join(configDir, file));
            const parent = path.dirname(dir);
            if (parent === dir) break;
            dir = parent;
        }
        return { config, sources };
    }

    static register(context: vscode.ExtensionContext): vscode.Disposable {
        return vscode.window.registerCustomEditorProvider(
            RouteGraphEditorProvider.viewType,
            new RouteGraphEditorProvider(context),
            { webviewOptions: { retainContextWhenHidden: true } });
    }

    resolveCustomTextEditor(
        document: vscode.TextDocument,
        panel: vscode.WebviewPanel): void {
        panel.webview.options = {
            enableScripts: true,
            localResourceRoots: [vscode.Uri.joinPath(this.context.extensionUri, "media")],
        };
        panel.webview.html = this.shell(panel.webview);
        panel.webview.postMessage({
            type: "palette",
            palette: buildPalette(
                [...this.index.values()],
                [...new Set(this.catalog.values())].map(c => ({ scheme: c.scheme, pathSynonym: c.pathSynonym }))),
        });

        // §4: collapsed-ness belongs to the VIEWER, not the file (Р5) — the explicit toggles
        // live in workspaceState per document; the defaults (deep nodes fold) are computed in
        // the webview.
        const collapseKey = `redbRoute.collapse:${document.uri.toString()}`;
        const collapseState = (): string[] =>
            this.context.workspaceState.get<string[]>(collapseKey) ?? [];

        // The layout mode — the snake (LR, §2) or the vertical columns — is the viewer's
        // choice per document, workspaceState like the collapse (Р5).
        const layoutKey = `redbRoute.layout:${document.uri.toString()}`;
        const layoutState = (): string =>
            this.context.workspaceState.get<string>(layoutKey) ?? "snake";

        // The zoom factor is the viewer's choice per document, same as the layout.
        const zoomKey = `redbRoute.zoom:${document.uri.toString()}`;
        const zoomState = (): number =>
            this.context.workspaceState.get<number>(zoomKey) ?? 1;

        // Config files change rarely; one read per couple of seconds, not per node.
        let cachedConfig: ConfigMap | null = null;
        let cachedAt = 0;
        const resolver: UriResolver = uri => {
            if (!cachedConfig || Date.now() - cachedAt > 2000) {
                cachedConfig = this.loadConfig(document.uri.fsPath).config;
                cachedAt = Date.now();
            }
            const { resolved, fromConfig } = resolvePlaceholders(uri, cachedConfig);
            return { resolved, fromConfig };
        };

        const render = (): void => {
            const text = document.getText();
            if (sniff(text)?.kind !== "routes") {
                panel.webview.postMessage({ type: "foreign" });
                return;
            }
            try {
                const graph = buildFileGraph(parseXml(text).root, this.index, resolver);
                panel.webview.postMessage({
                    type: "graph", graph,
                    toggled: collapseState(), layout: layoutState(), zoom: zoomState(),
                });
            } catch (error) {
                if (error instanceof XmlParseError) {
                    const position = document.positionAt(error.offset);
                    panel.webview.postMessage({
                        type: "broken",
                        message: error.message,
                        line: position.line + 1,
                    });
                    return;
                }
                throw error;
            }
        };

        // §8.6: outside text changes redraw the projection; full redraw, no increments.
        let pending: NodeJS.Timeout | undefined;
        const changed = vscode.workspace.onDidChangeTextDocument(e => {
            if (e.document.uri.toString() !== document.uri.toString()) return;
            clearTimeout(pending);
            pending = setTimeout(render, 120);
        });

        const postProps = (path: ElementPath): void => {
            const props = this.buildProps(document.getText(), path, resolver);
            if (props) panel.webview.postMessage({ type: "props", props });
        };

        const applyReplacementEdit = async (replacement: { start: number; end: number; newText: string } | null): Promise<void> => {
            if (!replacement) return;
            const edit = new vscode.WorkspaceEdit();
            edit.replace(document.uri,
                new vscode.Range(
                    document.positionAt(replacement.start),
                    document.positionAt(replacement.end)),
                replacement.newText);
            await vscode.workspace.applyEdit(edit);
        };

        const messages = panel.webview.onDidReceiveMessage(async message => {
            if (message.type === "select")
                postProps(message.path);
            if (message.type === "setLayout")
                await this.context.workspaceState.update(layoutKey, message.layout);
            if (message.type === "setZoom")
                await this.context.workspaceState.update(zoomKey, message.zoom);
            if (message.type === "toggleCollapse") {
                const current = new Set(collapseState());
                if (current.has(message.key)) current.delete(message.key);
                else current.add(message.key);
                await this.context.workspaceState.update(collapseKey, [...current]);
            }
            if (message.type === "insertStep") {
                await applyReplacementEdit(computeInsertAt(
                    document.getText(), message.parentPath, message.before, message.fragment));
            }
            if (message.type === "removeStep")
                await applyReplacementEdit(computeRemoveBlock(document.getText(), message.path));
            if (message.type === "moveStep") {
                const edits = computeMoveElement(
                    document.getText(), message.fromPath, message.toParentPath, message.before);
                if (edits) {
                    const edit = new vscode.WorkspaceEdit();
                    for (const replacement of edits)
                        edit.replace(document.uri,
                            new vscode.Range(
                                document.positionAt(replacement.start),
                                document.positionAt(replacement.end)),
                            replacement.newText);
                    await vscode.workspace.applyEdit(edit);
                }
            }
            if (message.type === "setChildText") {
                await applyReplacementEdit(computeSetTextContent(
                    document.getText(), message.path, message.value ?? ""));
                postProps(message.path.slice(0, -1));
            }
            if (message.type === "removeChild") {
                await applyReplacementEdit(computeRemoveElement(document.getText(), message.path));
                postProps(message.path.slice(0, -1));
            }
            if (message.type === "addChild") {
                const parent = resolveByPath(parseXml(document.getText()).root, message.path);
                const parentInfo = parent && parent.prefix === null ? this.index.get(parent.local) : undefined;
                await applyReplacementEdit(computeInsertChild(
                    document.getText(), message.path,
                    this.childFragment(parentInfo, message.childName)));
                postProps(message.path);
            }
            if (message.type === "setUriOption" || message.type === "setUriPath") {
                // Endpoint-option edit: rebuild the uri (order preserved, one parameter
                // touched) and ride the ordinary one-line attribute edit.
                const element = resolveByPath(parseXml(document.getText()).root, message.path);
                const uri = element?.attributes.find(a => a.name === "uri")?.value;
                if (element && uri !== undefined) {
                    const rebuilt = message.type === "setUriPath"
                        ? setUriPath(uri, message.value ?? "")
                        : setUriOption(uri,
                            message.existing ? message.name : parameterSpelling(message.name),
                            message.value);
                    if (rebuilt !== null && rebuilt !== uri) {
                        const replacement = computeSetAttribute(
                            document.getText(), message.path, "uri", rebuilt);
                        if (replacement) {
                            const edit = new vscode.WorkspaceEdit();
                            edit.replace(document.uri,
                                new vscode.Range(
                                    document.positionAt(replacement.start),
                                    document.positionAt(replacement.end)),
                                replacement.newText);
                            await vscode.workspace.applyEdit(edit);
                        }
                    }
                }
                postProps(message.path);
            }
            if (message.type === "setAttr") {
                const replacement = computeSetAttribute(
                    document.getText(), message.path, message.name, message.value);
                if (replacement) {
                    const edit = new vscode.WorkspaceEdit();
                    edit.replace(document.uri,
                        new vscode.Range(
                            document.positionAt(replacement.start),
                            document.positionAt(replacement.end)),
                        replacement.newText);
                    await vscode.workspace.applyEdit(edit);
                }
                postProps(message.path); // refreshed values, same node
            }
            if (message.type === "reveal") {
                // Node click → the exact spot in the text, side by side.
                const editor = await vscode.window.showTextDocument(document,
                    { viewColumn: vscode.ViewColumn.Beside, preserveFocus: false, preview: true });
                const start = document.positionAt(message.start);
                editor.selection = new vscode.Selection(start, start);
                editor.revealRange(
                    new vscode.Range(start, document.positionAt(message.end)),
                    vscode.TextEditorRevealType.InCenter);
            }
            if (message.type === "openAsText")
                await vscode.commands.executeCommand("workbench.action.reopenTextEditor", document.uri);
        });

        panel.onDidDispose(() => {
            clearTimeout(pending);
            changed.dispose();
            messages.dispose();
        });
        render();
    }

    /**
     * The §8 panel content: id and description first, then the spec's attributes with types,
     * enums and required flags, then whatever else the file carries (unknown to the spec —
     * editable as plain strings). A secret-bearing value is masked and locked: the panel
     * never shows it and never rewrites it (edit those in text).
     */
    private buildProps(text: string, path: ElementPath, resolver?: UriResolver): PropsPayload | null {
        let element: XmlElement | null;
        try {
            element = resolveByPath(parseXml(text).root, path);
        } catch {
            return null; // mid-edit broken document — the panel just waits
        }
        if (!element) return null;

        const info = element.prefix === null ? this.index.get(element.local) : undefined;
        const fields: PropField[] = [];
        const seen = new Set<string>();
        const push = (name: string, type: string, required: boolean, enumValues: string[] | null, declared: boolean): void => {
            if (seen.has(name)) return;
            seen.add(name);
            const raw = element!.attributes.find(a => a.name === name)?.value ?? null;
            const secret = raw !== null && redactUri(raw) !== raw;
            fields.push({
                name, type, required, enumValues,
                value: raw === null ? null : (secret ? redactUri(raw) : raw),
                secret, declared,
            });
        };

        push("id", "String", false, null, true);
        push("description", "String", false, null, true);
        // `from` is parsed by the loader itself, not a registry contribution — its uri is
        // declared here so the consumer's endpoint reads as first-class, not italic.
        if (element.local === "from" && element.prefix === null)
            push("uri", "Uri", true, null, true);
        for (const spec of info?.attributes ?? [])
            push(spec.name, spec.type, spec.required ?? false, spec.enumValues ?? null, true);
        for (const present of element.attributes)
            push(present.name, "String", false, null, info !== undefined ? false : true);

        return {
            path,
            type: element.prefix === null ? element.local : element.name,
            span: { start: element.span.start, end: element.span.end },
            fields,
            endpoint: this.buildEndpointFields(element, resolver),
            content: childElements(element).map((child, i): ContentRow => {
                const childSpec = info?.children?.find(c => c.name === child.local);
                const childPath = [...path, i];
                if (childSpec?.allowsText || (childSpec === undefined && child.attributes.length === 0)) {
                    return { path: childPath, type: child.local, text: redactUri(textContent(child)), name: null, opaque: null };
                }
                if (child.attributes.some(a => a.name === "name")) {
                    return {
                        path: childPath, type: child.local, text: null,
                        name: child.attributes.find(a => a.name === "name")!.value, opaque: null,
                    };
                }
                const bits = child.attributes.map(a => `${a.name}="${redactUri(a.value)}"`).join(" ");
                return { path: childPath, type: child.local, text: null, name: null, opaque: `<${child.name}${bits ? " " + bits : ""}>` };
            }),
            addable: (info?.children ?? []).map(c => c.name),
        };
    }

    /** The insertable fragment for one child kind: text-bearing gets a body, named gets name="". */
    private childFragment(parentInfo: ElementInfo | undefined, childName: string): string {
        const spec = parentInfo?.children?.find(c => c.name === childName);
        if (spec?.allowsText) return `<${childName}></${childName}>`;
        if (spec?.attributes?.some(a => a.name === "name")) return `<${childName} name=""/>`;
        return `<${childName}/>`;
    }

    /** The uri decomposed by the Ф4 catalog: scheme, path, typed options + actual parameters. */
    private buildEndpointFields(element: XmlElement, resolver?: UriResolver): PropsPayload["endpoint"] {
        const uri = element.attributes.find(a => a.name === "uri")?.value;
        if (!uri) return null;
        let parsed = parseEndpointUri(uri);
        let viaConfig = false;
        if (!parsed && uri.includes("{{") && resolver) {
            // A pure-placeholder uri: decompose the RESOLVED value for the eye — read-only,
            // because what gets edited is the placeholder, never the value (Р18).
            const { resolved, fromConfig } = resolver(uri);
            if (fromConfig) {
                parsed = parseEndpointUri(resolved);
                viaConfig = true;
            }
        }
        if (!parsed) {
            return uri.includes("{{")
                ? { scheme: "{{config}}", path: redactUri(uri), options: [] }
                : null;
        }

        const component = this.catalog.get(parsed.scheme.toLowerCase());
        const options: PropField[] = [];
        const seen = new Set<string>();
        for (const option of component?.options ?? []) {
            const actual = parsed.params.find(p => p.name.toLowerCase() === option.name.toLowerCase());
            if (actual) seen.add(actual.name.toLowerCase());
            const secret = option.sensitive || (actual !== undefined && redactUri(`x://x?${actual.name}=${actual.value}`).includes("***"));
            options.push({
                name: option.name,
                type: option.type,
                required: false,
                enumValues: option.enumValues ?? (option.type === "bool" ? ["true", "false"] : null),
                value: actual === undefined ? null : (secret ? "***" : actual.value),
                secret,
                declared: true,
            });
        }
        for (const param of parsed.params) {
            if (seen.has(param.name.toLowerCase())) continue;
            const secret = redactUri(`x://x?${param.name}=${param.value}`).includes("***");
            options.push({
                name: param.name, type: "string", required: false, enumValues: null,
                value: secret ? "***" : param.value, secret, declared: false,
            });
        }
        // The set options first — what the uri actually says is what the eye needs; the
        // catalog's remaining knobs follow alphabetically.
        options.sort((a, b) =>
            (a.value === null ? 1 : 0) - (b.value === null ? 1 : 0)
            || a.name.localeCompare(b.name));
        if (viaConfig) {
            // The whole decomposition came from config: show it, lock it (Р18 - edit the
            // placeholder in the uri field, or the config file itself).
            for (const option of options) option.secret = true;
            return {
                scheme: parsed.scheme + " · via {{config}}",
                path: parsed.path,
                options: options.filter(o => o.value !== null),
            };
        }
        return { scheme: parsed.scheme, path: parsed.path, options };
    }

    private shell(webview: vscode.Webview): string {
        const css = webview.asWebviewUri(
            vscode.Uri.joinPath(this.context.extensionUri, "media", "graph.css"));
        const js = webview.asWebviewUri(
            vscode.Uri.joinPath(this.context.extensionUri, "media", "graph.js"));
        const nonce = Math.random().toString(36).slice(2);
        return `<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<link rel="stylesheet" href="${css}">
</head>
<body>
<div id="root"></div>
<div id="panel"></div>
<script nonce="${nonce}" src="${js}"></script>
</body>
</html>`;
    }
}
