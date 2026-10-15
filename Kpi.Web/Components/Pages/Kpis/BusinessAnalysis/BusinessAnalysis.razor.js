// Interactive donut for Business Analysis. Rendered client-side so hover/click
// (explode a segment, cross-highlight the matching table row, custom tooltip)
// are instant. The page hands us the slices + colours; we draw the SVG, wire the
// events, and cross-link with the table rows (which carry data-dim="<value>").

const SVGNS = "http://www.w3.org/2000/svg";
let tip;   // shared tooltip element

function ensureTip() {
    if (tip) return tip;
    tip = document.createElement("div");
    tip.className = "ba-tip";
    document.body.appendChild(tip);
    return tip;
}

function polar(cx, cy, r, deg) {
    const a = (deg - 90) * Math.PI / 180;   // 0deg = top
    return [cx + r * Math.cos(a), cy + r * Math.sin(a)];
}

function arcPath(cx, cy, rOuter, rInner, startDeg, endDeg) {
    const large = endDeg - startDeg > 180 ? 1 : 0;
    const [x1, y1] = polar(cx, cy, rOuter, startDeg);
    const [x2, y2] = polar(cx, cy, rOuter, endDeg);
    const [xi2, yi2] = polar(cx, cy, rInner, endDeg);
    const [xi1, yi1] = polar(cx, cy, rInner, startDeg);
    return `M${x1.toFixed(2)},${y1.toFixed(2)} A${rOuter},${rOuter} 0 ${large} 1 ${x2.toFixed(2)},${y2.toFixed(2)} `
         + `L${xi2.toFixed(2)},${yi2.toFixed(2)} A${rInner},${rInner} 0 ${large} 0 ${xi1.toFixed(2)},${yi1.toFixed(2)} Z`;
}

// slices: [{ label, value, colour, pct, valueText }]
export function render(container, slices, totalText) {
    if (!container) return;
    container.innerHTML = "";
    ensureTip();

    const size = 260, cx = size / 2, cy = size / 2;
    const rOuter = 118, rInner = 66, explode = 9;

    const svg = document.createElementNS(SVGNS, "svg");
    svg.setAttribute("viewBox", `0 0 ${size} ${size}`);
    svg.setAttribute("class", "ba-pie");
    container.appendChild(svg);

    // Hover is tracked as ONE explicit "hovered dim" per container, reconciled on every
    // event, rather than each slice toggling itself on its own leave. That's what makes it
    // self-healing: a dropped mouseleave (common in SVG — a popped slice can move out from
    // under the cursor before its leave fires) is corrected the moment the pointer enters
    // any other slice/row, or leaves the pie/table entirely (the container-level guards below).
    container._hoverDim = null;

    const total = slices.reduce((s, x) => s + x.value, 0);
    let start = 0;

    slices.forEach(sl => {
        const sweep = total > 0 ? (sl.value / total) * 360 : 0;
        const end = start + sweep;
        const mid = (start + end) / 2;

        const path = document.createElementNS(SVGNS, "path");
        path.setAttribute("d", arcPath(cx, cy, rOuter, rInner, start, end));
        path.setAttribute("fill", sl.colour);
        path.setAttribute("class", "ba-slice");
        path.dataset.dim = sl.label;

        // offset vector for the "pop out" (towards the slice's mid-angle) — stored on
        // the element so both pie-hover and table-hover use the same translate.
        const [ox, oy] = polar(0, 0, explode, mid);
        path.dataset.tx = ox.toFixed(2);
        path.dataset.ty = oy.toFixed(2);

        path.addEventListener("mouseenter", e => {
            setHover(container, sl.label);
            showTip(e, `${sl.label}: ${sl.valueText} — ${sl.pct}%`);
        });
        path.addEventListener("mousemove", e => moveTip(e));
        // click toggles a "stuck" popped-out state
        path.addEventListener("click", () => {
            path.classList.toggle("ba-slice-stuck");
            reconcile(container);
        });
        // no native <title> — it would double up with the custom .ba-tip popup

        svg.appendChild(path);
        start = end;
    });

    // Safety net: whenever the pointer leaves the whole pie, clear the hover. This is the
    // guaranteed catch for a missed per-slice leave — mouseleave on the <svg> always fires
    // when the cursor exits its bounding box.
    svg.addEventListener("mouseleave", () => { setHover(container, null); hideTip(); });

    // centre value (no "Total" label — just the figure)
    if (total > 0) {
        const val = document.createElementNS(SVGNS, "text");
        val.setAttribute("x", cx); val.setAttribute("y", cy + 6);
        val.setAttribute("text-anchor", "middle");
        val.setAttribute("class", "ba-pie-centre");
        val.textContent = totalText;
        svg.appendChild(val);
    }

    reconcile(container);
}

