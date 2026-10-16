// A PHONE THAT SLEPT COMES BACK QUIETLY (Steve, Oct 2026: "when I don't use my phone for a short while the website loses
// connection and needs to reconnect"). The site runs over one live connection (Blazor Server); a sleeping phone drops
// it. The server keeps the person's place for 30 minutes after that (DisconnectedCircuitRetentionPeriod, Program.cs), so:
//   - back on the page: reconnect AT ONCE (Blazor would otherwise wait for its next retry), and
//   - if the server no longer has their place (past the 30 minutes, or the site restarted): reload the page, rather
//     than leaving Blazor's "reload the page" message for them to press.
// Blazor drives #components-reconnect-modal (App.razor) by its classes: -show while it tries, -failed when it has given
// up, -rejected when the server no longer knows the connection. account.css draws it as a small "Reconnecting…" note.
(function () {
    if (window.__kpiReconnectWired) return;
    window.__kpiReconnectWired = true;

    var reloading = false;
    function modal() { return document.getElementById('components-reconnect-modal'); }
    function reload() { if (!reloading) { reloading = true; location.reload(); } }

    function reconnectNow() {
        var m = modal();
        if (!m || !(m.classList.contains('components-reconnect-show') || m.classList.contains('components-reconnect-failed'))) return;
        if (!window.Blazor || typeof window.Blazor.reconnect !== 'function') { reload(); return; }
        window.Blazor.reconnect().then(function (ok) { if (!ok) reload(); }, reload);
    }

    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') reconnectNow();
    });
    window.addEventListener('pageshow', function (e) { if (e.persisted) reconnectNow(); });

    // Blazor gave up (no network, say): the note says so and a tap tries again - never an automatic loop
    document.addEventListener('click', function (e) {
        var t = e.target instanceof Element ? e.target : null;
        if (t && t.closest('#components-reconnect-modal.components-reconnect-failed')) reconnectNow();
    });

    function watch() {
        var m = modal();
        if (!m) return;
        new MutationObserver(function () {
            if (m.classList.contains('components-reconnect-rejected')) reload();
        }).observe(m, { attributes: true, attributeFilter: ['class'] });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', watch);
    else watch();
})();
