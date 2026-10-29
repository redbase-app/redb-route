/**
 * "redb: New Project": a page inside VS Code that creates a project from the redb templates
 * through `dotnet new`. It shows the SDKs and the template packages (with install and update
 * buttons), the templates as cards, a form for the chosen one, runs `dotnet new` and, when the
 * project carries a local tool manifest, `dotnet tool restore`, then offers to open the project.
 * Every decision is in the tested modules; this file wires the webview and the processes, and
 * every action of the page reaches it as a message.
 */

import * as vscode from "vscode";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { ChildProcess, spawn } from "child_process";
import {
    installedSdks, installedTemplatePackage, latestTemplateVersion, templateInstallArguments, templateState,
} from "./dotnetenv";
import { CreateForm, displayCommand, dotnetNewArguments, formErrors, pagePackage, pageState } from "./newproject";
import {
    orderTemplates, readTemplateDefinitions, RedbTemplate, selectTemplates, TEMPLATE_PACKAGE_IDS,
} from "./templates";

const DOTNET_DOWNLOAD = "https://dotnet.microsoft.com/download";

type PageMessage =
    | { type: "ready" | "refresh" | "cancel" | "showOutput" | "downloadDotnet" }
    | { type: "install"; packageId: string; version: string | null }
    | { type: "browse"; folder: string }
    | { type: "validate" | "create"; form: CreateForm }
    | { type: "open"; mode: "here" | "new" | "add" };

export class NewProjectPanel {
    private static current: NewProjectPanel | undefined;
    private templates: RedbTemplate[] = [];
    private running: ChildProcess | undefined;
    private cancelled = false;
    private created: { folder: string; name: string } | undefined;