// Set the single hovered dim for this pie and re-apply the whole visual state. Passing
// null clears the transient hover (stuck slices stay out).
function setHover(container, dim) {
    if (container._hoverDim === dim) return;
    container._hoverDim = dim;
    reconcile(container);
}

// Ground truth: every slice is popped out iff it is the hovered dim OR it is stuck; the
// matching table row is lit iff it is the hovered dim. Rebuilt from scratch each call so no
// slice can be left in a stale state by a dropped event.
function reconcile(container) {
    const hovered = container._hoverDim;
    container.querySelectorAll(".ba-slice").forEach(path => {
        const out = path.dataset.dim === hovered || path.classList.contains("ba-slice-stuck");
        if (out) path.setAttribute("transform", `translate(${path.dataset.tx},${path.dataset.ty})`);
        else path.removeAttribute("transform");
    });
    // sync the table-row highlight to whatever (if anything) is hovered
    const root = container.closest(".ba-grid") || document;
    root.querySelectorAll("tr[data-dim]").forEach(row => {
        row.classList.toggle("ba-row-hl", row.dataset.dim === hovered);
    });
}

// ---------------------------------------------------------------------------
// 8-year grouped trend bars — drawn client-side and MEASURED so it fills its host
// at any resolution. We size the SVG's viewBox to the host's real pixel box and use
// preserveAspectRatio="none" (1 unit == 1px), so the chart never letterboxes (the empty
// band / thin bars a fixed viewBox + "meet" produced). A ResizeObserver redraws on resize.
//
// data = { ticks:[{frac,label}], groups:[{label, bars:[{frac,colour,tip}]}] }
// frac is 0..1 (fraction of the axis top / plot height) — computed in C# so the "nice"
// axis and £ formatting stay single-sourced there.
// ---------------------------------------------------------------------------
export function renderTrend(host, data) {
    if (!host) return;
    ensureTip();
    host._trendData = data;                 // cache so the ResizeObserver can redraw

    if (!host._trendRO) {
        let raf = 0;
        host._trendRO = new ResizeObserver(() => {
            cancelAnimationFrame(raf);
            raf = requestAnimationFrame(() => drawTrend(host));   // debounce to one per frame
        });
        host._trendRO.observe(host);
    }
    drawTrend(host);
}

