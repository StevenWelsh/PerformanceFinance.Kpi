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
}
