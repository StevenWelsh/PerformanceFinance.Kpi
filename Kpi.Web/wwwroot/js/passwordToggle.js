// Password-reveal toggle: a button with data-pw-toggle="<input-id>" next to a password input (the .login-pw-wrap
// pattern in app.css) flips the input between password and text and swaps the bi-eye / bi-eye-slash icon.
// One listener on the document, loaded once by App.razor, so it also works for fields a live (interactive) page
// draws after load - My Account - as well as the static account pages.
(function () {
    if (window.__pwToggleWired) return;
    window.__pwToggleWired = true;
    document.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('[data-pw-toggle]') : null;
        if (!btn) return;
        var input = document.getElementById(btn.getAttribute('data-pw-toggle'));
        if (!input) return;
        var show = input.type === 'password';
        input.type = show ? 'text' : 'password';
        btn.setAttribute('aria-label', show ? 'Hide password' : 'Show password');
        var icon = btn.querySelector('i');
        if (icon) {
            icon.classList.toggle('bi-eye', !show);
            icon.classList.toggle('bi-eye-slash', show);
        }
    });
})();
