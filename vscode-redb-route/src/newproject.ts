/**
 * The pure half of the New Project page: what the page shows for the machine and each template,
 * how the form is checked, and the `dotnet new` command line built from it. The VS Code half
 * (the webview, the processes) stays thin.
 */

import { TemplateState } from "./dotnetenv";
import { RedbTemplate, TEMPLATE_GROUPS, TemplateGroup, TemplateParameter, DEFAULT_TEMPLATE } from "./templates";

/** A project name `dotnet new -n` accepts and that is a usable folder and assembly name. */
export function projectNameError(name: string): string | null {
    if (!name.trim())
        return "Enter a project name.";
    if (!/^[A-Za-z_][A-Za-z0-9_.-]*$/.test(name))
        return "Use letters, digits, '.', '_' and '-', starting with a letter or '_'.";
    if (name.endsWith("."))
        return "The name cannot end with '.'.";
    if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)/i.test(name))
        return `'${name}' is a reserved device name on Windows.`;
    return null;
}

export interface PromptItem {
    label: string;
    description?: string;
    /** The value passed to the template. */
    value: string;
    picked: boolean;
}

/** How one parameter is asked: a pick list for choice and bool, an input box for the rest. */
export type ParameterPrompt =
    | { kind: "pick"; parameter: TemplateParameter; title: string; placeholder: string; items: PromptItem[] }
    | { kind: "input"; parameter: TemplateParameter; title: string; placeholder: string; value: string };

export function parameterPrompt(parameter: TemplateParameter): ParameterPrompt {
    const title = parameter.displayName ?? parameter.name;
    const placeholder = parameter.description ?? title;
    if (parameter.kind === "choice") {
        const fallback = parameter.defaultValue ?? parameter.choices[0]?.value;
        return {
            kind: "pick", parameter, title, placeholder,
            items: parameter.choices.map(choice => ({
                label: choice.value,
                description: choice.description ?? undefined,
                value: choice.value,
                picked: choice.value === fallback,
            })),
        };
    }
    if (parameter.kind === "bool") {
        const current = (parameter.defaultValue ?? "false").toLowerCase() === "true";
        return {
            kind: "pick", parameter, title, placeholder,
            items: [
                { label: "Yes", value: "true", picked: current },
                { label: "No", value: "false", picked: !current },
            ],
        };
    }
    // An empty default means the template computes the value itself (the agreed `context`):
    // the box stays empty and an untouched box passes nothing.
    return {
        kind: "input", parameter, title,
        placeholder: parameter.defaultValue ? placeholder : `${placeholder} (leave empty for the template's default)`,
        value: parameter.defaultValue ?? "",
    };
}

/**
 * Marks target frameworks (`netX.Y`) the machine has no SDK for. The item stays selectable:
 * the project can be created and built elsewhere, but the user sees it before choosing.
 */
export function markMissingFrameworks(items: PromptItem[], sdkMajors: number[]): PromptItem[] {
    return items.map(item => {
        const match = /^net(\d+)\.\d+$/.exec(item.value);
        if (!match || sdkMajors.some(major => major >= Number(match[1])))
            return item;
        const note = "no .NET SDK for it on this machine";
        return { ...item, description: item.description ? `${item.description}, ${note}` : note };
    });
}

/** Problems with a typed value for a text parameter: required and numeric types. */
export function parameterValueError(parameter: TemplateParameter, value: string): string | null {
    if (!value) return parameter.isRequired ? `${parameter.displayName ?? parameter.name} is required.` : null;
    if ((parameter.datatype === "int" || parameter.datatype === "integer") && !/^-?\d+$/.test(value))
        return "Enter a whole number.";
    if ((parameter.datatype === "float" || parameter.datatype === "double") && !/^-?\d+(\.\d+)?$/.test(value))
        return "Enter a number.";
    return null;
}

export interface NewProjectRequest {
    shortName: string;
    name: string;
    /** The folder the project is created in (`-o`), the parent folder joined with the name. */
    outputDirectory: string;
    parameters: TemplateParameter[];
    /** The answers by parameter name; a missing or empty answer is not passed. */
    values: Map<string, string>;
}

/**
 * The arguments of `dotnet new`, for execFile (no shell, so a path with spaces is one argument).
 * A value equal to the template's default is left out: the command reads as what the user chose.
 */
export function dotnetNewArguments(request: NewProjectRequest): string[] {
    const args = ["new", request.shortName, "-n", request.name, "-o", request.outputDirectory];
    for (const parameter of request.parameters) {
        const value = request.values.get(parameter.name);
        if (value === undefined || value === "")
            continue;
        if (parameter.defaultValue !== null && value === parameter.defaultValue && !parameter.isRequired)
            continue;
        args.push(`--${parameter.name}`, value);
    }
    return args;
}

