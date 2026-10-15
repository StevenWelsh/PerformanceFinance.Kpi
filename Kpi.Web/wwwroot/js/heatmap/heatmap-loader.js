// Loads the Heat Map's non-module dependencies (Leaflet + the UK/Ireland geo data) on demand,
// once, before the HeatMap.razor.js module builds the map. These are plain scripts that set
// window.L / window.AREAS_GEOJSON / window.IRELAND_GEOJSON, so they can't be ES-imported.
window.cmsHeatMapEnsureAssets = (function () {
    let ready;
    function load(src) {
        return new Promise((resolve, reject) => {
            if (document.querySelector('script[data-hm="' + src + '"]')) { resolve(); return; }
            const s = document.createElement('script');
            s.src = src; s.async = false; s.dataset.hm = src;
            s.onload = resolve; s.onerror = () => reject(new Error('failed to load ' + src));
            document.head.appendChild(s);
        });
    }
    return function () {
        // idempotent: build the chain once and reuse the promise
        ready = ready || (async () => {
            if (!window.L) await load('lib/leaflet/leaflet.js');
            if (!window.AREAS_GEOJSON) await load('js/heatmap/geo.js');
            if (!window.IRELAND_GEOJSON) await load('js/heatmap/ireland.js');
        })();
        return ready;
    };
})();

// read a <details>'s open state (Blazor @ontoggle fires for open AND close)
window.cmsDetailsIsOpen = function (el) { return !!(el && el.open); };

// close any open .hm-yearsdd when the user clicks outside it (native <details> only closes on
// its own summary). Closing fires @ontoggle, which is where the page reloads if the selection
// changed — so an outside click both dismisses the popup and commits the pending years.
document.addEventListener('click', function (e) {
    document.querySelectorAll('details.hm-yearsdd[open]').forEach(function (d) {
        if (!d.contains(e.target)) d.open = false;
    });
}, true);
