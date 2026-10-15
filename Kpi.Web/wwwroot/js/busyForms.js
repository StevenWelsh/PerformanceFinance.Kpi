// "SIGNING IN…" - the pressed button says it is working (Steve, Oct 2026; DavidsonHR's busy-forms.js).
//
// The account pages are static, so a post is a full page navigation, and until the next page arrives the old one sits
// there looking untouched. A sign-in reaches Azure SQL and a code is checked, which can take a few seconds, and a
// button that shows nothing invites a second press. So the pressed button changes to its data-busy-text (or
// "Please wait…"), dims (.is-busy, account.css) and swallows further submits until the next page replaces it.
//
// Scoped to the sign-in card (.login-page, AuthCard) and My Account (.acc-page): every form there gets it with nothing
// to remember on a new one. Opt a form out with data-no-busy. One listener on the document, loaded once by App.razor,
// so it also sees forms a live page draws after load.
//
// A convenience, not a control: a determined double post still reaches the server.
(function () {
    if (window.__busyFormsWired) return;
    window.__busyFormsWired = true;

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!(form instanceof HTMLFormElement)) return;
        if (!form.closest('.login-page, .acc-page') || form.hasAttribute('data-no-busy')) return;
        if ((form.getAttribute('method') || '').toLowerCase() !== 'post') return;

        // already on its way: swallow the second press rather than posting twice
        if (form.dataset.busy) { e.preventDefault(); return; }

        // something else cancelled it, or the browser's own required checks would have: a blocked submit must not
        // leave a button stuck saying it is working
        if (e.defaultPrevented || (form.checkValidity && !form.checkValidity())) return;

        form.dataset.busy = '1';
        var btn = e.submitter || form.querySelector('button[type="submit"]');
        if (!btn) return;
        btn.dataset.originalText = btn.innerHTML;
        btn.textContent = btn.getAttribute('data-busy-text') || 'Please wait…';
        // aria-disabled, NOT disabled: a disabled button is left out of the post
        btn.setAttribute('aria-disabled', 'true');
        btn.classList.add('is-busy');
    });

    // Back after signing in: the browser can restore a page from its back-forward cache exactly as it was left - button
    // still reading "Signing in…", every submit swallowed. A restored page gets its buttons back.
    window.addEventListener('pageshow', function (e) {
        if (!e.persisted) return;
        document.querySelectorAll('.login-page form[data-busy], .acc-page form[data-busy]').forEach(function (form) {
            delete form.dataset.busy;
            form.querySelectorAll('.is-busy').forEach(function (btn) {
                if (btn.dataset.originalText !== undefined) btn.innerHTML = btn.dataset.originalText;
                btn.removeAttribute('aria-disabled');
                btn.classList.remove('is-busy');
            });
        });
    });
})();
