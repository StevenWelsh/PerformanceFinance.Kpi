// Tells KpiMobile whether the phone view is showing - the same media query kpi.css uses - and again
// whenever that changes (a rotation, a window resized across the line); and when the phone is turned.
const PHONE_QUERY = "(max-width: 599px), (max-height: 499px) and (orientation: landscape) and (pointer: coarse)";
const watches = new Map();

// ...and every time the phone is turned (Steve, 3 Nov 2026: the folded filters open again, then fold after the same few
// seconds). The device's own orientation where the browser has it - an on-screen keyboard can make the page wider than
// it is tall, which the orientation media query would take for a turn.
function watchTurns(dotnetRef) {
    const onTurn = () => dotnetRef.invokeMethodAsync("Turned").catch(() => { });
    const target = screen.orientation ?? window.matchMedia("(orientation: landscape)");
    target.addEventListener("change", onTurn);
    return () => target.removeEventListener("change", onTurn);
}

export function watch(id, dotnetRef) {
    const mql = window.matchMedia(PHONE_QUERY);
    const onChange = () => dotnetRef.invokeMethodAsync("SetPhone", mql.matches).catch(() => { });
    mql.addEventListener("change", onChange);
    watches.set(id, { mql, onChange, unwatchTurns: watchTurns(dotnetRef) });
    return mql.matches;
}

export function unwatch(id) {
    const w = watches.get(id);
    if (!w) return;
    w.mql.removeEventListener("change", w.onChange);
    w.unwatchTurns();
    watches.delete(id);
}

// The phone's favourite reports (KpiFavourites) - kept in this browser only, so each device has its own.
const FAVOURITES_KEY = "kpi-favourites";

export function loadFavourites() {
    try {
        const v = JSON.parse(localStorage.getItem(FAVOURITES_KEY) ?? "[]");
        return Array.isArray(v) ? v.filter(s => typeof s === "string") : [];
    } catch {
        return [];
    }
}

export function saveFavourites(slugs) {
    try { localStorage.setItem(FAVOURITES_KEY, JSON.stringify(slugs)); } catch { }
}

// The menu view the user last picked ("favourites" / "all"), or null when they never have.
const MENU_VIEW_KEY = "kpi-menu-view";

export function loadMenuView() {
    try { return localStorage.getItem(MENU_VIEW_KEY); } catch { return null; }
}

export function saveMenuView(view) {
    try { localStorage.setItem(MENU_VIEW_KEY, view); } catch { }
}