function drawTrend(host) {
    const data = host._trendData;
    if (!data) return;

    // measure the real box; clamp so a momentarily-collapsed host can't draw degenerate
    const W = Math.max(host.clientWidth, 320);
    const H = Math.max(host.clientHeight, 160);

    const svg = document.createElementNS(SVGNS, "svg");
    svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
    svg.setAttribute("preserveAspectRatio", "none");   // 1 unit == 1px, fill the box exactly
    svg.setAttribute("class", "ba-trendsvg");

    const padL = 56, padR = 10, padT = 10, padB = 26;
    const plotW = Math.max(W - padL - padR, 10);
    const plotH = Math.max(H - padT - padB, 10);
    const groups = data.groups || [];
    const nYears = groups.length ? (groups[0].bars || []).length : 0;

    const groupW = plotW / Math.max(groups.length, 1);
    // bar width clamped so bars are never hair-thin or absurdly wide (Formed's approach)
    const slot = groupW * 0.82 / Math.max(nYears, 1);
    const barW = Math.max(1.5, Math.min(slot - 0.5, 40));
    // font sizes track the group width but stay in a legible band
    const axisFS = clamp(9, groupW * 0.12, 12);
    const nameFS = clamp(9, groupW * 0.14, 13);

    // y gridlines + £ tick labels
    (data.ticks || []).forEach(t => {
        const yy = padT + plotH * (1 - t.frac);
        const line = el("line", { x1: padL, x2: W - padR, y1: yy, y2: yy, class: "ba-axisline" });
        svg.appendChild(line);
        const tx = el("text", { x: padL - 6, y: yy + 3, "text-anchor": "end", class: "ba-axistext" });
        tx.style.fontSize = axisFS + "px";
        tx.textContent = t.label;
        svg.appendChild(tx);
    });

    // grouped bars + dimension labels
    groups.forEach((g, gi) => {
        const gx = padL + gi * groupW;
        const groupInner = groupW * 0.82;
        const startX = gx + (groupW - groupInner) / 2;
        (g.bars || []).forEach((b, bi) => {
            const h = Math.max(0, b.frac * plotH);
            const x = startX + bi * (groupInner / Math.max(nYears, 1)) + (slot - barW) / 2;
            const y = padT + plotH - h;
            const rect = el("rect", {
                class: "ba-bar", x: x.toFixed(1), y: y.toFixed(1),
                width: barW.toFixed(1), height: h.toFixed(1), fill: b.colour
            });
            const text = b.tip || "";
            rect.addEventListener("mouseenter", e => showTip(e, text));
            rect.addEventListener("mousemove", e => moveTip(e));
            rect.addEventListener("mouseleave", () => hideTip());
            svg.appendChild(rect);
        });
        // dimension label under the group (ellipsised in JS so it never overruns the slot)
        const label = el("text", {
            x: (gx + groupW / 2).toFixed(1), y: H - 8,
            "text-anchor": "middle", class: "ba-axistext"
        });
        label.style.fontSize = nameFS + "px";
        label.textContent = fitLabel(g.label, groupW);
        svg.appendChild(label);
    });

    host.replaceChildren(svg);
}

function clamp(lo, v, hi) { return Math.max(lo, Math.min(v, hi)); }

// small helper: create an SVG element with attributes
function el(name, attrs) {
    const e = document.createElementNS(SVGNS, name);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    return e;
}

// crude fit: allow ~1 char per 7.5px of group width, ellipsise beyond that
function fitLabel(s, groupW) {
    const maxChars = Math.max(4, Math.floor(groupW / 7.5));
    return s.length > maxChars ? s.slice(0, maxChars - 1) + "…" : s;
}

// stop observing when the page/component goes away
export function disposeTrend(host) {
    if (host && host._trendRO) { host._trendRO.disconnect(); host._trendRO = null; }
}

// Table row hover -> drive the SAME single hovered-dim state the pie uses, so the slice
// pops and the row lights from one code path. `on=false` only clears if this row is still
// the hovered one (guards against a leave arriving after the pointer already moved to the
// pie or another row and set a new hover).
export function hoverDim(container, dim, on) {
    if (!container) return;
    if (on) setHover(container, dim);
    else if (container._hoverDim === dim) setHover(container, null);
}

function showTip(e, text) { const t = ensureTip(); t.textContent = text; t.style.opacity = "1"; moveTip(e); }
function moveTip(e) { if (!tip) return; tip.style.left = (e.clientX + 12) + "px"; tip.style.top = (e.clientY - 10) + "px"; }
function hideTip() { if (tip) tip.style.opacity = "0"; }