/** The command as the Output channel shows it, quoted where a shell would need it. */
export function displayCommand(executable: string, args: string[]): string {
    return [executable, ...args].map(a => /[\s"]/.test(a) ? `"${a.replace(/"/g, '\\"')}"` : a).join(" ");
}

// ── the page ─────────────────────────────────────────────────────────

/** One form field of a template on the page: a select for choice and bool, a text box for the rest. */
export interface PageField {
    name: string;
    title: string;
    description: string | null;
    kind: "select" | "text";
    options: { value: string; label: string; description: string | null }[];
    /** The preselected option or the text box's initial value. */
    value: string;
    placeholder: string;
}

/** The glyphs the page draws on template cards (media/newproject.js holds their paths). */
export type TemplateIcon =
    | "route" | "stream" | "queue" | "database" | "api" | "envelope" | "split" | "fan" | "chat" | "shield"
    | "browser" | "server" | "check" | "cube" | "terminal" | "document";

/** Words of a template's short name (or classifications) and the glyph they call for; the first match wins. */
const ICON_WORDS: [RegExp, TemplateIcon][] = [
    [/^as4$/, "envelope"],
    [/^kafka$/, "stream"],
    [/^(rabbitmq|amqp|mqtt|mq|sqs|asb)$/, "queue"],
    [/^(sql|db|database|outbox|batch)$/, "database"],
    [/^(rest|http|api|grpc|soap)$/, "api"],
    [/^(scatter|gather)$/, "fan"],
    [/^(eip|patterns|integration)$/, "split"],
    [/^(chat|llm|ai)$/, "chat"],
    [/^identity$/, "shield"],
    [/^(validator|validation|xslt)$/, "check"],
    [/^beans?$/, "cube"],
    [/^(blazor|razor|app|bff|web)$/, "browser"],
    [/^(worker|service)$/, "server"],
    [/^console$/, "terminal"],
];

/**
 * The glyph of a template card: from the words of its short name first ("redb-route-kafka"
 * gives kafka); an XML module without such a word gets the route glyph; other templates then
 * look at their classifications, and fall back to a server for a Tsak host and a document for
 * the rest.
 */
export function templateIcon(template: Pick<RedbTemplate, "shortName" | "classifications" | "group">): TemplateIcon {
    const words = template.shortName.toLowerCase().split(/[-_.]/).filter(w => w !== "redb" && w !== "route" && w !== "tsak");
    const match = (source: string[]) => {
        for (const word of source)
            for (const [pattern, icon] of ICON_WORDS)
                if (pattern.test(word)) return icon;
        return null;
    };
    return match(words)
        ?? (template.group === "route-xml" ? "route" : null)
        ?? match(template.classifications.map(c => c.toLowerCase()))
        ?? (template.group === "tsak" ? "server" : "document");
}

export interface PageTemplate {
    icon: TemplateIcon;
    shortName: string;
    name: string;
    description: string | null;
    type: string;
    group: TemplateGroup;
    packageId: string;
    fields: PageField[];
}

export interface PagePackage {
    packageId: string;
    kind: TemplateState["kind"];
    installed: string | null;
    latest: string | null;
}

/** Everything the page renders, sent to the webview as one message. */
export interface PageState {
    /** The installed SDK majors; null when `dotnet` is not on the PATH. */
    sdks: number[] | null;
    packages: PagePackage[];
    groups: { group: TemplateGroup; title: string }[];
    templates: PageTemplate[];
    /** The template selected when the page opens. */
    selected: string | null;
    folder: string;
}

export function pagePackage(packageId: string, state: TemplateState): PagePackage {
    return {
        packageId,
        kind: state.kind,
        installed: state.kind === "missing" ? null : state.installed.version,
        latest: state.kind === "current" ? null : state.latest,
    };
}

export function pageField(parameter: TemplateParameter, sdkMajors: number[]): PageField {
    const prompt = parameterPrompt(parameter);
    if (prompt.kind === "pick") {
        const items = markMissingFrameworks(prompt.items, sdkMajors);
        return {
            name: parameter.name, title: prompt.title, description: parameter.description, kind: "select",
            options: items.map(i => ({ value: i.value, label: i.label, description: i.description ?? null })),
            value: items.find(i => i.picked)?.value ?? items[0]?.value ?? "",
            placeholder: "",
        };
    }
    return {
        name: parameter.name, title: prompt.title, description: parameter.description, kind: "text",
        options: [], value: prompt.value, placeholder: prompt.placeholder,
    };
}

/** The page state; the groups listed are only those that have templates. */
export function pageState(sdks: number[] | null, packages: PagePackage[], templates: RedbTemplate[], folder: string): PageState {
    const majors = sdks ?? [];
    const groups = TEMPLATE_GROUPS.filter(g => templates.some(t => t.group === g.group));
    return {
        sdks, packages, groups, folder,
        templates: templates.map(t => ({
            icon: templateIcon(t),
            shortName: t.shortName, name: t.name, description: t.description, type: t.type, group: t.group,
            packageId: t.packageId, fields: t.parameters.map(p => pageField(p, majors)),
        })),
        selected: templates.find(t => t.shortName === DEFAULT_TEMPLATE)?.shortName ?? templates[0]?.shortName ?? null,
    };
}

/** What the page sends when Create is pressed. */
export interface CreateForm {
    template: string;
    name: string;
    folder: string;
    values: Record<string, string>;
}

/**
 * The form's problems keyed by field ("template", "name", "folder" or a parameter name); empty
 * when the project can be created. `targetIsNonEmpty` tells whether <folder>/<name> already
 * holds files.
 */
export function formErrors(form: CreateForm, template: RedbTemplate | undefined, targetIsNonEmpty: boolean): Record<string, string> {
    const errors: Record<string, string> = {};
    if (!template) {
        errors.template = "Choose a template.";
        return errors;
    }
    const nameError = projectNameError(form.name);
    if (nameError) errors.name = nameError;
    if (!form.folder.trim()) errors.folder = "Choose the folder to create the project in.";
    else if (!nameError && targetIsNonEmpty) errors.folder = `${form.name} already exists in this folder and is not empty.`;
    for (const parameter of template.parameters) {
        const value = form.values[parameter.name] ?? "";
        const error = parameter.kind === "choice"
            ? (value && !parameter.choices.some(c => c.value === value) ? "Choose one of the listed values." : null)
            : parameter.kind === "bool"
                ? (value && value !== "true" && value !== "false" ? "Choose yes or no." : null)
                : parameterValueError(parameter, value);
        if (error) errors[parameter.name] = error;
    }
    return errors;
}
