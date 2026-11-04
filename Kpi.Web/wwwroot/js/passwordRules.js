// THE PASSWORD RULES TICK THEMSELVES OFF (4 Nov 2026, Steve - DavidsonHR's, copied). Under every new-password box sits a
// PasswordRules list naming its box (data-pw-rules-for); each rule carries what to test (data-pw-check, from
// KpiIdentityRules). As the box changes, a met rule gets the "met" class - the CSS swaps its bullet for a tick in the same
// spot, so no text moves - and its hidden text says "done" for a screen reader.
//
// The tests mirror ASP.NET Identity's, which are ASCII-only: an uppercase letter is A-Z, a number 0-9. Length is UTF-16
// units on both sides. Delegated at document level, so it works on the static sign-in pages and on My Account alike.
// A guide only - the server checks everything.
(function () {
    function meets(check, value) {
        if (check.indexOf('length:') === 0) return value.length >= parseInt(check.slice(7), 10);
        switch (check) {
            case 'upper': return /[A-Z]/.test(value);
            case 'lower': return /[a-z]/.test(value);
            case 'digit': return /[0-9]/.test(value);
            case 'symbol': return /[^A-Za-z0-9]/.test(value);
            default: return false;
        }
    }

    function update(input) {
        if (!input.id) return;
        var lists = document.querySelectorAll('[data-pw-rules-for="' + input.id + '"]');
        for (var i = 0; i < lists.length; i++) {
            var rules = lists[i].querySelectorAll('[data-pw-check]');
            for (var j = 0; j < rules.length; j++) {
                var met = meets(rules[j].getAttribute('data-pw-check'), input.value || '');
                rules[j].classList.toggle('met', met);
                var state = rules[j].querySelector('[data-pw-state]');
                if (state) state.textContent = met ? ' (done)' : '';
            }
        }
    }

    document.addEventListener('input', function (e) {
        if (e.target instanceof HTMLInputElement) update(e.target);
    });

    // a password manager or the browser can fill the box before anyone types, and a page can come back from the
    // history with the box still filled - tick those off too
    function all() {
        var lists = document.querySelectorAll('[data-pw-rules-for]');
        for (var i = 0; i < lists.length; i++) {
            var input = document.getElementById(lists[i].getAttribute('data-pw-rules-for'));
            if (input) update(input);
        }
    }
    document.addEventListener('DOMContentLoaded', all);
    window.addEventListener('pageshow', all);
})();
