// The webview side of the Ф8 graph: render, selection, the properties panel (§8) and the
// four editing operations (§8.4) — insert via slots+palette, Delete, drag to reorder, drag
// into/out of a bracket. Every edit is a message; the document state lives in the text
// editor, never here.
(function () {
    "use strict";
    const vscode = acquireVsCodeApi();
    const root = document.getElementById("root");
    const panel = document.getElementById("panel");
    // The shell may be a version older than this script (an extension update with the
    // window not yet reloaded) — never die on a missing container, build it.
    let overlay = document.getElementById("overlay");
    if (!overlay) {
        overlay = document.createElement("div");
        overlay.id = "overlay";
        document.body.appendChild(overlay);
    }

    // ── tiles: one size for every step, a glyph and a short caption ──────
    //
    // The glyphs follow the Enterprise Integration Patterns notation (G. Hohpe, B. Woolf,
    // enterpriseintegrationpatterns.com, CC BY 4.0) redrawn as 24×24 line icons; the steps the
    // notation has no symbol for (set, log, delay, try…) are drawn in the same style. The stroke
    // takes currentColor, the tone class gives the color, so the theme decides light or dark.
    const GLYPHS = {
        consumer: '<path d="M3 12h11"/><path d="M10 8l4 4-4 4"/><path d="M18 4v16"/>',
        endpoint: '<rect x="3" y="6" width="9" height="12" rx="1.5"/><path d="M12 12h9"/><path d="M18 9l3 3-3 3"/>',
        direct: '<path d="M3 12h16"/><path d="M15 8l4 4-4 4"/><circle cx="4" cy="12" r="1.5"/>',
        channel: '<rect x="2" y="8" width="20" height="8" rx="4"/><path d="M7 12h8"/><path d="M13 10l2 2-2 2"/>',
        http: '<circle cx="12" cy="12" r="9"/><ellipse cx="12" cy="12" rx="4" ry="9"/><path d="M3 12h18"/>',
        database: '<ellipse cx="12" cy="6" rx="7" ry="3"/><path d="M5 6v12c0 1.7 3.1 3 7 3s7-1.3 7-3V6"/><path d="M5 12c0 1.7 3.1 3 7 3s7-1.3 7-3"/>',
        activator: '<circle cx="12" cy="12" r="3"/><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M5.6 18.4l2.1-2.1M16.3 7.7l2.1-2.1"/>',
        tag: '<path d="M3 12V4h8l10 10-7 7z"/><circle cx="7.5" cy="8.5" r="1.5"/>',
        document: '<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v4h4"/><path d="M9 12h6M9 16h6"/>',
        template: '<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v4h4"/><path d="M10.5 11c-1 0-1 1.2-1 2s-.5 1-1 1c.5 0 1 .2 1 1s0 2 1 2M13.5 11c1 0 1 1.2 1 2s.5 1 1 1c-.5 0-1 .2-1 1s0 2-1 2"/>',
        translator: '<path d="M3 8h15"/><path d="M15 5l3 3-3 3"/><path d="M21 16H6"/><path d="M9 13l-3 3 3 3"/>',
        json: '<path d="M8 4c-2 0-2 2-2 4s-1 3-3 4c2 1 3 2 3 4s0 4 2 4"/><path d="M16 4c2 0 2 2 2 4s1 3 3 4c-2 1-3 2-3 4s0 4-2 4"/>',
        router: '<path d="M12 3l9 9-9 9-9-9z"/><path d="M8 12h4l3-3M12 12l3 3"/>',
        when: '<path d="M4 19V11a4 4 0 0 1 4-4h11"/><path d="M16 4l3 3-3 3"/><circle cx="4" cy="19" r="1.5"/>',
        otherwise: '<path d="M3 12h17"/><path d="M16 8l4 4-4 4"/><path d="M7 8v8" stroke-dasharray="2 2"/>',
        filter: '<path d="M3 5h18l-7 8v6l-4 2v-8z"/>',
        loop: '<path d="M20 12a8 8 0 1 1-2.4-5.7"/><path d="M20 4v4h-4"/>',
        clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
        history: '<path d="M3.5 12a8.5 8.5 0 1 0 2.5-6"/><path d="M3 4v4h4"/><path d="M12 8v4l3 2"/>',
        gauge: '<path d="M4 17a8 8 0 1 1 16 0"/><path d="M12 17l4-5"/><path d="M4 17h16"/>',
        log: '<rect x="5" y="3" width="14" height="18" rx="2"/><path d="M8 8h8M8 12h8M8 16h5"/>',
        shield: '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
        warning: '<path d="M12 3l10 18H2z"/><path d="M12 10v5"/><path d="M12 18v.5"/>',
        flag: '<path d="M5 21V4"/><path d="M5 4h12l-2.5 4L17 12H5"/>',
        bolt: '<path d="M13 2L4 14h7l-1 8 9-12h-7z"/>',
        stop: '<rect x="5" y="5" width="14" height="14" rx="2"/>',
        splitter: '<path d="M3 12h6"/><path d="M9 12l9-7M9 12h9M9 12l9 7"/><path d="M15 4l3 1-1 3M15 11l3 1-3 1M15 20l3-1-1-3"/>',
        aggregator: '<path d="M3 5l9 7M3 12h9M3 19l9-7"/><path d="M12 12h9"/><path d="M18 9l3 3-3 3"/>',
        recipients: '<path d="M3 12h5"/><path d="M8 12l8-6M8 12h8M8 12l8 6"/><circle cx="18" cy="6" r="2"/><circle cx="18" cy="12" r="2"/><circle cx="18" cy="18" r="2"/>',
        wiretap: '<path d="M3 9h18"/><path d="M18 6l3 3-3 3"/><path d="M11 9v11"/><path d="M8 17l3 3 3-3"/>',
        enricher: '<rect x="4" y="4" width="16" height="16" rx="2"/><path d="M12 8v8M8 12h8"/>',
        eraser: '<path d="M3 12V4h8l10 10-7 7z"/><path d="M8 13l5-5M8 8l5 5"/>',
        check: '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/><path d="M8.5 12l2.5 2.5 4.5-5"/>',
        lock: '<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>',
        idempotent: '<circle cx="12" cy="12" r="9"/><path d="M10 9l2-1.5V16"/>',
        throttle: '<path d="M4 17a8 8 0 1 1 16 0"/><path d="M12 17l-4-4"/><path d="M4 17h16"/>',
        route: '<circle cx="5" cy="12" r="2"/><circle cx="19" cy="12" r="2"/><path d="M7 12h10"/>',
        unknown: '<rect x="4" y="4" width="16" height="16" rx="2" stroke-dasharray="3 2"/><path d="M10 9.5a2 2 0 1 1 2.5 2c-.5.2-.5.8-.5 1.5M12 16v.5"/>',
    };

    /** The glyph and the color tone of a step; tones are the groups of the legend. */
    const STEP_LOOK = {
        from: ["consumer", "in"],
        choice: ["router", "route"], when: ["when", "route"], otherwise: ["otherwise", "route"],
        filter: ["filter", "route"], ofType: ["filter", "route"], loop: ["loop", "route"], split: ["splitter", "route"],
        aggregate: ["aggregator", "route"], multicast: ["recipients", "route"],
        recipientList: ["recipients", "route"], routingSlip: ["recipients", "route"],
        dynamicRouter: ["router", "route"], scatterGather: ["recipients", "route"],
        loadBalance: ["recipients", "route"], resequence: ["aggregator", "route"],
        idempotentConsumer: ["idempotent", "route"], throttle: ["throttle", "route"],
        debounce: ["throttle", "route"], sample: ["throttle", "route"], threads: ["recipients", "route"],
        delay: ["clock", "route"], stop: ["stop", "route"],
        setProperty: ["tag", "data"], setProperties: ["tag", "data"], setHeader: ["tag", "data"], setHeaders: ["tag", "data"],
        setBody: ["document", "data"], transform: ["translator", "data"], convertBody: ["translator", "data"],
        marshal: ["translator", "data"], unmarshal: ["translator", "data"], normalize: ["translator", "data"],
        transformJson: ["json", "data"], payload: ["template", "data"], xslt: ["translator", "data"],
        removeHeader: ["eraser", "data"], removeHeaders: ["eraser", "data"], removeProperty: ["eraser", "data"],
        removeProperties: ["eraser", "data"], removeBody: ["eraser", "data"], sort: ["translator", "data"],
        claimCheck: ["enricher", "data"], enrich: ["enricher", "out"], pollEnrich: ["enricher", "out"],
        wireTap: ["wiretap", "out"],
        log: ["log", "watch"], metered: ["gauge", "watch"], traced: ["history", "watch"],
        messageHistory: ["history", "watch"], streamCaching: ["document", "watch"],
        tryCatch: ["shield", "error"], try: ["shield", "error"], catch: ["warning", "error"],
        finally: ["flag", "error"], throwException: ["bolt", "error"], onException: ["warning", "error"],
        onCompletion: ["flag", "error"], circuitBreaker: ["shield", "error"], validate: ["check", "error"],
        validateJsonSchema: ["check", "error"], validateXsd: ["check", "error"],
        transaction: ["lock", "error"], saga: ["lock", "error"],
    };

    /** Transports by scheme — the tile of an endpoint step shows where the message goes. */
    const SCHEME_GLYPH = {
        bean: "activator", sql: "database", jdbc: "database", redb: "database",
        http: "http", https: "http", rest: "http", soap: "http", grpc: "http",
        direct: "direct", seda: "direct", vm: "direct", "direct-vm": "direct",
        kafka: "channel", amqp: "channel", rabbitmq: "channel", ibmmq: "channel", sqs: "channel",
        sns: "channel", azureservicebus: "channel", mqtt: "channel", redis: "channel", jms: "channel",
        file: "document", ftp: "document", sftp: "document", s3: "document",
        timer: "clock", cron: "clock", quartz: "clock",
    };

    /** The short caption under (or beside) the glyph: a word, not a sentence. */
    const CAPTIONS = {
        setProperty: "set", setProperties: "set", setHeader: "set hdr", setHeaders: "set hdr", setBody: "set body",
        removeHeader: "rm hdr", removeHeaders: "rm hdrs", removeProperty: "rm prop",
        removeProperties: "rm props", removeBody: "rm body", convertBody: "convert",
        transformJson: "json", payload: "template", messageHistory: "history",
        tryCatch: "try-catch", throwException: "throw", idempotentConsumer: "idempotent",
        recipientList: "recipients", validateJsonSchema: "validate", validateXsd: "validate",
        onException: "on error", onCompletion: "on done", pollEnrich: "enrich",
    };

    /** Glyph, tone and caption of a step: `type` is the element, `scheme` its transport. */
    function lookOf(type, scheme, category) {
        let look = STEP_LOOK[type];
        const endpoint = scheme && (type === "from" || type === "to" || type === "toD" ||
            type === "wireTap" || type === "enrich" || type === "pollEnrich");
        if (endpoint && type !== "from") {
            const tone = category === "userCode" ? "code" : category === "sendInternal" ? "inner" : "out";
            look = [SCHEME_GLYPH[scheme] || "endpoint", tone];
        } else if (!look) {
            look = category === "unknown" ? ["unknown", "muted"]
                : type === "to" || type === "toD" ? ["endpoint", "out"] : ["route", "muted"];
        }
        const caption = endpoint && type !== "from" ? scheme : (CAPTIONS[type] || type);
        return { glyph: look[0], tone: look[1], caption: caption };
    }

    function glyph(name) {
        const box = el("span", "glyph");
        box.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" ' +
            'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
            (GLYPHS[name] || GLYPHS.unknown) + "</svg>";
        return box;
    }

    /**
     * One tile: the glyph and the caption, the same size for every step. The details (the
     * property name, the condition, the SQL) live in the tooltip and the properties panel.
     */
    function tile(type, scheme, category, tooltip, extraClass) {
        const look = lookOf(type, scheme, category);
        const box = el("span", "tile tone-" + look.tone + (extraClass ? " " + extraClass : ""));
        box.title = tooltip || type;
        box.appendChild(glyph(look.glyph));
        box.appendChild(el("span", "cap", look.caption));
        return box;
    }

    /** The width of one tile in the current look, matches .tile / .tiles-inline .tile in graph.css. */
    function tileWidth() { return tileStyle === "inline" ? 108 : 66; }

    /** The element a branch label stands for: `when …` → when, `catch X` → catch. */
    function branchType(label, parentType) {
        const first = (label || "").split(" ", 1)[0];
        if (["when", "otherwise", "try", "catch", "finally"].includes(first)) return first;
        return parentType === "multicast" ? "multicast" : "when";
    }

    let selectedKey = null;   // "1,2,0" — survives full re-renders
    let selectedPath = null;
    let selectedGroup = null; // a run of assignments selected as one tile (its steps)

    // The properties panel is widened by dragging its left edge (owner, 2026-09-24: a dialog
    // would block clicking through the graph). The width is one choice for all documents.
    let panelWidth = 380;
    function applyPanelWidth() { document.body.style.setProperty("--panel-width", panelWidth + "px"); }
    applyPanelWidth();
    (function panelGrip() {
        const grip = document.createElement("div");
        grip.id = "panel-grip";
        grip.title = "drag to resize the panel";
        document.body.appendChild(grip);
        grip.addEventListener("mousedown", function (e) {
            e.preventDefault();
            document.body.classList.add("resizing-panel");
            function move(ev) {
                panelWidth = Math.round(Math.min(window.innerWidth * 0.75, Math.max(260, window.innerWidth - ev.clientX)));
                applyPanelWidth();
            }
            function up() {
                document.body.classList.remove("resizing-panel");
                window.removeEventListener("mousemove", move);
                window.removeEventListener("mouseup", up);
                vscode.postMessage({ type: "setPanelWidth", width: panelWidth });
                rerender(); // the snake re-chunks for the new width
            }
            window.addEventListener("mousemove", move);
            window.addEventListener("mouseup", up);
        });
    })();
    let paletteData = [];     // categories from the extension
    let dragKey = null;       // the path being dragged (string key)
    let dragPath = null;
    let toggled = new Set();  // explicit collapse/expand toggles (workspaceState, Р5)
    // Two layouts, one look: the snake left to right (§2) and the columns top down, both drawn
    // the mermaid way. The plain snake with CSS elbows is gone (owner, 2026-09-20).
    let layout = "snake-mermaid";
    const LAYOUTS = ["snake-mermaid", "mermaid"];

    /** Left to right: the wrapping snake. The other layout is top down. */
    function lrMermaid() { return layout === "snake-mermaid"; }
    // The tile look is the viewer's choice for every document (globalState): the glyph over the
    // caption ("stacked", square tiles) or beside it ("inline", one-line tiles).
    let tileStyle = "stacked";
    const TILE_STYLES = ["stacked", "inline"];
    let zoom = 1;             // viewer zoom, workspaceState per doc like the layout
    let canvas = null;        // the zoomed drawing surface, rebuilt on every render

    /**
     * §4 revisited with the snake in place: width no longer needs a depth default, so
     * everything renders EXPANDED unless the viewer folded it (the strip stays folded on
     * its own <details>). Toggles live in workspaceState.
     */
    function isCollapsed(step) {
        return toggled.has(keyOf(step.path));
    }

    function countSteps(step) {
        if (step.kind === "scope") return step.steps.length;
        if (step.kind === "branching") return step.branches.length;
        return 0;
    }

    /** Every container of the drawn file (scopes and branchings, nested ones too), by key. */
    function containerKeys(graph) {
        const keys = [];
        function walk(steps) {
            (steps || []).forEach(function (step) {
                if (step.kind === "scope") { keys.push(keyOf(step.path)); walk(step.steps); }
                else if (step.kind === "branching") {
                    keys.push(keyOf(step.path));
                    step.branches.forEach(function (b) { walk(b.steps); });
                }
            });
        }
        (graph.routes || []).forEach(function (r) { walk(r.steps); });
        return keys;
    }

    /** Folds (true) or unfolds (false) every container at once; remembered like single toggles. */
    function setAllCollapsed(collapsed) {
        if (!lastGraph) return;
        // folding all containers keeps the routes folded by hand; unfolding all opens everything
        const routes = Array.from(toggled).filter(function (k) { return k.indexOf("r:") === 0; });
        toggled = new Set(collapsed ? containerKeys(lastGraph).concat(routes) : []);
        vscode.postMessage({ type: "setCollapsed", keys: Array.from(toggled) });
        rerender();
    }

    function collapseToggle(step) {
        const button = el("span", "fold", isCollapsed(step) ? "▸" : "▾");
        button.title = isCollapsed(step)
            ? "folded — click to expand (" + countSteps(step) + (step.kind === "branching" ? " branches)" : " steps)")
            : "click to fold into one tile";
        button.addEventListener("click", function (e) {
            e.stopPropagation();
            const key = keyOf(step.path);
            if (toggled.has(key)) toggled.delete(key); else toggled.add(key);
            vscode.postMessage({ type: "toggleCollapse", key: key });
            rerender();
        });
        return button;
    }

    function connector(kind) {
        // CSS elbows stretch to the row's full height, so tall scope rows stay visually
        // connected: out = center-right turning down, in = top-left turning right + arrow.
        const elbow = el("div", "wrap " + kind);
        if (kind === "in") elbow.appendChild(el("i"));
        return elbow;
    }

    let lastGraph = null;
    function rerender() { if (lastGraph) render(lastGraph); }

    // Re-chunk the snake on width changes (§2 wrap: the break point is COMPUTED from the
    // window, never stored - Р4 holds).
    let resizePending = null;
    window.addEventListener("resize", function () {
        clearTimeout(resizePending);
        resizePending = setTimeout(rerender, 150);
    });

    /** The folded container: its tile with a counter badge (§4). */
    function collapsedView(step) {
        const box = tile(step.type, null, step.category,
            (step.tooltip || step.type) + " — collapsed, " + countSteps(step) +
            (step.kind === "branching" ? " branch(es)" : " step(s)"),
            "node collapsed cat-" + step.category);
        box.appendChild(collapseToggle(step));
        box.appendChild(el("span", "badge", String(countSteps(step))));
        // a folded debounce / throttle still shows where a message may drop out or fail
        const cond = conditionOf(step.type, step.attrs);
        if (cond) box.dataset.cond = cond;
        selectable(box, step.path, step.span, "container");
        draggable(box, step.path);
        return box;
    }

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function keyOf(path) { return path.join(","); }

    function reveal(span) {
        vscode.postMessage({ type: "reveal", start: span.start, end: span.end });
    }

    function select(path) {
        selectedKey = keyOf(path);
        selectedPath = path;
        selectedGroup = null;
        highlightSelection();
        vscode.postMessage({ type: "select", path: path });
    }

    /** The panel's title: the step's glyph in its group color, then its name. */
    function panelHeadTitle(head, type, scheme, text) {
        const look = lookOf(type, scheme, scheme === "bean" ? "userCode" : null);
        head.classList.add("tone-" + look.tone);
        head.appendChild(glyph(look.glyph));
        head.appendChild(el("span", "panel-type", scheme && text === type ? text + " · " + scheme : text));
    }

    /** A run of assignments is selected as one: its panel lists the rows, built here. */
    function selectGroup(steps) {
        selectedKey = groupKey(steps);
        selectedPath = null;
        selectedGroup = steps;
        highlightSelection();
        renderGroupPanel(steps);
    }

    /** Removes every element of a run in one message: the host does it bottom up. */
    function removeGroup(steps) {
        vscode.postMessage({ type: "removeSteps", paths: steps.map(function (s) { return s.path; }) });
        clearSelection();
    }

    function revealGroup(steps) {
        reveal({ start: steps[0].span.start, end: steps[steps.length - 1].span.end });
    }

    function openGroupMenu(x, y, steps) {
        closeContextMenu();
        menuBox = el("div", "ctx-menu");
        function item(label, action, danger) {
            const row = el("div", "ctx-item" + (danger ? " danger" : ""), label);
            row.addEventListener("click", function (e) {
                e.stopPropagation();
                closeContextMenu();
                action();
            });
            menuBox.appendChild(row);
        }
        const first = steps[0], last = steps[steps.length - 1];
        item("Insert before", function () {
            openPalette({ parentPath: first.path.slice(0, -1), before: first.path[first.path.length - 1] });
        });
        item("Insert after", function () {
            openPalette({ parentPath: last.path.slice(0, -1), before: last.path[last.path.length - 1] + 1 });
        });
        item("Show in text", function () { revealGroup(steps); });
        item("Delete all " + steps.length, function () { removeGroup(steps); }, true);
        document.body.appendChild(menuBox);
        const rect = menuBox.getBoundingClientRect();
        menuBox.style.left = Math.min(x, window.innerWidth - rect.width - 6) + "px";
        menuBox.style.top = Math.min(y, window.innerHeight - rect.height - 6) + "px";
    }

    /**
     * The panel of a run: one row per element, in the file's order (the order matters — a
     * later row may read an earlier one), each with its name, `=` constant or `ƒ` expression,
     * and its value. Every edit goes to that element; the view is rebuilt from the file.
     */
    function renderGroupPanel(steps) {
        panel.textContent = "";
        if (!document.body.classList.contains("with-panel")) {
            document.body.classList.add("with-panel");
            setTimeout(rerender, 0);
        }
        const head = el("div", "panel-head");
        panelHeadTitle(head, steps[0].type, null, "set ×" + steps.length);
        const close = el("span", "panel-close", "✕");
        close.addEventListener("click", clearSelection);
        head.appendChild(close);
        panel.appendChild(head);
        panel.appendChild(el("div", "panel-section", "rows, in order"));
        steps.forEach(function (step) {
            const row = contentRow({
                path: step.path, type: "", text: null, name: step.assign.name, opaque: null,
                value: { attr: step.assign.attr, text: step.assign.text },
            }, false);
            // the kind of the row: an exchange property or a message header — switching renames
            // the element (<setProperty> <-> <setHeader>), everything else stays
            const kind = el("select", "value content-kind-target");
            [["setProperty", "property"], ["setHeader", "header"]].forEach(function (pair) {
                const o = el("option", null, pair[1]);
                o.value = pair[0];
                kind.appendChild(o);
            });
            kind.value = step.type;
            kind.title = "property: lives in the exchange only; header: travels with the message";
            kind.addEventListener("change", function () {
                vscode.postMessage({ type: "renameElement", path: step.path, name: kind.value });
            });
            row.replaceChild(kind, row.querySelector(".content-type"));
            panel.appendChild(row);
        });
        const actions = el("div", "panel-actions");
        const show = el("button", null, "Show in text");
        show.addEventListener("click", function () { revealGroup(steps); });
        actions.appendChild(show);
        const removeButton = el("button", "danger", "Delete all " + steps.length);
        removeButton.addEventListener("click", function () { removeGroup(steps); });
        actions.appendChild(removeButton);
        panel.appendChild(actions);
    }

    function clearSelection() {
        selectedKey = null;
        selectedPath = null;
        selectedGroup = null;
        panel.textContent = "";
        if (document.body.classList.contains("with-panel")) {
            document.body.classList.remove("with-panel");
            setTimeout(rerender, 0);
        }
        highlightSelection();
    }

    function highlightSelection() {
        root.querySelectorAll(".selected").forEach(function (n) { n.classList.remove("selected"); });
        if (selectedKey === null) return;
        const match = root.querySelector('[data-path="' + selectedKey + '"]');
        if (match) match.classList.add("selected");
    }

    function selectable(node, path, span, kind) {
        node.dataset.path = keyOf(path);
        node.addEventListener("click", function (e) {
            e.stopPropagation();
            select(path);
        });
        node.addEventListener("contextmenu", function (e) {
            e.preventDefault();
            e.stopPropagation();
            openContextMenu(e.clientX, e.clientY, path, span || null, kind || "step");
        });
    }

    // ── the context menu: the editing verbs one right-click away ─────

    let menuBox = null;
    function closeContextMenu() {
        if (menuBox) { menuBox.remove(); menuBox = null; }
    }

    function openContextMenu(x, y, path, span, kind) {
        closeContextMenu();
        menuBox = el("div", "ctx-menu");
        const parentPath = path.slice(0, -1);
        const index = path[path.length - 1];

        function item(label, action, danger) {
            const row = el("div", "ctx-item" + (danger ? " danger" : ""), label);
            row.addEventListener("click", function (e) {
                e.stopPropagation();
                closeContextMenu();
                action();
            });
            menuBox.appendChild(row);
        }

        if (kind === "route" || kind === "container" || kind === "branch") {
            item("Insert inside (first)", function () {
                openPalette({ parentPath: path, before: 0 });
            });
            item("Insert inside (last)", function () {
                openPalette({ parentPath: path, before: 9999 });
            });
        }
        if (kind === "step" || kind === "container") {
            item("Insert before", function () {
                openPalette({ parentPath: parentPath, before: index });
            });
            item("Insert after", function () {
                openPalette({ parentPath: parentPath, before: index + 1 });
            });
        }
        if (span)
            item("Show in text", function () { reveal(span); });
        if (kind !== "route")
            item("Delete", function () {
                vscode.postMessage({ type: "removeStep", path: path });
                clearSelection();
            }, true);

        document.body.appendChild(menuBox);
        const rect = menuBox.getBoundingClientRect();
        menuBox.style.left = Math.min(x, window.innerWidth - rect.width - 6) + "px";
        menuBox.style.top = Math.min(y, window.innerHeight - rect.height - 6) + "px";
    }

    window.addEventListener("click", closeContextMenu);
    window.addEventListener("contextmenu", closeContextMenu);

    /** Drag source (§8.4: переставить/вложить): carries the element path. */
    function draggable(node, path) {
        node.draggable = true;
        node.addEventListener("dragstart", function (e) {
            e.stopPropagation();
            dragPath = path;
            dragKey = keyOf(path);
            root.classList.add("dragging");
            e.dataTransfer.effectAllowed = "move";
            e.dataTransfer.setData("text/plain", dragKey);
        });
        node.addEventListener("dragend", function () {
            dragPath = null; dragKey = null;
            root.classList.remove("dragging");
        });
    }

    /** The «+» between steps: click opens the palette; a drag drops here. */
    function slot(parentPath, before) {
        const s = el("div", "slot");
        s.title = "insert here";
        s.appendChild(el("span", "slot-plus", "+"));
        s.addEventListener("click", function (e) {
            e.stopPropagation();
            openPalette({ parentPath: parentPath, before: before });
        });
        s.addEventListener("dragover", function (e) {
            if (dragPath === null) return;
            e.preventDefault();
            e.stopPropagation();
            s.classList.add("drop");
        });
        s.addEventListener("dragleave", function () { s.classList.remove("drop"); });
        s.addEventListener("drop", function (e) {
            e.preventDefault();
            e.stopPropagation();
            s.classList.remove("drop");
            if (dragPath === null) return;
            vscode.postMessage({
                type: "moveStep",
                fromPath: dragPath, toParentPath: parentPath, before: before,
            });
            dragPath = null;
        });
        return s;
    }

    // ── graph rendering ──────────────────────────────────────────────

    function nodeView(step) {
        const box = tile(step.type, step.scheme || null, step.category,
            step.tooltip || step.type, "node cat-" + step.category);
        const cond = conditionOf(step.type, step.attrs);
        if (cond) box.dataset.cond = cond;
        // several assignments in one step: `set ×3`, the rows are in the tooltip and the panel
        if ((step.type === "setProperties" || step.type === "setHeaders") && /^×\d+$/.test(step.label || ""))
            box.querySelector(".cap").textContent += " " + step.label;
        selectable(box, step.path, step.span, "step");
        draggable(box, step.path);
        return box;
    }

    /** A branch as a tile of its own kind: when / otherwise / try / catch / finally / 1, 2… */
    function branchTile(branch, parentType) {
        const type = branchType(branch.label, parentType);
        const box = tile(type, null, parentType === "tryCatch" ? "errors" : "flow",
            branch.tooltip || branch.label, "branch-label" + (branch.warn ? " warn" : ""));
        if (type === "multicast") box.querySelector(".cap").textContent = branch.label;
        selectable(box, branch.path, branch.span, "branch");
        return box;
    }

    /**
     * The horizontal line with insert slots: one before every step (its `before` is the
     * step's child-element index) and one append slot at the end.
     */
    // ── the snake: the route's main line wraps like text, with a drawn U-turn ──

    /**
     * Builds a line as a SNAKE: atoms (slot+step, unbreakable) measured in the live root,
     * then chunked greedily into left-to-right rows by the width left at this nesting depth;
     * rows are joined by stretch elbows and a left spine says «still one line». RECURSIVE:
     * scope bodies and branch rows snake too, each one level deeper.
     */
    function snakeSequence(steps, parentPath, available) {
        // The snake packs rows in LOCAL (unzoomed) units — the measure div sits in the
        // unzoomed root — so the viewport budget converts by the zoom factor.
        available = Math.max(240, available || (root.clientWidth - 44) / zoom);
        const entries = flatten(steps);
        const atoms = entries.map(function (entry) {
            const atom = el("span", "atom");
            atom.appendChild(entrySlot(entry));
            atom.appendChild(entryView(entry, function (step) { return stepView(step, available); }));
            return atom;
        });

        const measure = el("div", "seq measure");
        atoms.forEach(function (a) { measure.appendChild(a); });
        root.appendChild(measure);
        const widths = atoms.map(function (a) { return a.getBoundingClientRect().width; });
        root.removeChild(measure);

        // The edge is an empty gap the curve runs through; the turn is the room the carriage
        // return needs on the right of a row.
        const EDGE = 32, TURN = 40;

        const snake = el("div", "snake");
        let row = null, used = 0, rowIndex = 0;
        function newRow(withEntry) {
            row = el("div", "seq snake-row");
            if (withEntry) row.appendChild(connector("in"));
            snake.appendChild(row);
            used = withEntry ? 20 : 0;
            rowIndex++;
        }
        newRow(false);
        /**
         * A loop is kept on ONE row when it fits one: its return arrow then runs in a short
         * lane under that row instead of around the drawing. The width is the loop's whole
         * stretch, from its tile to its `end loop` tile.
         */
        function loopWidth(i) {
            const entry = entries[i];
            if (entry.role !== "open" || entry.step.type !== "loop") return 0;
            let w = 0;
            for (let j = i; j < entries.length; j++) {
                w += widths[j] + (j > i ? EDGE : 0);
                if (entries[j].role === "close" && entries[j].step === entry.step) return w;
            }
            return 0;
        }

        atoms.forEach(function (atom, i) {
            const need = widths[i] + (row.querySelector(".atom") ? EDGE : 0);
            const group = loopWidth(i);
            const keepLoop = group > 0 && used + EDGE + group + TURN > available && 20 + group + TURN <= available;
            if ((used + need + TURN > available || keepLoop) && row.querySelector(".atom")) {
                row.appendChild(connector("out"));
                // The RETURN lane: down at the row end, all the way back left, down into
                // the next row - the full carriage return the eye expects.
                const lane = el("div", "uturn");
                lane.appendChild(el("i"));
                lane.style.width = (used + 16) + "px";
                snake.appendChild(lane);
                newRow(true);
            }
            if (row.querySelector(".atom")) row.appendChild(el("div", "edge"));
            row.appendChild(atom);
            used += need;
        });
        if (parentPath !== undefined) {
            // The append slot never breaks the line — it rides the last row.
            const tail = el("span", "atom");
            tail.appendChild(slot(parentPath, 9999));
            row.appendChild(tail);
        }
        return snake;
    }

    /**
     * No container draws a box (owner, 2026-09-24, after Talend/n8n). A wrapper (metered,
     * loop, split, transaction…) puts its steps into the enclosing line between an opening tile
     * and an `end` tile, and they wrap together with it — no stair of indents; a loop adds a
     * return arrow from its end to its start. Branchings keep their fan; a filter is a branching
     * with one branch and a bypass; a folded wrapper stays one tile.
     */
    function flatten(steps) {
        const out = [];
        for (let i = 0; i < steps.length; i++) {
            const step = steps[i];
            if (step.kind === "scope" && step.type !== "filter" && !isCollapsed(step)) {
                out.push({ role: "open", step: step });
                out.push(...flatten(step.steps));
                out.push({ role: "close", step: step });
                continue;
            }
            // A run of assignments (setProperty and setHeader, mixed, in their order) is ONE tile
            // with rows, the Talend way (owner, 2026-09-24): the file keeps its elements, only
            // the view joins them.
            if (isAssignment(step)) {
                let j = i + 1;
                while (j < steps.length && isAssignment(steps[j])) j++;
                if (j - i >= 2) {
                    out.push({ role: "group", step: step, steps: steps.slice(i, j) });
                    i = j - 1;
                    continue;
                }
            }
            out.push({ role: "step", step: step });
        }
        return out;
    }

    function isAssignment(step) {
        return step.kind === "leaf" && step.assign && (step.type === "setProperty" || step.type === "setHeader");
    }

    /** The insert slot in front of an entry: before the step, or at the end of a wrapper. */
    function entrySlot(entry) {
        const step = entry.step;
        if (entry.role === "close") return slot(step.path, 9999);
        return slot(step.path.slice(0, -1), step.path[step.path.length - 1]);
    }

    function entryView(entry, view) {
        if (entry.role === "open") return wrapperTile(entry.step, false);
        if (entry.role === "close") return wrapperTile(entry.step, true);
        if (entry.role === "group") return groupTile(entry.steps);
        return view(entry.step);
    }

    /** The row text of one assignment: a header marked, a constant quoted, an expression bare. */
    function assignmentLine(step) {
        const a = step.assign;
        return (step.type === "setHeader" ? "header " : "") + a.name + " = " +
            (a.attr === "expr" ? a.text : '"' + a.text + '"');
    }

    /** The key a group is selected by: its first element, marked so it never meets a step key. */
    function groupKey(steps) { return "g:" + keyOf(steps[0].path); }

    /** One tile for a run of assignments: `set ×3`, the rows in the tooltip and the panel. */
    function groupTile(steps) {
        const first = steps[0];
        const oneKind = steps.every(function (s) { return s.type === first.type; });
        const box = tile(first.type, null, first.category,
            ["set ×" + steps.length].concat(steps.map(assignmentLine)).join("\n"),
            "node cat-" + first.category);
        // all headers: `set hdr ×2`; properties or a mix: `set ×3`
        box.querySelector(".cap").textContent =
            (oneKind && first.type === "setHeader" ? "set hdr" : "set") + " ×" + steps.length;
        box.dataset.path = groupKey(steps);
        box.groupSteps = steps;
        box.addEventListener("click", function (e) {
            e.stopPropagation();
            selectGroup(steps);
        });
        box.addEventListener("contextmenu", function (e) {
            e.preventDefault();
            e.stopPropagation();
            openGroupMenu(e.clientX, e.clientY, steps);
        });
        return box;
    }

    /** The opening or the closing tile of a wrapper; both select the wrapper itself. */
    function wrapperTile(step, closing) {
        const box = tile(step.type, null, step.category,
            (closing ? "end of " : "") + (step.tooltip || step.type),
            "node wrapper-" + (closing ? "end" : "open") + " cat-" + step.category);
        box.dataset.wrapper = keyOf(step.path);
        box.dataset.wrapperType = step.type;
        if (closing) {
            const cap = box.querySelector(".cap");
            cap.textContent = "end " + cap.textContent;
            // a consumer that lets duplicates through has no skip to draw
            if (step.type === "idempotentConsumer" && (step.attrs || {}).skipDuplicate === "false")
                box.dataset.noLane = "1";
        } else {
            const cond = conditionOf(step.type, step.attrs);
            if (cond) box.dataset.cond = cond;
            box.appendChild(collapseToggle(step));
            if (step.repeats) box.appendChild(el("span", "badge", "⟳"));
            if (step.parallel) box.appendChild(el("span", "badge", "∥"));
        }
        selectable(box, step.path, step.span, "container");
        if (!closing) draggable(box, step.path);
        return box;
    }

    /** A filter drawn as a branching with its one conditional branch. */
    function filterAsBranching(step) {
        return {
            kind: "branching", path: step.path, type: step.type, category: step.category,
            id: step.id, tooltip: step.tooltip, span: step.span,
            branches: [{
                path: step.path, label: "when", tooltip: step.tooltip, warn: false,
                steps: step.steps, span: step.span,
            }],
        };
    }

    /** A step that is not a wrapper (flatten() spliced those): a branching, a folded tile, a leaf. */
    function stepView(step, available) {
        if (step.kind === "scope" || step.kind === "branching") {
            if (isCollapsed(step)) return collapsedView(step);
            if (step.kind === "scope") return branchingView(filterAsBranching(step), available);
            return branchingView(step, available);
        }
        return nodeView(step);
    }

    function branchingView(step, available) {
        const box = el("div", "branching");
        box.dataset.path = keyOf(step.path);
        box.dataset.type = step.type;
        draggable(box, step.path);
        // The decision tile the fan starts from, with the stack of branches to the right.
        const head = tile(step.type, null, step.category, step.tooltip || step.type,
            "lbranch-head cat-" + step.category);
        head.appendChild(collapseToggle(step));
        selectable(head, step.path, step.span, "container");
        box.appendChild(head);
        const host = el("div", "lstack");
        box.appendChild(host);
        step.branches.forEach(function (branch) {
            const row = el("div", "branch");
            row.dataset.kind = branchType(branch.label, step.type);
            row.appendChild(branchTile(branch, step.type));
            row.appendChild(el("div", "edge"));
            // the decision tile, the branch tile and the gaps take their share of the line
            row.appendChild(snakeSequence(branch.steps, branch.path,
                (available || root.clientWidth - 44) - (2 * tileWidth() + 96)));
            host.appendChild(row);
        });
        return box;
    }

    function stripView(scope) {
        const details = el("details", "strip");
        const summary = el("summary", null, scope.type + "  " + (scope.label || ""));
        summary.title = scope.tooltip;
        details.appendChild(summary);
        details.appendChild(snakeSequence(scope.steps, scope.path, root.clientWidth - 60));
        return details;
    }

    // ── a whole route folds to its header (owner, 2026-09-25) ─────────
    function routeKey(route) { return "r:" + keyOf(route.path); }
    function isRouteCollapsed(route) { return toggled.has(routeKey(route)); }

    /** All steps of a route, nested ones included — what a folded route hides. */
    function countAllSteps(steps) {
        let n = 0;
        (steps || []).forEach(function (step) {
            n++;
            if (step.kind === "scope") n += countAllSteps(step.steps);
            else if (step.kind === "branching") step.branches.forEach(function (b) { n += countAllSteps(b.steps); });
        });
        return n;
    }

    /** The route's title line with its own fold button. */
    function routeHeader(route) {
        const header = el("div", "route-header");
        const folded = isRouteCollapsed(route);
        const button = el("span", "route-fold" + (folded ? " folded" : ""), folded ? "▸" : "▾");
        button.title = folded ? "expand the route" : "fold the route to its title";
        button.addEventListener("click", function (e) {
            e.stopPropagation();
            const key = routeKey(route);
            if (toggled.has(key)) toggled.delete(key); else toggled.add(key);
            vscode.postMessage({ type: "toggleCollapse", key: key });
            rerender();
        });
        header.appendChild(button);
        header.appendChild(document.createTextNode(route.id || "(no id)"));
        if (route.description) header.appendChild(el("span", "description", route.description));
        if (folded) header.appendChild(el("span", "route-count", countAllSteps(route.steps) + " steps"));
        selectable(header, route.path, route.span, "route");
        return header;
    }

    /** A folded route: its entry tile only — where the messages come from. */
    function foldedRoute(route) {
        const line = el("div", "seq route-folded");
        if (route.from) line.appendChild(nodeView(route.from));
        return line;
    }

    function routeView(route) {
        const box = el("div", "route");
        box.appendChild(routeHeader(route));
        if (isRouteCollapsed(route)) { box.appendChild(foldedRoute(route)); return box; }
        route.strips.forEach(function (s) { box.appendChild(stripView(s)); });
        const steps = route.from ? [route.from].concat(route.steps) : route.steps;
        box.appendChild(snakeSequence(steps, route.path, root.clientWidth - 44));
        return box;
    }

    function beansRow(beans) {
        const row = el("div", "beans");
        row.appendChild(el("span", "beans-title", "beans:"));
        beans.forEach(function (bean) {
            const chip = el("span", "bean-chip", "#" + bean.name);
            chip.title = bean.type;
            chip.addEventListener("click", function () { reveal(bean.span); });
            row.appendChild(chip);
        });
        return row;
    }

    // ── the vertical layout: TD columns, routes side by side ─────────

    function vedge() {
        return el("div", "vedge");
    }

    function vstack(steps, parentPath) {
        const col = el("div", "vcol");
        flatten(steps).forEach(function (entry, i) {
            if (i > 0) col.appendChild(vedge());
            col.appendChild(entrySlot(entry));
            col.appendChild(entryView(entry, stepViewV));
        });
        if (parentPath !== undefined)
            col.appendChild(slot(parentPath, 9999));
        return col;
    }

    /** The top-down twin of stepView. */
    function stepViewV(step) {
        if (step.kind === "scope" || step.kind === "branching") {
            if (isCollapsed(step)) return collapsedView(step);
            if (step.kind === "scope") return branchingViewV(filterAsBranching(step));
            return branchingViewV(step);
        }
        return nodeView(step);
    }

    function branchingViewV(step) {
        const box = el("div", "vbranching cat-" + step.category);
        box.dataset.type = step.type;
        const head = tile(step.type, null, step.category, step.tooltip || step.type,
            "vbranch-head cat-" + step.category);
        head.appendChild(collapseToggle(step));
        selectable(head, step.path, step.span, "container");
        draggable(head, step.path);
        box.appendChild(head);
        const fan = el("div", "vfan");
        step.branches.forEach(function (branch) {
            const column = el("div", "vbranch");
            column.dataset.kind = branchType(branch.label, step.type);
            column.appendChild(branchTile(branch, step.type));
            column.appendChild(vedge());
            column.appendChild(vstack(branch.steps, branch.path));
            fan.appendChild(column);
        });
        box.appendChild(fan);
        return box;
    }

    function routeViewV(route) {
        const box = el("div", "routeV");
        box.appendChild(routeHeader(route));
        if (isRouteCollapsed(route)) { box.appendChild(foldedRoute(route)); return box; }
        route.strips.forEach(function (s) { box.appendChild(stripView(s)); });
        const steps = route.from ? [route.from].concat(route.steps) : route.steps;
        box.appendChild(vstack(steps, route.path));
        return box;
    }

    // ── the edges: orthogonal lines over the actual tile positions ──
    //
    // Every edge runs in straight segments with rounded corners (owner, 2026-09-24: a diagonal
    // across the drawing reads as a wire to nowhere). Three shapes cover the flow:
    //   * a LINK between two steps: straight when they share a line, otherwise a step whose
    //     cross segment runs right before the target, so several tails merging into one step
    //     share it (the merge bus of a choice);
    //   * a FAN from a decision tile to its branches: one trunk right after the tile;
    //   * a container EXIT: from every inner tail along the inner edge of the container to its
    //     exit port, never across the body.

    /**
     * The overlay both layouts draw into. It lives INSIDE the zoomed canvas, so every
     * coordinate is local. Engines disagree on whether client rects include an ancestor's CSS
     * zoom (the webview's engine: no), so the effective scale is MEASURED instead of assumed
     * to equal the zoom factor.
     */
    function edgeOverlay() {
        const ns = "http://www.w3.org/2000/svg";
        const svg = document.createElementNS(ns, "svg");
        svg.setAttribute("class", "edge-overlay");
        const canvasRect = canvas.getBoundingClientRect();
        const scale = canvas.offsetWidth > 0 ? canvasRect.width / canvas.offsetWidth : 1;
        svg.setAttribute("width", String(canvasRect.width / scale));
        svg.setAttribute("height", String(canvasRect.height / scale));

        function local(x, y) {
            return { x: (x - canvasRect.left) / scale, y: (y - canvasRect.top) / scale };
        }

        function rect(node) {
            const r = node.getBoundingClientRect();
            const a = local(r.left, r.top), b = local(r.right, r.bottom);
            return {
                left: a.x, top: a.y, right: b.x, bottom: b.y,
                cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2,
            };
        }

        function add(d, className) {
            const path = document.createElementNS(ns, "path");
            path.setAttribute("d", d);
            if (className) path.setAttribute("class", className);
            else path.setAttribute("fill", "none");
            svg.appendChild(path);
        }

        /** The arrowhead at `end`, pointing along the unit direction (dx, dy). */
        function arrow(end, dx, dy, kind) {
            const px = -dy, py = dx;
            add("M" + (end.x - dx * 5 + px * 3.5) + " " + (end.y - dy * 5 + py * 3.5) +
                " L" + end.x + " " + end.y +
                " L" + (end.x - dx * 5 - px * 3.5) + " " + (end.y - dy * 5 - py * 3.5) + " Z",
                "head" + (kind ? " " + kind : ""));
        }

        /**
         * A polyline through the points with rounded corners; `withArrow` puts a head at the
         * end. Repeated points and points on one line are dropped first. `kind` colors a
         * conditional path: `skip` (the condition did not hold), `drop`, `error`.
         */
        function ortho(points, withArrow, kind) {
            const pts = [];
            points.forEach(function (p) {
                const q = { x: Math.round(p.x * 2) / 2, y: Math.round(p.y * 2) / 2 };
                const last = pts[pts.length - 1];
                if (last && Math.abs(last.x - q.x) < 0.5 && Math.abs(last.y - q.y) < 0.5) return;
                if (pts.length >= 2) {
                    const prev = pts[pts.length - 2];
                    const collinear = (Math.abs(prev.x - last.x) < 0.5 && Math.abs(last.x - q.x) < 0.5) ||
                        (Math.abs(prev.y - last.y) < 0.5 && Math.abs(last.y - q.y) < 0.5);
                    if (collinear) pts.pop();
                }
                pts.push(q);
            });
            if (pts.length < 2) return;
            let d = "M" + pts[0].x + " " + pts[0].y;
            for (let i = 1; i < pts.length - 1; i++) {
                const p = pts[i], prev = pts[i - 1], next = pts[i + 1];
                const inLen = Math.abs(p.x - prev.x) + Math.abs(p.y - prev.y);
                const outLen = Math.abs(next.x - p.x) + Math.abs(next.y - p.y);
                const radius = Math.min(6, inLen / 2, outLen / 2);
                const inX = Math.sign(p.x - prev.x), inY = Math.sign(p.y - prev.y);
                const outX = Math.sign(next.x - p.x), outY = Math.sign(next.y - p.y);
                d += " L" + (p.x - inX * radius) + " " + (p.y - inY * radius) +
                     " Q" + p.x + " " + p.y + " " + (p.x + outX * radius) + " " + (p.y + outY * radius);
            }
            const end = pts[pts.length - 1], before = pts[pts.length - 2];
            const dx = Math.sign(end.x - before.x), dy = Math.sign(end.y - before.y);
            add(d + " L" + (end.x - (withArrow ? dx * 4 : 0)) + " " + (end.y - (withArrow ? dy * 4 : 0)),
                kind ? "cond " + kind : null);
            if (withArrow) arrow(end, dx, dy, kind);
        }

        /** The end of a path that goes nowhere: a small cross (a dropped message). */
        function cross(p, kind) {
            add("M" + (p.x - 4) + " " + (p.y - 4) + " L" + (p.x + 4) + " " + (p.y + 4) +
                " M" + (p.x + 4) + " " + (p.y - 4) + " L" + (p.x - 4) + " " + (p.y + 4), "cond " + kind);
        }

        /** A short caption on an edge (the loop's «repeat»). */
        function label(x, y, text, vertical, kind) {
            const t = document.createElementNS(ns, "text");
            t.setAttribute("x", String(x));
            t.setAttribute("y", String(y));
            if (vertical) t.setAttribute("transform", "rotate(-90 " + x + " " + y + ")");
            t.setAttribute("class", "edge-label" + (kind ? " " + kind : ""));
            t.textContent = text;
            svg.appendChild(t);
        }

        return {
            local: local, rect: rect, ortho: ortho, label: label, cross: cross,
            done: function () { canvas.appendChild(svg); },
        };
    }

    /** The branch rows (LR) or columns (TD) of a branching box. */
    function branchesOf(box, vertical) {
        return Array.from(box.querySelectorAll(vertical ? ":scope > .vfan > .vbranch" : ":scope > .lstack > .branch"));
    }

    /**
     * Which branches the flow LEAVES a branching through: every one of them, a tryCatch
     * included. Leaving through the finally alone left the try and catch tails hanging (owner,
     * 2026-09-24): no line can run from them back up into the finally without crossing.
     */
    function leavingBranches(box, vertical) {
        return branchesOf(box, vertical);
    }

    /**
     * A branching also left by a message NO branch took: a filter's rejected message, a
     * choice without `otherwise` when no `when` matched. The path starts at its tile.
     */
    function bypassOf(box, vertical) {
        const noOtherwise = box.dataset.type === "choice" &&
            !branchesOf(box, vertical).some(function (b) { return b.dataset.kind === "otherwise"; });
        if (box.dataset.type !== "filter" && !noOtherwise) return null;
        return box.querySelector(vertical ? ":scope > .vbranch-head" : ":scope > .lbranch-head");
    }

    /** The caption of a bypass: what kind of message takes it. */
    function bypassLabel(box) {
        return box.dataset.type === "filter" ? "not passed" : "no match";
    }

    /**
     * The lanes a wrapper draws between its tile and its `end` tile: the loop's return, and the
     * skip of a wrapper that lets some messages past its body.
     */
    const WRAPPER_LANES = {
        loop: { back: true, kind: null, label: "repeat" },
        ofType: { back: false, kind: "skip", label: "other type" },
        idempotentConsumer: { back: false, kind: "skip", label: "duplicate" },
    };

    /**
     * The conditional path of one step, if it has one: `drop` — the message may go nowhere
     * (sample, debounce); `error` — the step may raise (validate unless told not to, throw,
     * a throttle that rejects on overflow).
     */
    function conditionOf(type, attrs) {
        attrs = attrs || {};
        if (type === "sample" || type === "debounce") return "drop";
        if (type === "throwException") return "error";
        if ((type === "validate" || type === "validateJsonSchema" || type === "validateXsd") && attrs.throwOnFailure !== "false")
            return "error";
        if (type === "throttle" && attrs.rejectOnOverflow === "true") return "error";
        return null;
    }

    /** The short stubs of the `drop` and `error` paths, out of the tile that has one. */
    function drawConditionStubs(o, vertical) {
        canvas.querySelectorAll("[data-cond]").forEach(function (t) {
            const kind = t.dataset.cond;
            const r = o.rect(t);
            const text = kind === "drop" ? "dropped" : errorTarget(t);
            if (vertical) {
                const a = { x: r.right, y: r.cy + 10 }, b = { x: r.right + 18, y: r.cy + 10 };
                if (kind === "drop") { o.ortho([a, b], false, kind); o.cross({ x: b.x + 4, y: b.y }, kind); }
                else o.ortho([a, b], true, kind);
                o.label(b.x + 10, b.y + 3, text, false, kind);
            } else {
                const a = { x: r.cx + 14, y: r.bottom }, b = { x: r.cx + 14, y: r.bottom + 14 };
                if (kind === "drop") { o.ortho([a, b], false, kind); o.cross({ x: b.x, y: b.y + 4 }, kind); }
                else o.ortho([a, b], true, kind);
                o.label(b.x + 6, b.y + 4, text, false, kind);
            }
        });
    }

    /**
     * Where an error raised by a step goes: the catch of the nearest try-catch whose try branch
     * holds it, else the file's onException, else out of the route.
     */
    function errorTarget(tile) {
        const tryBranch = tile.closest('.branch[data-kind="try"], .vbranch[data-kind="try"]');
        if (tryBranch) {
            const box = tryBranch.closest(".branching, .vbranching");
            if (box && box.dataset.type === "tryCatch" &&
                branchesOf(box, box.classList.contains("vbranching")).some(function (b) { return b.dataset.kind === "catch"; }))
                return "→ catch";
        }
        return canvas.querySelector(".strip") ? "→ onException" : "→ error";
    }

    /**
     * Top-down: the column's spine. A tile hands the flow on from its bottom middle, a container
     * from the middle of its bottom edge; the flow enters a step at its top middle.
     */
    function drawCurvedEdges() {
        const o = edgeOverlay();
        const stepSelector = ":scope > .node, :scope > .scope, :scope > .vbranching";
        const innerSelector = ":scope > .vcol > .node, :scope > .vcol > .scope, :scope > .vcol > .vbranching";
        const GAP = 13;

        function top(node) { const r = o.rect(node); return { x: r.cx, y: r.top }; }
        function bottom(node) { const r = o.rect(node); return { x: r.cx, y: r.bottom }; }

        /** Where the flow enters a step: a branching through its decision tile. */
        function entryOf(step) {
            return step.classList.contains("vbranching")
                ? step.querySelector(":scope > .vbranch-head") || step : step;
        }

        /** The visual EXITS of a step: a branching merges from the tails of its leaving branches. */
        function exitsOf(step) {
            if (!step.classList.contains("vbranching") || !step.querySelector(":scope > .vfan"))
                return [step];
            const exits = [];
            leavingBranches(step, true).forEach(function (branch) {
                const steps = branch.querySelectorAll(innerSelector);
                if (steps.length > 0) exits.push(...exitsOf(steps[steps.length - 1]));
                else {
                    const label = branch.querySelector(":scope > .branch-label");
                    if (label) exits.push(label);
                }
            });
            // a filter, a choice without otherwise: the bypass is an exit too (see the LR twin)
            const head = bypassOf(step, true);
            if (head) exits.push({ bypass: step, head: head });
            return exits.length > 0 ? exits : [step];
        }

        /**
         * Where a path out of an exit starts: a tile's bottom middle, or — a bypass — out of the
         * decision tile to the right and down past the whole branching.
         */
        const bypassLabelled = new Set();
        function exitStart(from) {
            if (!from.bypass) return { lead: [], end: bottom(from), kind: null };
            const box = o.rect(from.bypass), h = o.rect(from.head);
            const x = box.right + 10;
            if (!bypassLabelled.has(from.bypass)) {
                bypassLabelled.add(from.bypass);
                o.label(x + 4, h.cy - 4, bypassLabel(from.bypass), false, "skip");
            }
            return { lead: [{ x: h.right, y: h.cy }, { x: x, y: h.cy }], end: { x: x, y: box.bottom + 6 }, kind: "skip" };
        }

        /** Down from an exit to b: straight on one spine, else across right above the target. */
        function link(from, b) {
            const s = exitStart(from), a = s.end;
            if (!s.kind && Math.abs(a.x - b.x) < 1.5) { o.ortho([a, { x: a.x, y: b.y }], true); return; }
            const busY = Math.max(a.y + 6, b.y - GAP);
            o.ortho(s.lead.concat([a, { x: a.x, y: busY }, { x: b.x, y: busY }, b]), true, s.kind);
        }

        canvas.querySelectorAll(".vcol").forEach(function (col) {
            const steps = col.querySelectorAll(stepSelector);
            for (let i = 1; i < steps.length; i++) {
                const target = top(entryOf(steps[i]));
                exitsOf(steps[i - 1]).forEach(function (from) { link(from, target); });
            }
        });
        // The loop returns: from its `end loop` tile out to the right of everything between the
        // two tiles, up, and into the `loop` tile from its right (the left edge of a column is
        // often the edge of the drawing).
        // The same lane carries the skip of a wrapper that lets a message past its body
        // (ofType: another type, idempotentConsumer: a duplicate), from its tile to its end.
        canvas.querySelectorAll(".wrapper-end").forEach(function (end) {
            const lane = WRAPPER_LANES[end.dataset.wrapperType];
            if (!lane || end.dataset.noLane) return;
            const open = canvas.querySelector('.wrapper-open[data-wrapper="' + end.dataset.wrapper + '"]');
            if (!open || open.parentElement !== end.parentElement) return;
            const items = Array.from(end.parentElement.children);
            let right = Math.max(o.rect(open).right, o.rect(end).right);
            for (let i = items.indexOf(open) + 1; i < items.indexOf(end); i++)
                if (items[i].matches(".node, .scope, .vbranching")) right = Math.max(right, o.rect(items[i]).right);
            const e = o.rect(end), s = o.rect(open);
            const x = right + (lane.kind ? 34 : 22);
            const from = lane.back ? e : s, to = lane.back ? s : e;
            o.ortho([{ x: from.right, y: from.cy }, { x: x, y: from.cy }, { x: x, y: to.cy }, { x: to.right, y: to.cy }],
                true, lane.kind);
            o.label(x + 11, (e.cy + s.cy) / 2 + 16, lane.label, true, lane.kind);
        });
        drawConditionStubs(o, true);
        canvas.querySelectorAll(".vbranching").forEach(function (box) {
            const head = box.querySelector(":scope > .vbranch-head");
            if (!head) return;
            const h = bottom(head);
            const trunkY = h.y + GAP;
            branchesOf(box, true).forEach(function (branch) {
                const label = branch.querySelector(":scope > .branch-label");
                if (!label) return;
                const t = top(label);
                o.ortho([h, { x: h.x, y: trunkY }, { x: t.x, y: trunkY }, t], true);
                const first = branch.querySelector(innerSelector);
                if (first) link(label, top(entryOf(first)));
            });
        });
        o.done();
    }

    /**
     * Left to right over the snake: a tile hands the flow on from its right middle, a container
     * from its right edge at the height of its head tile; a wrapped row returns along the lane
     * between the rows.
     */
    function drawCurvedEdgesLR() {
        const o = edgeOverlay();
        const stepSelector = ":scope > .atom > .node, :scope > .atom > .scope, :scope > .atom > .branching";
        const GAP = 14;

        function rowsOf(snake) { return Array.from(snake.querySelectorAll(":scope > .snake-row")); }
        function stepsOf(row) { return Array.from(row.querySelectorAll(stepSelector)); }

        function firstStepOf(snake) {
            for (const row of rowsOf(snake)) {
                const steps = stepsOf(row);
                if (steps.length > 0) return steps[0];
            }
            return null;
        }

        function lastStepOf(snake) {
            const rows = rowsOf(snake);
            for (let i = rows.length - 1; i >= 0; i--) {
                const steps = stepsOf(rows[i]);
                if (steps.length > 0) return steps[steps.length - 1];
            }
            return null;
        }

        /** Where the flow enters a step: a branching through its decision tile. */
        function entryOf(step) {
            return step.classList.contains("branching")
                ? step.querySelector(":scope > .lbranch-head") || step
                : step;
        }

        function leftPort(step) {
            const r = o.rect(entryOf(step));
            return { x: r.left, y: r.cy };
        }

        function rightPort(node) {
            const r = o.rect(node);
            return { x: r.right, y: r.cy };
        }

        /**
         * The visual EXITS of a step: a branching merges from the tails of its leaving branches,
         * and — a filter, a choice without otherwise — from its bypass too (`{ bypass, head }`),
         * so the bypass follows the flow wherever the branches' tails go: the next step, the
         * wrapped row, the tails of an enclosing branching.
         */
        function exitsOf(step) {
            if (!step.classList.contains("branching") || !step.querySelector(":scope > .lstack"))
                return [step];
            const exits = [];
            leavingBranches(step, false).forEach(function (branch) {
                const snake = branch.querySelector(":scope > .snake");
                const last = snake ? lastStepOf(snake) : null;
                if (last) exits.push(...exitsOf(last));
                else {
                    const label = branch.querySelector(":scope > .branch-label");
                    if (label) exits.push(label);
                }
            });
            const head = bypassOf(step, false);
            if (head) exits.push({ bypass: step, head: head });
            return exits.length > 0 ? exits : [step];
        }

        /**
         * Where a path out of an exit starts. A tile or tail leaves from its right middle; a
         * bypass leaves its decision tile downward and runs under the whole branching first.
         */
        const bypassLabelled = new Set();
        function exitStart(from) {
            if (!from.bypass) return { lead: [], end: rightPort(from), kind: null };
            const box = o.rect(from.bypass), h = o.rect(from.head);
            const y = box.bottom + 8;
            if (!bypassLabelled.has(from.bypass)) {
                bypassLabelled.add(from.bypass);
                o.label(h.cx + 6, y - 3, bypassLabel(from.bypass), false, "skip");
            }
            return { lead: [{ x: h.cx, y: h.bottom }], end: { x: h.cx, y: y }, kind: "skip" };
        }

        /** Right from an exit to b: straight on one line, else a step right before the target. */
        function link(from, b) {
            const s = exitStart(from), a = s.end;
            if (!s.kind && Math.abs(a.y - b.y) < 1.5) { o.ortho([a, { x: b.x, y: a.y }], true); return; }
            const busX = Math.max(a.x + 6, b.x - GAP);
            o.ortho(s.lead.concat([a, { x: busX, y: a.y }, { x: busX, y: b.y }, b]), true, s.kind);
        }

        canvas.querySelectorAll(".snake").forEach(function (snake) {
            const rows = rowsOf(snake);
            rows.forEach(function (row, r) {
                const steps = stepsOf(row);
                for (let i = 1; i < steps.length; i++) {
                    const target = leftPort(steps[i]);
                    exitsOf(steps[i - 1]).forEach(function (from) { link(from, target); });
                }
                // The wrapped line: out on the right, back along the lane between the rows,
                // down, and into the first step of the next row.
                const next = rows[r + 1] ? stepsOf(rows[r + 1]) : [];
                if (steps.length === 0 || next.length === 0) return;
                const target = leftPort(next[0]);
                const rowBox = o.rect(row);
                const nextBox = o.rect(rows[r + 1]);
                const laneY = (rowBox.bottom + nextBox.top) / 2;
                const leftX = Math.min(nextBox.left, target.x - GAP);
                exitsOf(steps[steps.length - 1]).forEach(function (from) {
                    const s = exitStart(from), a = s.end;
                    const rightX = Math.max(rowBox.right, a.x) + 8;
                    o.ortho(s.lead.concat([a, { x: rightX, y: a.y }, { x: rightX, y: laneY },
                        { x: leftX, y: laneY }, { x: leftX, y: target.y }, target]), true, s.kind);
                });
            });
        });
        // The loop returns: from its `end loop` tile down under the row, back left and up into
        // the `loop` tile — the flowchart way.
        // The same lane carries the skip of a wrapper that lets a message past its body
        // (ofType: another type, idempotentConsumer: a duplicate), from its tile to its end.
        /** Under the tallest thing in a row, inside the room the row keeps for a lane. */
        /**
         * Under the tallest step the lane passes: only the steps between its two tiles
         * (`from`/`to`, either may sit on another row — then the rest of this row counts), not
         * the whole row, so a tall branching elsewhere in the row does not push the lane down.
         */
        function laneUnder(row, from, to) {
            const steps = Array.from(row.querySelectorAll(":scope > .atom > .node, :scope > .atom > .branching"));
            let i = from ? steps.indexOf(from) : -1, j = to ? steps.indexOf(to) : -1;
            if (i < 0) i = 0;
            if (j < 0) j = steps.length - 1;
            let lowest = 0;
            for (let k = Math.min(i, j); k <= Math.max(i, j); k++) lowest = Math.max(lowest, o.rect(steps[k]).bottom);
            return lowest + 12;
        }
        canvas.querySelectorAll(".wrapper-end").forEach(function (end) {
            const lane = WRAPPER_LANES[end.dataset.wrapperType];
            if (!lane || end.dataset.noLane) return;
            const open = canvas.querySelector('.wrapper-open[data-wrapper="' + end.dataset.wrapper + '"]');
            const row = end.closest(".snake-row");
            const openRow = open ? open.closest(".snake-row") : null;
            if (!open || !row || !openRow) return;
            const e = o.rect(end), s = o.rect(open);
            // the skip runs a little left of the middle, clear of the loop's lane ends
            const ex = lane.back ? e.cx : e.cx - 10, sx = lane.back ? s.cx : s.cx - 10;
            if (openRow === row) {
                const y = laneUnder(row, open, end) + (lane.kind ? 8 : 0);
                const pts = [{ x: ex, y: e.bottom }, { x: ex, y: y }, { x: sx, y: y }, { x: sx, y: s.bottom }];
                o.ortho(lane.back ? pts : pts.reverse(), true, lane.kind);
                o.label(sx + 8, y + 11, lane.label, false, lane.kind);
                return;
            }
            // The two tiles sit on different rows: around the right of the line, entering the
            // later-drawn tile from above, so the lane never crosses the rows in between.
            const rightX = o.rect(row.closest(".snake")).right + (lane.kind ? 26 : 16);
            if (lane.back) {
                const y = laneUnder(row, null, end), topY = s.top - 9;
                o.ortho([{ x: ex, y: e.bottom }, { x: ex, y: y }, { x: rightX, y: y }, { x: rightX, y: topY },
                    { x: sx, y: topY }, { x: sx, y: s.top }], true, lane.kind);
                o.label(rightX + 4, (y + topY) / 2, lane.label, false, lane.kind);
            } else {
                const y = laneUnder(openRow, open, null) + 8, topY = e.top - 9;
                o.ortho([{ x: sx, y: s.bottom }, { x: sx, y: y }, { x: rightX, y: y }, { x: rightX, y: topY },
                    { x: ex, y: topY }, { x: ex, y: e.top }], true, lane.kind);
                o.label(rightX + 4, (y + topY) / 2, lane.label, false, lane.kind);
            }
        });
        drawConditionStubs(o, false);
        canvas.querySelectorAll(".branching").forEach(function (box) {
            const head = box.querySelector(":scope > .lbranch-head");
            if (!head) return;
            const h = rightPort(head);
            const trunkX = h.x + GAP;
            branchesOf(box, false).forEach(function (branch) {
                const label = branch.querySelector(":scope > .branch-label");
                if (!label) return;
                const l = leftPort(label);
                o.ortho([h, { x: trunkX, y: h.y }, { x: trunkX, y: l.y }, l], true);
                const snake = branch.querySelector(":scope > .snake");
                const first = snake ? firstStepOf(snake) : null;
                if (first) link(label, leftPort(first));
            });
        });
        o.done();
    }

    /**
     * Draws the edges and draws them again if drawing moved the tiles: the overlay makes the
     * page taller, a scrollbar appears, the window narrows and centered columns shift — the
     * lines must follow the tiles where they finally stand.
     */
    let edgeDraw = null;   // the edge painter of the current layout, for redraws
    let edgeProbe = null;  // where the first tile of the routes stood when the edges were drawn
    /** The first tile of the routes (not of the strip above them): what moves when the layout does. */
    function probeTile() {
        return canvas && canvas.querySelector(".route .node, .routeV .node");
    }
    function probeAt() {
        const probe = probeTile();
        if (!probe) return null;
        const r = probe.getBoundingClientRect();
        return { left: r.left, top: r.top };
    }
    function redrawEdges() {
        if (!canvas || !edgeDraw) return;
        canvas.querySelectorAll(":scope > .edge-overlay").forEach(function (svg) { svg.remove(); });
        edgeDraw();
        edgeProbe = probeAt();
    }
    /** Redraws when the tiles moved since the last drawing, horizontally or vertically. */
    function followTiles() {
        const now = probeAt();
        if (now && edgeProbe && (Math.abs(now.left - edgeProbe.left) > 0.5 || Math.abs(now.top - edgeProbe.top) > 0.5))
            redrawEdges();
    }
    function settleEdges(draw) {
        edgeDraw = draw;
        redrawEdges();
        // the tiles may still move after this frame (a scrollbar appearing narrows the window,
        // centered columns shift): check once the layout has settled and follow them
        requestAnimationFrame(followTiles);
    }
    // any later change of the drawing's box (the window, the panel, a scrollbar, the strip
    // above the routes opening) redraws the lines where the tiles now stand
    if (typeof ResizeObserver === "function") {
        let pendingEdges = null;
        new ResizeObserver(function () {
            cancelAnimationFrame(pendingEdges);
            pendingEdges = requestAnimationFrame(followTiles);
        }).observe(root);
    }
    // the strip (onException…) opening or closing pushes every route down or up
    document.addEventListener("toggle", function (e) {
        if (e.target && e.target.classList && e.target.classList.contains("strip"))
            requestAnimationFrame(followTiles);
    }, true);

    // ── the layout toggle ────────────────────────────────────────────

    function toolbar() {
        const bar = el("div", "toolbar");
        [["snake-mermaid", "⇢ mermaid"], ["mermaid", "⇣ mermaid"]].forEach(function (pair) {
            const button = el("button", layout === pair[0] ? "active" : "", pair[1]);
            button.addEventListener("click", function () {
                if (layout === pair[0]) return;
                layout = pair[0];
                vscode.postMessage({ type: "setLayout", layout: layout });
                rerender();
            });
            bar.appendChild(button);
        });
        // fold or unfold every container of the file at once
        const folds = el("span", "zoom-box");
        [["⊟", "collapse all containers", true], ["⊞", "expand all containers", false]].forEach(function (f) {
            const button = el("button", "", f[0]);
            button.title = f[1];
            button.addEventListener("click", function () { setAllCollapsed(f[2]); });
            folds.appendChild(button);
        });
        bar.appendChild(folds);
        const tiles = el("span", "zoom-box");
        [["stacked", "▦", "glyph over the caption"], ["inline", "▭", "glyph beside the caption"]].forEach(function (t) {
            const button = el("button", tileStyle === t[0] ? "active" : "", t[1]);
            button.title = t[2];
            button.addEventListener("click", function () {
                if (tileStyle === t[0]) return;
                tileStyle = t[0];
                vscode.postMessage({ type: "setTileStyle", tileStyle: tileStyle });
                rerender();
            });
            tiles.appendChild(button);
        });
        bar.appendChild(tiles);
        const zoomBox = el("span", "zoom-box");
        function zoomButton(text, factor) {
            const b = el("button", "", text);
            b.addEventListener("click", function () { setZoomLevel(zoom * factor); });
            return b;
        }
        zoomBox.appendChild(zoomButton("−", 1 / 1.2));
        const pct = el("button", "zoom-pct", Math.round(zoom * 100) + "%");
        pct.title = "reset zoom";
        pct.addEventListener("click", function () { setZoomLevel(1); });
        zoomBox.appendChild(pct);
        zoomBox.appendChild(zoomButton("+", 1.2));
        bar.appendChild(zoomBox);
        return bar;
    }

    function setZoomLevel(k) {
        k = Math.round(Math.min(2.5, Math.max(0.4, k)) * 100) / 100;
        if (k === zoom) return;
        zoom = k;
        vscode.postMessage({ type: "setZoom", zoom: zoom });
        rerender();
    }

    // Ctrl+wheel (a trackpad pinch arrives the same way) zooms the canvas.
    window.addEventListener("wheel", function (e) {
        if (!e.ctrlKey) return;
        e.preventDefault();
        setZoomLevel(zoom * (e.deltaY < 0 ? 1.1 : 1 / 1.1));
    }, { passive: false });

    function render(graph) {
        root.textContent = "";
        const columnar = layout === "mermaid";
        root.classList.toggle("vertical", columnar);
        root.classList.toggle("mermaid-look", columnar || lrMermaid());
        root.classList.toggle("lr", lrMermaid());
        root.classList.toggle("tiles-inline", tileStyle === "inline");
        root.appendChild(toolbar());
        // Everything drawable lives on the zoomed canvas; the toolbar and the panel stay 1:1.
        canvas = el("div", "canvas");
        canvas.style.zoom = String(zoom);
        root.appendChild(canvas);
        graph.strips.forEach(function (s) { canvas.appendChild(stripView(s)); });
        if (columnar) {
            if (graph.beans && graph.beans.length > 0) canvas.appendChild(beansRow(graph.beans));
            const grid = el("div", "routes-grid");
            graph.routes.forEach(function (r) { grid.appendChild(routeViewV(r)); });
            graph.others.forEach(function (o) {
                const cell = el("div", "routeV");
                cell.appendChild(nodeView(o));
                grid.appendChild(cell);
            });
            canvas.appendChild(grid);
            highlightSelection();
            settleEdges(drawCurvedEdges);
            return;
        }
        if (graph.beans && graph.beans.length > 0) canvas.appendChild(beansRow(graph.beans));
        graph.routes.forEach(function (r) { canvas.appendChild(routeView(r)); });
        graph.others.forEach(function (o) {
            const row = el("div", "seq");
            row.appendChild(nodeView(o));
            canvas.appendChild(row);
        });
        highlightSelection();
        alignLanes();
        if (lrMermaid()) settleEdges(drawCurvedEdgesLR);
    }

    // The return lanes were sized from ESTIMATED row widths; true up against the real
    // layout so the right-hand corner sits exactly under the out-stub.
    function alignLanes() {
        root.querySelectorAll(".uturn").forEach(function (lane) {
            const row = lane.previousElementSibling;
            if (!row || !row.lastElementChild) return;
            // Same measured-scale story as the curve overlay: rect deltas may or may not
            // carry the canvas zoom depending on the engine.
            const scale = canvas && canvas.offsetWidth > 0
                ? canvas.getBoundingClientRect().width / canvas.offsetWidth : 1;
            const width = (row.lastElementChild.getBoundingClientRect().right
                - row.getBoundingClientRect().left) / scale;
            if (width > 20) lane.style.width = width + "px";
        });
    }

    // ── the palette (§8.5) ───────────────────────────────────────────

    function openPalette(target) {
        overlay.textContent = "";
        overlay.classList.add("open");

        const box = el("div", "palette");
        const search = el("input", "palette-search");
        search.placeholder = "filter…";
        box.appendChild(search);
        const list = el("div", "palette-list");
        box.appendChild(list);

        function renderList(filter) {
            list.textContent = "";
            paletteData.forEach(function (category) {
                const items = category.items.filter(function (item) {
                    return filter === "" || item.label.toLowerCase().includes(filter);
                });
                if (items.length === 0) return;
                list.appendChild(el("div", "palette-cat", category.title));
                items.forEach(function (item) {
                    const row = el("div", "palette-item", item.label);
                    if (item.hint) row.appendChild(el("span", "palette-hint", item.hint));
                    row.addEventListener("click", function () {
                        vscode.postMessage({
                            type: "insertStep",
                            parentPath: target.parentPath, before: target.before,
                            fragment: item.fragment,
                        });
                        closePalette();
                    });
                    list.appendChild(row);
                });
            });
        }
        search.addEventListener("input", function () { renderList(search.value.toLowerCase()); });
        renderList("");

        overlay.appendChild(box);
        search.focus();
    }

    function closePalette() {
        overlay.classList.remove("open");
        overlay.textContent = "";
    }

    overlay.addEventListener("click", function (e) {
        if (e.target === overlay) closePalette();
    });
    window.addEventListener("keydown", function (e) {
        if (e.key === "Escape") { closePalette(); return; }
        const tag = document.activeElement ? document.activeElement.tagName : "";
        if (tag === "INPUT" || tag === "SELECT" || tag === "TEXTAREA") return;
        if ((e.key === "Delete" || e.key === "Backspace") && selectedGroup !== null) {
            removeGroup(selectedGroup);
        } else if ((e.key === "Delete" || e.key === "Backspace") && selectedPath !== null) {
            vscode.postMessage({ type: "removeStep", path: selectedPath });
            clearSelection();
        }
    });

    // ── the properties panel (§8) ────────────────────────────────────

    function contentRow(row, parentPath) {
        const box = el("div", "content-row");
        box.appendChild(el("span", "content-type", row.type));

        if (row.opaque !== null) {
            box.appendChild(el("span", "content-opaque", row.opaque));
        } else {
            const input = el("input", "value");
            input.value = row.text !== null ? row.text : (row.name || "");
            input.placeholder = row.text !== null ? "(text)" : "name";
            input.addEventListener("change", function () {
                if (row.text !== null)
                    vscode.postMessage({ type: "setChildText", path: row.path, value: input.value });
                else
                    vscode.postMessage({
                        type: "setAttr", path: row.path, refresh: parentPath,
                        name: "name", value: input.value === "" ? null : input.value,
                    });
            });
            input.addEventListener("keydown", function (e) { if (e.key === "Enter") input.blur(); });
            box.appendChild(input);
            if (row.value) box.appendChild(assignmentEditor(row, parentPath));
        }

        const remove = el("span", "content-remove", "✕");
        remove.title = "remove";
        remove.addEventListener("click", function () {
            // a row of a joined `set ×N` tile is a step of its own: removed as a step, and the
            // run's panel is rebuilt from the file (parentPath === false)
            if (parentPath === false) vscode.postMessage({ type: "removeStep", path: row.path });
            else vscode.postMessage({ type: "removeChild", path: row.path });
        });
        box.appendChild(remove);
        return box;
    }

    /**
     * The value half of a setter row: `=` a constant or `ƒ` an expression, and the text. Switching
     * the kind moves the text to the other attribute (the format takes exactly one of them).
     */
    function assignmentEditor(row, parentPath) {
        const wrap = el("span", "content-assign");
        const kind = el("select", "value content-kind");
        [["value", "="], ["expr", "ƒ"]].forEach(function (pair) {
            const o = el("option", null, pair[1]);
            o.value = pair[0];
            o.title = pair[0] === "value" ? "constant (value=)" : "expression (expr=)";
            kind.appendChild(o);
        });
        kind.value = row.value.attr;
        kind.title = "constant or expression";
        const text = el("input", "value");
        text.value = row.value.text;
        text.placeholder = "value";
        function set(name, value, removeAttr) {
            vscode.postMessage({
                type: "setAttr", path: row.path, refresh: parentPath,
                name: name, value: value, removeAttr: removeAttr || null,
            });
        }
        kind.addEventListener("change", function () {
            set(kind.value, text.value, kind.value === "value" ? "expr" : "value");
        });
        text.addEventListener("change", function () { set(kind.value, text.value); });
        text.addEventListener("keydown", function (e) { if (e.key === "Enter") text.blur(); });
        wrap.appendChild(kind);
        wrap.appendChild(text);
        return wrap;
    }

    function fieldRow(props, field, messageType) {
        const row = el("div", "field" + (field.declared ? "" : " undeclared"));
        const label = el("label", null, field.name + (field.required ? " *" : ""));
        label.title = field.declared
            ? field.type + (field.required ? ", required" : "")
            : "not declared — kept as written";
        row.appendChild(label);
        row.appendChild(fieldControl(props, field, messageType));
        return row;
    }

    function commit(messageType, props, field, value) {
        vscode.postMessage({
            type: messageType,
            path: props.path,
            name: field.name,
            existing: field.value !== null,
            value: value === "" ? null : value,
        });
    }

    function fieldControl(props, field, messageType) {
        if (field.secret) {
            const input = el("input", "value");
            input.value = field.value || "";
            input.disabled = true;
            input.title = "contains a secret — edit in the text editor";
            return input;
        }
        if (field.enumValues || field.type === "Bool") {
            const selectBox = el("select", "value");
            const options = [""].concat(field.enumValues || ["true", "false"]);
            options.forEach(function (option) {
                const o = el("option", null, option === "" ? "(not set)" : option);
                o.value = option;
                selectBox.appendChild(o);
            });
            selectBox.value = field.value === null ? "" : field.value;
            selectBox.addEventListener("change", function () {
                commit(messageType, props, field, selectBox.value);
            });
            return selectBox;
        }
        const input = el("input", "value");
        input.value = field.value === null ? "" : field.value;
        input.placeholder = "(not set)";
        input.addEventListener("change", function () {
            commit(messageType, props, field, input.value);
        });
        input.addEventListener("keydown", function (e) {
            if (e.key === "Enter") input.blur();
        });
        return input;
    }

    function renderPanel(props) {
        panel.textContent = "";
        if (!document.body.classList.contains("with-panel")) {
            // The panel narrows the viewport - the snake must re-chunk for the new width.
            document.body.classList.add("with-panel");
            setTimeout(rerender, 0);
        }

        const head = el("div", "panel-head");
        panelHeadTitle(head, props.type, props.endpoint ? props.endpoint.scheme : null, props.type);
        const close = el("span", "panel-close", "✕");
        close.addEventListener("click", clearSelection);
        head.appendChild(close);
        panel.appendChild(head);

        props.fields.forEach(function (field) {
            panel.appendChild(fieldRow(props, field, "setAttr"));
        });

        if (props.endpoint) {
            panel.appendChild(el("div", "panel-section",
                "endpoint · " + props.endpoint.scheme));
            const pathField = {
                name: "path", type: "string", required: false, enumValues: null,
                value: props.endpoint.path, secret: false, declared: true,
            };
            panel.appendChild(fieldRow(props, pathField, "setUriPath"));
            props.endpoint.options.forEach(function (field) {
                panel.appendChild(fieldRow(props, field, "setUriOption"));
            });
        }

        if ((props.content && props.content.length > 0) || (props.addable && props.addable.length > 0)) {
            panel.appendChild(el("div", "panel-section", "content"));
            props.content.forEach(function (row) {
                panel.appendChild(contentRow(row, props.path));
            });
            if (props.addable && props.addable.length > 0) {
                const adders = el("div", "content-add");
                props.addable.forEach(function (childName) {
                    const button = el("button", null, "+ " + childName);
                    button.addEventListener("click", function () {
                        vscode.postMessage({ type: "addChild", path: props.path, childName: childName });
                    });
                    adders.appendChild(button);
                });
                panel.appendChild(adders);
            }
        }

        const actions = el("div", "panel-actions");
        const show = el("button", null, "Show in text");
        show.addEventListener("click", function () { reveal(props.span); });
        actions.appendChild(show);
        const removeButton = el("button", "danger", "Delete step");
        removeButton.addEventListener("click", function () {
            vscode.postMessage({ type: "removeStep", path: props.path });
            clearSelection();
        });
        actions.appendChild(removeButton);
        panel.appendChild(actions);
    }

    // ── messages and states ──────────────────────────────────────────

    window.addEventListener("message", function (event) {
        const message = event.data;
        if (message.type === "graph") {
            toggled = new Set(message.toggled || []);
            // A layout remembered from a retired mode (the plain columns, the plain snake) opens
            // as the default.
            if (message.layout) layout = LAYOUTS.includes(message.layout) ? message.layout : "snake-mermaid";
            if (message.zoom) zoom = message.zoom;
            if (message.tileStyle) tileStyle = TILE_STYLES.includes(message.tileStyle) ? message.tileStyle : "stacked";
            if (typeof message.panelWidth === "number" && message.panelWidth >= 260) {
                panelWidth = message.panelWidth;
                applyPanelWidth();
            }
            lastGraph = message.graph;
            render(message.graph);
            // A selected run is rebuilt from the new file: its rows show what was just edited,
            // or the panel closes when the run is gone.
            if (selectedGroup !== null) {
                const tile = root.querySelector('[data-path="' + selectedKey + '"]');
                if (tile && tile.groupSteps) {
                    selectedGroup = tile.groupSteps;
                    renderGroupPanel(selectedGroup);
                } else clearSelection();
            }
        }
        else if (message.type === "props") renderPanel(message.props);
        else if (message.type === "palette") paletteData = message.palette;
        else if (message.type === "foreign")
            notice("This XML does not carry the urn:redb:route namespace — nothing to draw.", true);
        else if (message.type === "broken")
            notice("The document does not parse (line " + message.line + "): " + message.message +
                " — fix it in the text editor.", true);
    });

    function notice(text, withOpenAsText) {
        root.textContent = "";
        const box = el("div", "notice", text);
        if (withOpenAsText) {
            const button = el("button", null, "Open as text");
            button.addEventListener("click", function () {
                vscode.postMessage({ type: "openAsText" });
            });
            box.appendChild(el("div")).appendChild(button);
        }
        root.appendChild(box);
    }
})();
