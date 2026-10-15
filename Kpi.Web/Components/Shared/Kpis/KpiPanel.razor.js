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

export function unregister(id) {
    const handler = registry.get(id);
    if (handler) {
        document.removeEventListener("visibilitychange", handler);
        registry.delete(id);
    }
}