    static show(context: vscode.ExtensionContext, output: vscode.OutputChannel): void {
        if (NewProjectPanel.current) {
            NewProjectPanel.current.panel.reveal();
            return;
        }
        const panel = vscode.window.createWebviewPanel("redbNewProject", "New redb Project", vscode.ViewColumn.Active, {
            enableScripts: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, "media")],
            retainContextWhenHidden: true,
        });
        NewProjectPanel.current = new NewProjectPanel(panel, context, output);
    }

    private constructor(
        private readonly panel: vscode.WebviewPanel,
        context: vscode.ExtensionContext,
        private readonly output: vscode.OutputChannel,
    ) {
        panel.iconPath = vscode.Uri.joinPath(context.extensionUri, "media", "icon.png");
        panel.webview.html = html(panel.webview, context.extensionUri);
        panel.webview.onDidReceiveMessage((message: PageMessage) => this.receive(message));
        panel.onDidDispose(() => {
            this.running?.kill();
            NewProjectPanel.current = undefined;
        });
    }

    private post(message: unknown): void {
        void this.panel.webview.postMessage(message);
    }

    private async receive(message: PageMessage): Promise<void> {
        switch (message.type) {
            case "ready":
            case "refresh":
                await this.sendState();
                return;
            case "downloadDotnet":
                await vscode.env.openExternal(vscode.Uri.parse(DOTNET_DOWNLOAD));
                return;
            case "showOutput":
                this.output.show(true);
                return;
            case "cancel":
                if (this.running) {
                    this.cancelled = true;
                    this.running.kill();
                }
                return;
            case "install":
                if (!(TEMPLATE_PACKAGE_IDS as readonly string[]).includes(message.packageId)) return;
                await this.run(`Installing ${message.packageId}`, "dotnet",
                    templateInstallArguments(message.packageId, message.version));
                await this.sendState();
                return;
            case "browse": {
                const picked = await vscode.window.showOpenDialog({
                    canSelectFolders: true, canSelectFiles: false, canSelectMany: false,
                    openLabel: "Create the project here",
                    defaultUri: message.folder && fs.existsSync(message.folder) ? vscode.Uri.file(message.folder) : undefined,
                });
                if (picked?.[0]) this.post({ type: "folder", folder: picked[0].fsPath });
                return;
            }
            case "validate":
                this.post({ type: "errors", errors: this.errors(message.form) });
                return;
            case "create":
                await this.create(message.form);
                return;
            case "open":
                await this.open(message.mode);
                return;
        }
    }

    private async sendState(): Promise<void> {
        this.post({ type: "busy", text: "Checking the .NET SDK and the redb templates..." });
        const [sdks, ...latest] = await Promise.all([
            installedSdks(),
            ...TEMPLATE_PACKAGE_IDS.map(id => latestTemplateVersion(id)),
        ]);
        const packages = TEMPLATE_PACKAGE_IDS.map((id, i) => ({ id, state: templateState(installedTemplatePackage(id), latest[i]) }));
        const templates: RedbTemplate[] = [];
        for (const { id, state } of packages) {
            if (state.kind === "missing") continue;
            try {
                templates.push(...selectTemplates(readTemplateDefinitions(state.installed.mountPoint), id));
            } catch (error) {
                this.output.appendLine(`Cannot read ${id} at ${state.installed.mountPoint}: ${(error as Error).message}`);
            }
        }
        this.templates = orderTemplates(templates);
        const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? os.homedir();
        this.post({
            type: "state",
            state: pageState(sdks === null ? null : sdks.map(s => s.major),
                packages.map(p => pagePackage(p.id, p.state)), this.templates, folder),
        });
    }

    private errors(form: CreateForm): Record<string, string> {
        const target = path.join(form.folder, form.name);
        const nonEmpty = form.folder.trim() !== "" && form.name.trim() !== ""
            && fs.existsSync(target) && fs.readdirSync(target).length > 0;
        return formErrors(form, this.templates.find(t => t.shortName === form.template), nonEmpty);
    }

    private async create(form: CreateForm): Promise<void> {
        const errors = this.errors(form);
        if (Object.keys(errors).length > 0) {
            this.post({ type: "errors", errors });
            return;
        }
        const template = this.templates.find(t => t.shortName === form.template)!;
        const target = path.join(form.folder, form.name);
        const args = dotnetNewArguments({
            shortName: template.shortName, name: form.name, outputDirectory: target,
            parameters: template.parameters, values: new Map(Object.entries(form.values)),
        });
        this.created = undefined;
        const code = await this.run(`Creating ${form.name}`, "dotnet", args, form.folder);
        if (code !== 0) {
            this.post({ type: "done", ok: false, message: code === null ? "Cancelled." : `dotnet new failed (exit code ${code}).` });
            return;
        }
        if (fs.existsSync(path.join(target, ".config", "dotnet-tools.json"))) {
            const restored = await this.run("Restoring the project's .NET tools", "dotnet", ["tool", "restore"], target);
            if (restored !== 0)
                this.log("The project was created, but 'dotnet tool restore' failed. Run it in the project folder.");
        }
        this.created = { folder: target, name: form.name };
        this.post({ type: "done", ok: true, message: `Created ${target}.` });
    }

    private async open(mode: "here" | "new" | "add"): Promise<void> {
        if (!this.created) return;
        const uri = vscode.Uri.file(this.created.folder);
        if (mode === "add")
            vscode.workspace.updateWorkspaceFolders(vscode.workspace.workspaceFolders?.length ?? 0, 0, { uri, name: this.created.name });
        else
            await vscode.commands.executeCommand("vscode.openFolder", uri, { forceNewWindow: mode === "new" });
    }

    private log(text: string): void {
        this.output.append(text.endsWith("\n") ? text : text + "\n");
        this.post({ type: "log", text });
    }

    /** Runs one process, its output in the page and the Output channel; the exit code, or null when cancelled or not started. */
    private run(title: string, executable: string, args: string[], cwd?: string): Promise<number | null> {
        this.post({ type: "busy", text: `${title}...` });
        this.log(`> ${displayCommand(executable, args)}`);
        this.cancelled = false;
        return new Promise(resolve => {
            const child = spawn(executable, args, { cwd, windowsHide: true });
            this.running = child;
            child.stdout?.on("data", (chunk: Buffer) => this.log(chunk.toString()));
            child.stderr?.on("data", (chunk: Buffer) => this.log(chunk.toString()));
            child.on("error", error => {
                this.log(error.message);
                this.running = undefined;
                resolve(null);
            });
            child.on("close", code => {
                this.log(`(exit code ${code ?? "none"})`);
                this.running = undefined;
                this.post({ type: "idle" });
                resolve(this.cancelled ? null : code);
            });
        });
    }
}

function html(webview: vscode.Webview, extensionUri: vscode.Uri): string {
    const css = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", "newproject.css"));
    const js = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", "newproject.js"));
    const nonce = Array.from({ length: 4 }, () => Math.random().toString(36).slice(2)).join("");
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; img-src ${webview.cspSource}; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<link rel="stylesheet" href="${css}">
<title>New redb Project</title>
</head>
<body>
<main id="root"></main>
<script nonce="${nonce}" src="${js}"></script>
</body>
</html>`;
}
