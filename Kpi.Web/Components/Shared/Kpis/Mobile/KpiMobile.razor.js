// Tells KpiMobile whether the phone view is showing - the same media query kpi.css uses - and again
// whenever that changes (a rotation, a window resized across the line).
const PHONE_QUERY = "(max-width: 599px), (max-height: 499px) and (orientation: landscape) and (pointer: coarse)";
const watches = new Map();

export function watch(id, dotnetRef) {
    const mql = window.matchMedia(PHONE_QUERY);
    const onChange = () => dotnetRef.invokeMethodAsync("SetPhone", mql.matches).catch(() => { });
    mql.addEventListener("change", onChange);
    watches.set(id, { mql, onChange });
    return mql.matches;
}

export function unwatch(id) {
    const w = watches.get(id);
    if (!w) return;
    w.mql.removeEventListener("change", w.onChange);
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
