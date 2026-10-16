// "ADD TO HOME SCREEN" (Steve, Oct 2026). Added to a phone's Home Screen the site opens full screen, with no address
// bar (manifest.webmanifest + the tags in App.razor's head). This offers it:
//   - the avatar menu's "Add to Home Screen" (KpiUserMenu, class um-app) and
//   - a bar across the top, ONCE after each sign-in: AccountEndpoints.SignedIn sets the cookie below, this shows the bar
//     on the first page and clears the cookie, so it is not seen again until the next sign-in;
// both opening the same instructions (#kpi-app-how in App.razor): Safari's steps on an iPhone or iPad, the browser
// menu's on Android - with a real Install button when the browser offers one. Only on a phone or tablet, and never
// inside the app itself.
//
// The bar and the instructions live in App.razor OUTSIDE the Blazor app, and every click is caught here at the document,
// so nothing has to be wired when Blazor draws a page or the menu.
(function () {
    if (window.__kpiAppWired) return;
    window.__kpiAppWired = true;

    var TIP_COOKIE = 'kpi-app-tip';     // AccountEndpoints.AppTipCookie
    var html = document.documentElement;
    var standalone = window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
    var ua = navigator.userAgent;
    // an iPad says it is a Mac; it is the one "Mac" with a touch screen
    var ios = /iPhone|iPad|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    var android = /Android/.test(ua);
    var eligible = !standalone && (ios || android);

    html.classList.toggle('kpi-app-eligible', eligible);   // shows the menu item (account.css)
    html.classList.toggle('kpi-app-ios', ios);             // picks Safari's steps or the browser menu's

    // Android / Chrome: the browser's own install offer, kept for the Install button
    var installPrompt = null;
    window.addEventListener('beforeinstallprompt', function (e) {
        e.preventDefault();
        installPrompt = e;
        html.classList.add('kpi-app-can-install');
    });
    window.addEventListener('appinstalled', function () {
        installPrompt = null;
        html.classList.remove('kpi-app-can-install', 'kpi-app-eligible');
        hideBar();
    });

    function el(id) { return document.getElementById(id); }
    function hideBar() { var b = el('kpi-app-bar'); if (b) b.hidden = true; }
    function openHow() {
        hideBar();
        var d = el('kpi-app-how');
        if (d && !d.open) d.showModal();
    }
    function closeHow() { var d = el('kpi-app-how'); if (d && d.open) d.close(); }

    function hasTip() { return document.cookie.split('; ').some(function (c) { return c.indexOf(TIP_COOKIE + '=') === 0; }); }
    function clearTip() { document.cookie = TIP_COOKIE + '=; Max-Age=0; Path=/; Secure; SameSite=Lax'; }

    // the first page after a sign-in: show the bar (a phone not using the app), and spend the cookie either way
    function maybeShowBar() {
        if (!hasTip() || location.pathname.indexOf('/account') === 0) return;
        clearTip();
        var b = el('kpi-app-bar');
        if (b && eligible) b.hidden = false;
    }

    document.addEventListener('click', function (e) {
        var t = e.target instanceof Element ? e.target : null;
        if (!t) return;
        if (t.closest('[data-app-bar-close]')) { hideBar(); return; }
        if (t.closest('[data-app-how-close]')) { closeHow(); return; }
        if (t.closest('[data-app-install]')) {
            if (!installPrompt) return;
            installPrompt.prompt();
            installPrompt.userChoice.finally(function () {
                installPrompt = null;
                html.classList.remove('kpi-app-can-install');
                closeHow();
            });
            return;
        }
        if (t.closest('[data-app-how]')) { e.preventDefault(); openHow(); return; }
        // a tap on the dimmed page outside the instructions closes them (the dialog itself is the backdrop's target)
        if (t.id === 'kpi-app-how') {
            var r = t.getBoundingClientRect();
            if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) closeHow();
        }
    });

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', maybeShowBar);
    else maybeShowBar();
})();
