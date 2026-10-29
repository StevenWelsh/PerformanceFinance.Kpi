// Page Visibility bridge for KpiPanel. Lets the panel pause its 10-minute refresh
// timer when the KPI tab isn't actually being looked at — minimised, another
// browser tab in front, or another app covering it — and catch up on return.
// "Hidden" here means obscured, not closed (a closed page disposes the component,
// which unhooks this in dispose()).

const registry = new Map();

export function register(id, dotNet) {
    const handler = () => {
        // document.hidden is true when the page is not the foreground tab/window.
        dotNet.invokeMethodAsync("OnVisibilityChanged", !document.hidden);
    };
    document.addEventListener("visibilitychange", handler);
    registry.set(id, handler);
    // Report the initial state so the panel starts in the right mode.
    handler();
}

// KPI site (19 Oct 2026): how this report is being looked at, for Settings > Audit > Report views - the Home Screen
// app (standalone display), a phone-sized screen, or an ordinary browser.
export function clientKind() {
    if (window.matchMedia("(display-mode: standalone)").matches || window.navigator.standalone === true) return "App";
    if (window.matchMedia("(max-width: 767px)").matches) return "Phone";
    return "Browser";
}

export function unregister(id) {
    const handler = registry.get(id);
    if (handler) {
        document.removeEventListener("visibilitychange", handler);
        registry.delete(id);
    }
    untrack(id);
}

// ---- KPI site (29 Oct 2026, Steve): what people DO on a report, for Settings > Audit > Report activity --------------
// Listens on the whole document, so the copied report pages need nothing added:
//   - a FILTER chosen: every filter is a KpiField (a <select> inside .kpim-field, labelled by its caption) -> its
//     caption and the chosen option's text ("Division" = "TFI");
//   - a VIEW switched: KpiSegmented (.kpim-seg, labelled) -> its label and the button's text ("Years");
//   - a BUTTON pressed: anything clickable (a button or link, or an element with a pointer cursor, e.g. a heat map row)
//     -> its title, label or text - the timelapse's title is "Play 2025 timelapse", so the year comes with it.
// Each comes with the report's filters at that moment. Menus (the sidebar, the phone menu, the avatar menu) are left out:
// moving between reports is already recorded as each one opening. The same event twice within a second counts once,
// and a page sends at most 500.
const trackers = new Map();
const SKIP = ".kpi-sidebar, .kpim-menu, .um, [data-kpi-notrack]";

export function track(id, dotNet) {
    let sent = 0, last = "", lastAt = 0;
    const send = (kind, label, value) => {
        label = (label || "").replace(/\s+/g, " ").trim().slice(0, 200);
        if (!label || sent >= 500) return;
        const sig = kind + "|" + label + "|" + (value || ""), now = Date.now();
        if (sig === last && now - lastAt < 1000) return;
        last = sig; lastAt = now; sent++;
        dotNet.invokeMethodAsync("OnActivity", kind, label, value || null, filters()).catch(() => { });
    };

    const onChange = e => {
        const sel = e.target;
        if (!(sel instanceof HTMLSelectElement) || sel.closest(SKIP)) return;
        const field = sel.closest(".kpim-field");
        const caption = sel.getAttribute("aria-label") || field?.querySelector(".kpim-field-caption")?.textContent;
        const chosen = sel.selectedOptions[0]?.textContent;
        setTimeout(() => send("Filter", caption || "Filter", chosen), 400);   // after the page redraws: filters() then shows it
    };

    const onClick = e => {
        const t = e.target instanceof Element ? e.target : null;
        if (!t || t.closest(SKIP) || t.closest(".kpim-field")) return;   // a filter's own click: its change says it
        const seg = t.closest(".kpim-seg button");
        if (seg) { send("View", seg.closest(".kpim-seg").getAttribute("aria-label") || "View", seg.textContent); return; }
        const el = clickable(t);
        if (el) send("Button", describe(el));
    };

    document.addEventListener("change", onChange, true);
    document.addEventListener("click", onClick, true);
    trackers.set(id, () => {
        document.removeEventListener("change", onChange, true);
        document.removeEventListener("click", onClick, true);
    });
}

function untrack(id) {
    trackers.get(id)?.();
    trackers.delete(id);
}

// the thing actually clicked: a button, link or tab - or the nearest element (within a few levels) shown as clickable
function clickable(t) {
    const direct = t.closest("button, a[href], [role=button], [role=tab], summary, input[type=checkbox], input[type=radio]");
    if (direct) return direct;
    for (let el = t, i = 0; el && el !== document.body && i < 5; el = el.parentElement, i++)
        if (getComputedStyle(el).cursor === "pointer") return el;
    return null;
}

function describe(el) {
    const own = el.getAttribute("title") || el.getAttribute("aria-label");
    if (own) return own;
    const text = (el.innerText || el.textContent || "").replace(/\s+/g, " ").trim();
    if (el.tagName === "TR") return "Row: " + (el.cells[0]?.innerText || text).trim();
    if (text) return text.slice(0, 120);
    const icon = el.querySelector("i.bi")?.className.match(/bi-[\w-]+/g)?.find(c => c !== "bi");
    return icon ? icon.replace("bi-", "").replace(/-/g, " ") : el.tagName.toLowerCase();
}

// the filters on show now: "Measure: AF · Division: All · Year: 2025" (only visible ones - a page keeps its phone and
// full views both, and only one is on screen)
function filters() {
    const parts = [];
    for (const f of document.querySelectorAll(".kpim-field")) {
        if (!f.offsetParent || f.closest(SKIP)) continue;
        const caption = f.querySelector(".kpim-field-caption")?.textContent?.trim();
        const value = f.querySelector(".kpim-field-value")?.textContent?.trim();
        if (caption && value && value !== "–") parts.push(`${caption}: ${value}`);
    }
    for (const s of document.querySelectorAll(".kpim-seg:not(.kpim-menu-seg)")) {
        if (!s.offsetParent || s.closest(SKIP)) continue;
        const on = s.querySelector("button.kpim-on")?.textContent?.trim();
        if (on) parts.push(`${s.getAttribute("aria-label") || "View"}: ${on}`);
    }
    return [...new Set(parts)].join(" · ").slice(0, 1000) || null;
}
