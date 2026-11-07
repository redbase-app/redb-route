// The New redb Project page. It renders what the extension sends ("state", "errors", "log", ...)
// and sends every action back as a message; it never runs anything itself. All text goes in
// through textContent, never as markup.
(function () {
    "use strict";
    const vscode = acquireVsCodeApi();
    const root = document.getElementById("root");

    let state = null;           // the PageState from the extension
    let selected = null;        // short name of the chosen template
    let form = { name: "", folder: "", values: {} };
    let errors = {};
    let busy = null;            // text of the running step, or null
    let done = null;            // { ok, message } after a run
    const logLines = [];
    let validateTimer = 0;

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    // 24x24 line glyphs of the template cards, drawn in the card's color (templateIcon picks one).
    const GLYPHS = {
        route: "M2.5 12h5M15.5 12h3M9 8.5h6v7H9zM18 9l3.5 3-3.5 3z",
        stream: "M3 6h14M14 3l3 3-3 3M3 12h14M14 9l3 3-3 3M3 18h14M14 15l3 3-3 3",
        queue: "M2.5 8h4.5v8H2.5zM9.75 8h4.5v8h-4.5zM17 8h4.5v8H17z",
        database: "M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3zM4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3",
        api: "M8 4c-2 0-3 1-3 3v2c0 1.5-1 3-2 3 1 0 2 1.5 2 3v2c0 2 1 3 3 3M16 4c2 0 3 1 3 3v2c0 1.5 1 3 2 3-1 0-2 1.5-2 3v2c0 2-1 3-3 3",
        envelope: "M3 6h18v12H3zM3 6l9 7 9-7",
        split: "M3 12h5M8 12l5-6h5M8 12l5 6h5M16 3.5l2.5 2.5-2.5 2.5M16 15.5l2.5 2.5-2.5 2.5",
        fan: "M2.5 12h3.5l5-6h2l5 6h3.5M6 12h12M6 12l5 6h2l5-6",
        chat: "M4 5h16v10H10l-5 4v-4H4z",
        shield: "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6zM9 12l2 2 4-4",
        browser: "M3 5h18v14H3zM3 9h18M6 7h.01M8.5 7h.01",
        server: "M4 4h16v6H4zM4 14h16v6H4zM7.5 7h.01M7.5 17h.01M11 7h5M11 17h5",
        check: "M6 3h9l4 4v14H6zM15 3v4h4M9 14l2 2 4-4",
        cube: "M12 3l8 4.5v9L12 21l-8-4.5v-9zM12 12l8-4.5M12 12v9M12 12L4 7.5",
        terminal: "M3 5h18v14H3zM7 10l3 2-3 2M12 15h5",
        document: "M6 3h9l4 4v14H6zM15 3v4h4M9 12h6M9 16h6",
    };

    function glyph(name) {
        const ns = "http://www.w3.org/2000/svg";
        const svg = document.createElementNS(ns, "svg");
        svg.setAttribute("viewBox", "0 0 24 24");
        svg.setAttribute("class", "card-icon");
        svg.setAttribute("aria-hidden", "true");
        const path = document.createElementNS(ns, "path");
        path.setAttribute("d", GLYPHS[name] || GLYPHS.document);
        svg.appendChild(path);
        return svg;
    }

    function template() {
        return state && state.templates.find(function (t) { return t.shortName === selected; });
    }

    function select(shortName) {
        selected = shortName;
        const t = template();
        form.values = {};
        if (t) t.fields.forEach(function (f) { form.values[f.name] = f.value; });
        done = null;
        render();
        validate();
    }

    function send(message) { vscode.postMessage(message); }

    function currentForm() {
        return { template: selected || "", name: form.name, folder: form.folder, values: form.values };
    }

    function validate() {
        clearTimeout(validateTimer);
        validateTimer = setTimeout(function () { send({ type: "validate", form: currentForm() }); }, 200);
    }

    // ── machine status ───────────────────────────────────────────────

    function renderStatus() {
        const bar = el("section", "status");
        const sdk = el("div", "status-row");
        if (state.sdks === null) {
            sdk.appendChild(el("span", "badge bad", ".NET SDK"));
            sdk.appendChild(el("span", "note", "not found on the PATH. Creating a project needs it."));
            const b = el("button", "secondary", "Download .NET");
            b.addEventListener("click", function () { send({ type: "downloadDotnet" }); });
            sdk.appendChild(b);
        } else {
            sdk.appendChild(el("span", "badge ok", ".NET SDK"));
            const majors = state.sdks.slice().sort(function (a, b) { return a - b; })
                .filter(function (m, i, all) { return all.indexOf(m) === i; });
            sdk.appendChild(el("span", "note", majors.length ? majors.map(function (m) { return m + ".0"; }).join(", ") : "none"));
        }
        bar.appendChild(sdk);

        state.packages.forEach(function (p) {
            const row = el("div", "status-row");
            if (p.kind === "missing") {
                row.appendChild(el("span", "badge bad", p.packageId));
                row.appendChild(el("span", "note", "not installed for dotnet new"));
                const b = el("button", "", "Install" + (p.latest ? " " + p.latest : ""));
                b.disabled = !!busy || state.sdks === null;
                b.addEventListener("click", function () { send({ type: "install", packageId: p.packageId, version: p.latest }); });
                row.appendChild(b);
            } else if (p.kind === "outdated") {
                row.appendChild(el("span", "badge warn", p.packageId));
                row.appendChild(el("span", "note", p.installed + " installed, " + p.latest + " available"));
                const b = el("button", "secondary", "Update to " + p.latest);
                b.disabled = !!busy;
                b.addEventListener("click", function () { send({ type: "install", packageId: p.packageId, version: p.latest }); });
                row.appendChild(b);
            } else {
                row.appendChild(el("span", "badge ok", p.packageId));
                row.appendChild(el("span", "note", p.installed ? p.installed : "installed from a folder"));
            }
            bar.appendChild(row);
        });

        const refresh = el("button", "link", "Refresh");
        refresh.disabled = !!busy;
        refresh.addEventListener("click", function () { send({ type: "refresh" }); });
        bar.appendChild(refresh);
        return bar;
    }

    // ── templates ────────────────────────────────────────────────────

    function renderTemplates() {
        const list = el("section", "templates");
        if (state.templates.length === 0) {
            list.appendChild(el("p", "empty", "No redb templates are installed. Install the template packages above."));
            return list;
        }
        state.groups.forEach(function (g) {
            list.appendChild(el("h2", null, g.title));
            const grid = el("div", "cards");
            state.templates.filter(function (t) { return t.group === g.group; }).forEach(function (t) {
                const card = el("button", "card group-" + t.group + (t.shortName === selected ? " selected" : ""));
                card.setAttribute("aria-pressed", t.shortName === selected ? "true" : "false");
                card.appendChild(glyph(t.icon));
                const text = el("span", "card-text");
                text.appendChild(el("span", "card-title", t.name));
                text.appendChild(el("span", "card-name", t.shortName + (t.type === "solution" ? "  ·  solution" : "")));
                if (t.description) text.appendChild(el("span", "card-description", t.description));
                card.appendChild(text);
                card.addEventListener("click", function () { select(t.shortName); });
                grid.appendChild(card);
            });
            list.appendChild(grid);
        });
        return list;
    }

    // ── form ─────────────────────────────────────────────────────────

    function field(label, control, error, hint) {
        const wrap = el("label", "field");
        wrap.appendChild(el("span", "field-label", label));
        wrap.appendChild(control);
        if (hint) wrap.appendChild(el("span", "field-hint", hint));
        if (error) {
            wrap.appendChild(el("span", "field-error", error));
            control.classList.add("invalid");
        }
        return wrap;
    }

    function textInput(key, value, placeholder, onInput) {
        const input = el("input");
        input.type = "text";
        input.dataset.key = key;
        input.value = value;
        if (placeholder) input.placeholder = placeholder;
        input.disabled = !!busy;
        input.addEventListener("input", function () { onInput(input.value); validate(); });
        return input;
    }

    function renderForm() {
        const t = template();
        const panel = el("section", "form");
        if (!t) {
            panel.appendChild(el("p", "empty", "Choose a template."));
            return panel;
        }
        panel.appendChild(el("h2", null, t.name));
        if (t.description) panel.appendChild(el("p", "description", t.description));

        panel.appendChild(field("Project name", textInput("name", form.name, "MyProject", function (v) { form.name = v; }), form.name ? errors.name : null));

        t.fields.forEach(function (f) {
            let control;
            if (f.kind === "select") {
                control = el("select");
                f.options.forEach(function (o) {
                    const option = el("option", null, o.description ? o.label + "  (" + o.description + ")" : o.label);
                    option.value = o.value;
                    option.selected = form.values[f.name] === o.value;
                    control.appendChild(option);
                });
                control.disabled = !!busy;
                control.addEventListener("change", function () { form.values[f.name] = control.value; validate(); });
            } else {
                control = textInput("param:" + f.name, form.values[f.name] || "", f.placeholder, function (v) { form.values[f.name] = v; });
            }
            panel.appendChild(field(f.title, control, errors[f.name], f.kind === "select" ? f.description : null));
        });

        const folderRow = el("div", "folder-row");
        const folder = textInput("folder", form.folder, "", function (v) { form.folder = v; });
        folderRow.appendChild(folder);
        const browse = el("button", "secondary", "Browse...");
        browse.type = "button";
        browse.disabled = !!busy;
        browse.addEventListener("click", function (e) { e.preventDefault(); send({ type: "browse", folder: form.folder }); });
        folderRow.appendChild(browse);
        const target = form.folder && form.name ? "Creates " + joinPath(form.folder, form.name) : null;
        panel.appendChild(field("Location", folderRow, errors.folder, target));

        const actions = el("div", "actions");
        const create = el("button", "", "Create");
        create.disabled = !!busy || Object.keys(errors).length > 0 || !form.name || state.sdks === null;
        create.addEventListener("click", function () { done = null; send({ type: "create", form: currentForm() }); });
        actions.appendChild(create);
        panel.appendChild(actions);
        return panel;
    }

    function joinPath(folder, name) {
        const separator = folder.indexOf("\\") >= 0 ? "\\" : "/";
        return folder.replace(/[\\/]+$/, "") + separator + name;
    }

    // ── progress ─────────────────────────────────────────────────────

    function renderProgress() {
        const box = el("section", "progress");
        if (busy) {
            const row = el("div", "busy");
            row.appendChild(el("span", "spinner"));
            row.appendChild(el("span", null, busy));
            const cancel = el("button", "link", "Cancel");
            cancel.addEventListener("click", function () { send({ type: "cancel" }); });
            row.appendChild(cancel);
            box.appendChild(row);
        }
        if (done) {
            const row = el("div", done.ok ? "result ok" : "result bad");
            row.appendChild(el("span", null, done.message));
            if (done.ok) {
                [["Open", "here"], ["Open in New Window", "new"], ["Add to Workspace", "add"]].forEach(function (pair, i) {
                    const b = el("button", i === 0 ? "" : "secondary", pair[0]);
                    b.addEventListener("click", function () { send({ type: "open", mode: pair[1] }); });
                    row.appendChild(b);
                });
            } else {
                const b = el("button", "secondary", "Show Output");
                b.addEventListener("click", function () { send({ type: "showOutput" }); });
                row.appendChild(b);
            }
            box.appendChild(row);
        }
        if (logLines.length) box.appendChild(el("pre", "log", logLines.join("")));
        return box;
    }

    function render() {
        const focused = document.activeElement && document.activeElement.tagName === "INPUT" ? document.activeElement : null;
        const caret = focused ? [focused.selectionStart, focused.selectionEnd, focused.dataset.key] : null;
        root.textContent = "";
        root.appendChild(el("h1", null, "New redb Project"));
        if (!state) {
            root.appendChild(el("p", "note", busy || "Loading..."));
            return;
        }
        root.appendChild(renderStatus());
        const main = el("div", "main");
        main.appendChild(renderTemplates());
        main.appendChild(renderForm());
        root.appendChild(main);
        root.appendChild(renderProgress());
        // Re-rendering replaces the inputs; keep the one being typed in focused.
        if (caret) {
            const again = Array.prototype.find.call(root.querySelectorAll("input"), function (i) { return i.dataset.key === caret[2]; });
            if (again) { again.focus(); again.setSelectionRange(caret[0], caret[1]); }
        }
        const log = root.querySelector(".log");
        if (log) log.scrollTop = log.scrollHeight;
    }

    window.addEventListener("message", function (event) {
        const message = event.data;
        switch (message.type) {
            case "state":
                state = message.state;
                busy = null;
                if (!form.folder) form.folder = state.folder;
                if (!state.templates.some(function (t) { return t.shortName === selected; })) {
                    selected = null;
                    if (state.selected) { select(state.selected); return; }
                }
                render();
                validate();
                break;
            case "busy": busy = message.text; render(); break;
            case "idle": busy = null; render(); break;
            case "log":
                logLines.push(message.text.endsWith("\n") ? message.text : message.text + "\n");
                render();
                break;
            case "errors": errors = message.errors || {}; render(); break;
            case "folder": form.folder = message.folder; render(); validate(); break;
            case "done": done = { ok: message.ok, message: message.message }; busy = null; render(); break;
        }
    });

    send({ type: "ready" });
}());
