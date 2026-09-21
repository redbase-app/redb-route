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

    const ICONS = {
        source: "▶", sendExternal: "⇥", sendInternal: "→", transform: "≡",
        flow: "◈", errors: "▲", userCode: "▣", unknown: "?",
    };

    let selectedKey = null;   // "1,2,0" — survives full re-renders
    let selectedPath = null;
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

    function collapseToggle(step) {
        const button = el("span", "fold", isCollapsed(step) ? "▸" : "▾");
        button.title = isCollapsed(step) ? "expand" : "collapse";
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

    /** The folded square with a counter: `◈▸3` (§4). */
    function collapsedView(step) {
        const box = el("span", "node collapsed cat-" + step.category);
        box.title = (step.tooltip || step.type) + " — collapsed, " + countSteps(step) +
            (step.kind === "branching" ? " branch(es)" : " step(s)");
        box.appendChild(el("span", "icon", ICONS[step.category] || "◈"));
        box.appendChild(collapseToggle(step));
        box.appendChild(el("span", "t", step.type));
        box.appendChild(el("span", "label", String(countSteps(step))));
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
        highlightSelection();
        vscode.postMessage({ type: "select", path: path });
    }

    function clearSelection() {
        selectedKey = null;
        selectedPath = null;
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
        const box = el("span", "node cat-" + step.category);
        box.title = step.tooltip || step.type;
        box.appendChild(el("span", "icon", ICONS[step.category] || "?"));
        box.appendChild(el("span", "t", step.type));
        box.appendChild(el("span", "label", step.label));
        selectable(box, step.path, step.span, "step");
        draggable(box, step.path);
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
        const atoms = steps.map(function (step) {
            const atom = el("span", "atom");
            atom.appendChild(slot(step.path.slice(0, -1), step.path[step.path.length - 1]));
            atom.appendChild(stepView(step, available));
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
        atoms.forEach(function (atom, i) {
            const need = widths[i] + (row.querySelector(".atom") ? EDGE : 0);
            if (used + need + TURN > available && row.querySelector(".atom")) {
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

    function stepView(step, available) {
        if (step.kind === "scope" || step.kind === "branching") {
            if (isCollapsed(step)) return collapsedView(step);
            return step.kind === "scope" ? scopeView(step, available) : branchingView(step, available);
        }
        return nodeView(step);
    }

    function scopeHead(step) {
        const head = el("div", "scope-head");
        head.title = step.tooltip;
        head.appendChild(collapseToggle(step));
        head.appendChild(document.createTextNode(
            (ICONS[step.category] || "") + " " + step.type +
            (step.label && step.label !== step.type ? "  " + step.label : "")));
        if (step.repeats) head.appendChild(el("span", "repeat", "⟳"));
        if (step.parallel) head.appendChild(el("span", "repeat", "∥"));
        selectable(head, step.path, step.span, "container");
        draggable(head, step.path);
        return head;
    }

    function scopeView(step, available) {
        // §4: a one-leaf body collapses into the condition's own line automatically.
        if (step.steps.length === 1 && step.steps[0].kind === "leaf") {
            const row = el("div", "scope compact cat-" + step.category);
            row.appendChild(scopeHead(step));
            row.appendChild(el("div", "edge"));
            row.appendChild(stepView(step.steps[0], available));
            return row;
        }
        const box = el("div", "scope cat-" + step.category);
        box.appendChild(scopeHead(step));
        // the bracket walls and its head, which stands BESIDE the body, take their share of the line
        box.appendChild(snakeSequence(step.steps, step.path, (available || root.clientWidth - 44) - 260));
        return box;
    }

    function branchingView(step, available) {
        const box = el("div", "branching");
        box.dataset.path = keyOf(step.path);
        draggable(box, step.path);
        // The decision diamond the fan starts from, with the stack of branches to the right.
        const head = el("div", "lbranch-head cat-" + step.category);
        head.appendChild(collapseToggle(step));
        head.appendChild(el("span", "icon", ICONS[step.category] || "◈"));
        head.appendChild(el("span", "t", step.type));
        selectable(head, step.path, step.span, "container");
        box.appendChild(head);
        const host = el("div", "lstack");
        box.appendChild(host);
        step.branches.forEach(function (branch) {
            const row = el("div", "branch");
            const label = el("span", "branch-label" + (branch.warn ? " warn" : ""), branch.label);
            label.title = branch.tooltip || branch.label;
            selectable(label, branch.path, branch.span, "branch");
            row.appendChild(label);
            row.appendChild(el("div", "edge"));
            // the diamond, the branch label (capped at 280) and the gaps eat the lion share of the line
            row.appendChild(snakeSequence(branch.steps, branch.path,
                (available || root.clientWidth - 44) - 360));
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

    function routeView(route) {
        const box = el("div", "route");
        const header = el("div", "route-header", route.id || "(no id)");
        if (route.description) header.appendChild(el("span", "description", route.description));
        selectable(header, route.path, route.span, "route");
        box.appendChild(header);
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
        steps.forEach(function (step, i) {
            if (i > 0) col.appendChild(vedge());
            col.appendChild(slot(step.path.slice(0, -1), step.path[step.path.length - 1]));
            col.appendChild(stepViewV(step));
        });
        if (parentPath !== undefined)
            col.appendChild(slot(parentPath, 9999));
        return col;
    }

    function stepViewV(step) {
        if (step.kind === "scope" || step.kind === "branching") {
            if (isCollapsed(step)) return collapsedView(step);
            return step.kind === "scope" ? scopeViewV(step) : branchingViewV(step);
        }
        return nodeView(step);
    }

    function scopeViewV(step) {
        const box = el("div", "scope vscope cat-" + step.category);
        box.appendChild(scopeHead(step));
        box.appendChild(vstack(step.steps, step.path));
        return box;
    }

    function branchingViewV(step) {
        const box = el("div", "vbranching cat-" + step.category);
        const head = el("div", "vbranch-head cat-" + step.category);
        head.appendChild(collapseToggle(step));
        head.appendChild(el("span", "icon", ICONS[step.category] || "◈"));
        head.appendChild(el("span", "t", step.type));
        selectable(head, step.path, step.span, "container");
        draggable(head, step.path);
        box.appendChild(head);
        const fan = el("div", "vfan");
        step.branches.forEach(function (branch) {
            const column = el("div", "vbranch");
            const label = el("span", "branch-label" + (branch.warn ? " warn" : ""), branch.label);
            label.title = branch.tooltip || branch.label;
            selectable(label, branch.path, branch.span, "branch");
            column.appendChild(label);
            column.appendChild(vedge());
            column.appendChild(vstack(branch.steps, branch.path));
            fan.appendChild(column);
        });
        box.appendChild(fan);
        return box;
    }

    function routeViewV(route) {
        const box = el("div", "routeV");
        const header = el("div", "route-header", route.id || "(no id)");
        if (route.description) header.appendChild(el("span", "description", route.description));
        selectable(header, route.path, route.span, "route");
        box.appendChild(header);
        route.strips.forEach(function (s) { box.appendChild(stripView(s)); });
        const steps = route.from ? [route.from].concat(route.steps) : route.steps;
        box.appendChild(vstack(steps, route.path));
        return box;
    }

    // ── the mermaid look: SVG curves over the actual node positions ──

    /**
     * The overlay both mermaid layouts draw into. It lives INSIDE the zoomed canvas, so every
     * coordinate is local. Engines disagree on whether client rects include an ancestor's CSS
     * zoom (the webview's engine: no — curves flew apart by 1/zoom), so the effective scale is
     * MEASURED instead of assumed to equal the zoom factor.
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

        /** The middle of one side of a node's box. */
        function point(node, side) {
            const r = node.getBoundingClientRect();
            if (side === "top") return local(r.left + r.width / 2, r.top);
            if (side === "bottom") return local(r.left + r.width / 2, r.bottom);
            if (side === "left") return local(r.left, r.top + r.height / 2);
            return local(r.right, r.top + r.height / 2);
        }

        function add(d, className) {
            const path = document.createElementNS(ns, "path");
            path.setAttribute("d", d);
            if (className) path.setAttribute("class", className);
            else path.setAttribute("fill", "none");
            svg.appendChild(path);
        }

        function arrowDown(b) {
            add("M" + (b.x - 3.5) + " " + (b.y - 5) + " L" + b.x + " " + b.y +
                " L" + (b.x + 3.5) + " " + (b.y - 5) + " Z", "head");
        }

        function arrowRight(b) {
            add("M" + (b.x - 5) + " " + (b.y - 3.5) + " L" + b.x + " " + b.y +
                " L" + (b.x - 5) + " " + (b.y + 3.5) + " Z", "head");
        }

        /** Top-down: dive DOWN first, spread second, so a wide fan does not cut the labels. */
        function down(a, b) {
            const bend = Math.max(12, Math.min(56, (b.y - a.y) * 0.6 + Math.abs(b.x - a.x) * 0.12));
            add("M" + a.x + " " + a.y + " C" + a.x + " " + (a.y + bend) + " " + b.x + " " + (b.y - bend) +
                " " + b.x + " " + (b.y - 4));
            arrowDown(b);
        }

        /** Left to right: the horizontal twin of down(), leave RIGHT first, spread second. */
        function right(a, b) {
            const bend = Math.max(12, Math.min(56, (b.x - a.x) * 0.6 + Math.abs(b.y - a.y) * 0.12));
            add("M" + a.x + " " + a.y + " C" + (a.x + bend) + " " + a.y + " " + (b.x - bend) + " " + b.y +
                " " + (b.x - 4) + " " + b.y);
            arrowRight(b);
        }

        /** An orthogonal route through the points with rounded corners, entering the last point rightward. */
        function carriageReturn(points) {
            const radius = 6;
            let d = "M" + points[0].x + " " + points[0].y;
            for (let i = 1; i < points.length - 1; i++) {
                const p = points[i], prev = points[i - 1], next = points[i + 1];
                const inX = Math.sign(p.x - prev.x), inY = Math.sign(p.y - prev.y);
                const outX = Math.sign(next.x - p.x), outY = Math.sign(next.y - p.y);
                d += " L" + (p.x - inX * radius) + " " + (p.y - inY * radius) +
                     " Q" + p.x + " " + p.y + " " + (p.x + outX * radius) + " " + (p.y + outY * radius);
            }
            const end = points[points.length - 1];
            add(d + " L" + (end.x - 4) + " " + end.y);
            arrowRight(end);
        }

        return {
            local: local, point: point, down: down, right: right, carriageReturn: carriageReturn,
            done: function () { canvas.appendChild(svg); },
        };
    }

    /**
     * Top-down: bezier edges between the actual node positions. The vedge stubs are hidden by
     * CSS in this mode, the curves carry the flow instead, including the fan from a branching
     * head to its branch labels, the way Mermaid draws a choice.
     */
    function drawCurvedEdges() {
        const o = edgeOverlay();
        const stepSelector = ":scope > .node, :scope > .scope, :scope > .vbranching";
        const innerSelector = ":scope > .vcol > .node, :scope > .vcol > .scope, :scope > .vcol > .vbranching";

        /** The visual EXITS of a step: a branching merges, one exit per branch tail. */
        function exitsOf(step) {
            if (!step.classList.contains("vbranching"))
                return [step];
            const exits = [];
            step.querySelectorAll(":scope > .vfan > .vbranch").forEach(function (branch) {
                const steps = branch.querySelectorAll(innerSelector);
                if (steps.length > 0) exits.push(...exitsOf(steps[steps.length - 1]));
                else {
                    const label = branch.querySelector(":scope > .branch-label");
                    if (label) exits.push(label);
                }
            });
            return exits.length > 0 ? exits : [step];
        }

        canvas.querySelectorAll(".vcol").forEach(function (col) {
            const steps = col.querySelectorAll(stepSelector);
            for (let i = 1; i < steps.length; i++) {
                // Fan-in: after a branching the flow continues from EVERY branch tail; an
                // arrow born at an invisible box's bottom reads as «out of nowhere».
                exitsOf(steps[i - 1]).forEach(function (from) {
                    o.down(o.point(from, "bottom"), o.point(steps[i], "top"));
                });
            }
        });
        canvas.querySelectorAll(".scope").forEach(function (scope) {
            // Into the bracket: the head hands the flow to the first inner step.
            const head = scope.querySelector(":scope > .scope-head");
            const inner = scope.querySelectorAll(innerSelector);
            if (inner.length === 0) return;
            if (head) o.down(o.point(head, "bottom"), o.point(inner[0], "top"));
            // Out of the bracket through the bottom edge, from every tail of the last inner step:
            // a branch ending inside the box would otherwise read as a dead end (2026-09-18).
            const exit = o.point(scope, "bottom");
            exitsOf(inner[inner.length - 1]).forEach(function (from) {
                o.down(o.point(from, "bottom"), exit);
            });
        });
        canvas.querySelectorAll(".vbranching").forEach(function (box) {
            const head = box.querySelector(":scope > .vbranch-head");
            if (!head) return;
            box.querySelectorAll(":scope > .vfan > .vbranch").forEach(function (branch) {
                const label = branch.querySelector(":scope > .branch-label");
                if (!label) return;
                o.down(o.point(head, "bottom"), o.point(label, "top"));
                const first = branch.querySelector(innerSelector);
                if (first) o.down(o.point(label, "bottom"), o.point(first, "top"));
            });
        });
        o.done();
    }

    /**
     * Left to right over the snake: the same shapes; curves leave a step on its right and enter
     * the next on its left, a branching fans out from its diamond, and a wrapped row returns
     * along the lane between the rows. The CSS elbows and stubs of the plain snake step aside.
     */
    function drawCurvedEdgesLR() {
        const o = edgeOverlay();
        const stepSelector = ":scope > .atom > .node, :scope > .atom > .scope, :scope > .atom > .branching";

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

        /** Where the flow enters a step: a branching through its diamond. */
        function entryOf(step) {
            return step.classList.contains("branching")
                ? step.querySelector(":scope > .lbranch-head") || step
                : step;
        }

        /** The visual EXITS of a step: a branching merges from every branch tail. */
        function exitsOf(step) {
            if (!step.classList.contains("branching") || !step.querySelector(":scope > .lstack"))
                return [step];
            const exits = [];
            step.querySelectorAll(":scope > .lstack > .branch").forEach(function (branch) {
                const snake = branch.querySelector(":scope > .snake");
                const last = snake ? lastStepOf(snake) : null;
                if (last) exits.push(...exitsOf(last));
                else {
                    const label = branch.querySelector(":scope > .branch-label");
                    if (label) exits.push(label);
                }
            });
            return exits.length > 0 ? exits : [step];
        }

        canvas.querySelectorAll(".snake").forEach(function (snake) {
            const rows = rowsOf(snake);
            rows.forEach(function (row, r) {
                const steps = stepsOf(row);
                for (let i = 1; i < steps.length; i++) {
                    exitsOf(steps[i - 1]).forEach(function (from) {
                        o.right(o.point(from, "right"), o.point(entryOf(steps[i]), "left"));
                    });
                }
                // The wrapped line: out on the right, back along the lane between the rows,
                // down, and into the first step of the next row.
                const next = rows[r + 1] ? stepsOf(rows[r + 1]) : [];
                if (steps.length === 0 || next.length === 0) return;
                const target = o.point(entryOf(next[0]), "left");
                const rowBox = row.getBoundingClientRect();
                const nextBox = rows[r + 1].getBoundingClientRect();
                const rightX = o.local(rowBox.right, 0).x + 8;
                const laneY = (o.local(0, rowBox.bottom).y + o.local(0, nextBox.top).y) / 2;
                const leftX = Math.min(o.local(nextBox.left, 0).x, target.x - 16);
                exitsOf(steps[steps.length - 1]).forEach(function (from) {
                    const a = o.point(from, "right");
                    o.carriageReturn([a, { x: rightX, y: a.y }, { x: rightX, y: laneY },
                        { x: leftX, y: laneY }, { x: leftX, y: target.y }, target]);
                });
            });
        });
        canvas.querySelectorAll(".scope").forEach(function (scope) {
            const head = scope.querySelector(":scope > .scope-head");
            if (!head) return;
            // A compact scope carries its single leaf inline, a full one its own snake body.
            const body = scope.querySelector(":scope > .snake");
            const first = body ? firstStepOf(body) : scope.querySelector(":scope > .node");
            const last = body ? lastStepOf(body) : first;
            if (!first) return;
            o.right(o.point(head, "right"), o.point(entryOf(first), "left"));
            // Out of the bracket through its right edge, from every tail of the last inner step.
            const exit = o.point(scope, "right");
            exitsOf(last).forEach(function (from) { o.right(o.point(from, "right"), exit); });
        });
        canvas.querySelectorAll(".branching").forEach(function (box) {
            const head = box.querySelector(":scope > .lbranch-head");
            if (!head) return;
            box.querySelectorAll(":scope > .lstack > .branch").forEach(function (branch) {
                const label = branch.querySelector(":scope > .branch-label");
                if (!label) return;
                o.right(o.point(head, "right"), o.point(label, "left"));
                const snake = branch.querySelector(":scope > .snake");
                const first = snake ? firstStepOf(snake) : null;
                if (first) o.right(o.point(label, "right"), o.point(entryOf(first), "left"));
            });
        });
        o.done();
    }

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
            drawCurvedEdges();
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
        if (lrMermaid()) drawCurvedEdgesLR();
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
        if ((e.key === "Delete" || e.key === "Backspace") && selectedPath !== null) {
            vscode.postMessage({ type: "removeStep", path: selectedPath });
            clearSelection();
        }
    });

    // ── the properties panel (§8) ────────────────────────────────────

    function contentRow(row) {
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
                    vscode.postMessage({ type: "setAttr", path: row.path, name: "name", value: input.value === "" ? null : input.value });
            });
            input.addEventListener("keydown", function (e) { if (e.key === "Enter") input.blur(); });
            box.appendChild(input);
        }

        const remove = el("span", "content-remove", "✕");
        remove.title = "remove";
        remove.addEventListener("click", function () {
            vscode.postMessage({ type: "removeChild", path: row.path });
        });
        box.appendChild(remove);
        return box;
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
        head.appendChild(el("span", "panel-type", "<" + props.type + ">"));
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
                panel.appendChild(contentRow(row));
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
            lastGraph = message.graph;
            render(message.graph);
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
