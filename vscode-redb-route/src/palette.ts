/**
 * The insert palette (10-VSCODE §8.5), assembled MECHANICALLY: EIP steps from the generated
 * element list, endpoint senders from the Ф4 catalog. Templates carry the required
 * attributes plus the element's primary one (uri/expr) as empty stubs — the properties
 * panel fills them right after the insert.
 */

import { ElementInfo } from "./graphmodel";
import { Category, categoryOf } from "./category";

export interface PaletteItem {
    label: string;
    fragment: string;
    hint?: string;
}

export interface PaletteCategory {
    title: string;
    items: PaletteItem[];
}

const SPECIAL_FRAGMENTS: Record<string, string> = {
    choice: `<choice>\n  <when expr="">\n  </when>\n  <otherwise>\n  </otherwise>\n</choice>`,
    tryCatch: `<tryCatch>\n  <try>\n  </try>\n  <catch exception="System.Exception">\n  </catch>\n</tryCatch>`,
};

const CATEGORY_TITLES: [Category, string][] = [
    ["flow", "Flow control"],
    ["transform", "Transform"],
    ["errors", "Errors & reliability"],
];

export function stepFragment(info: ElementInfo): string {
    const special = SPECIAL_FRAGMENTS[info.name];
    if (special) return special;

    const stubs = info.attributes.filter(a => a.required).map(a => a.name);
    if (stubs.length === 0) {
        const primary = info.attributes.find(a => a.name === "uri")
            ?? info.attributes.find(a => a.type === "Expression");
        if (primary) stubs.push(primary.name);
    }
    const attributes = stubs.map(name => ` ${name}=""`).join("");

    return info.kind === "Scope"
        ? `<${info.name}${attributes}>\n</${info.name}>`
        : `<${info.name}${attributes}/>`;
}

export function buildPalette(
    elements: ElementInfo[],
    schemes: { scheme: string; pathSynonym?: string | null }[]): PaletteCategory[] {
    const byCategory = new Map<Category, PaletteItem[]>();
    for (const info of elements) {
        if (info.kind !== "Step" && info.kind !== "Scope" && info.kind !== "Branching") continue;
        const category = categoryOf(info.name) ?? "flow";
        const bucket = byCategory.get(category) ?? [];
        bucket.push({ label: info.name, fragment: stepFragment(info) });
        byCategory.set(category, bucket);
    }

    const categories: PaletteCategory[] = [];
    for (const [category, title] of CATEGORY_TITLES) {
        const items = (byCategory.get(category) ?? []).sort((a, b) => a.label.localeCompare(b.label));
        if (items.length > 0) categories.push({ title, items });
    }

    categories.push({
        title: "Send to (endpoints)",
        items: [...schemes]
            .sort((a, b) => a.scheme.localeCompare(b.scheme))
            .map(component => ({
                label: component.scheme,
                fragment: `<to uri="${component.scheme}://"/>`,
                hint: component.pathSynonym ? `path = ${component.pathSynonym}` : undefined,
            })),
    });
    return categories;
}
