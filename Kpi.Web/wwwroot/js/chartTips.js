// Chart labels (Auto Approval, 26 Nov 2026): any element with data-tip shows its words in a small panel - beside the
// mouse while it is over it, or where a finger tapped (a tap elsewhere closes it). Listens on the document, so the
// parts Blazor redraws keep working. A new line in data-tip starts a new line; the first line is bold.
(function () {
    let tip = null, pinned = false, on = null;   // on = the part shown (a donut slice pops out while shown)
    function panel() {
        if (!tip) { tip = document.createElement("div"); tip.className = "chart-tip"; tip.hidden = true; document.body.appendChild(tip); }
        return tip;
    }
    function show(el, x, y) {
        const t = panel(), lines = el.getAttribute("data-tip").split("\n");
        t.replaceChildren(...lines.map((l, i) => { const d = document.createElement(i ? "div" : "b"); d.textContent = l; return d; }));
        t.hidden = false;
        if (on && on !== el) on.classList.remove("tip-on");
        on = el; el.classList.add("tip-on");
        place(x, y);
    }
    function place(x, y) {
        const t = panel(), w = t.offsetWidth, h = t.offsetHeight;
        t.style.left = Math.max(8, Math.min(x + 14, innerWidth - w - 8)) + "px";
        t.style.top = (y + 16 + h > innerHeight ? y - h - 10 : y + 16) + "px";
    }
    function hide() { if (tip) tip.hidden = true; pinned = false; if (on) { on.classList.remove("tip-on"); on = null; } }
    document.addEventListener("pointerover", e => {
        if (e.pointerType !== "mouse") return;
        const el = e.target.closest && e.target.closest("[data-tip]");
        if (el) { pinned = false; show(el, e.clientX, e.clientY); }
    });
    document.addEventListener("pointermove", e => { if (e.pointerType === "mouse" && tip && !tip.hidden && !pinned) place(e.clientX, e.clientY); });
    document.addEventListener("pointerout", e => {
        if (e.pointerType !== "mouse" || pinned) return;
        const el = e.target.closest && e.target.closest("[data-tip]");
        if (el && !(e.relatedTarget && el.contains(e.relatedTarget))) hide();
    });
    document.addEventListener("click", e => {
        const el = e.target.closest && e.target.closest("[data-tip]");
        if (el) { show(el, e.clientX, e.clientY); pinned = true; } else if (pinned) hide();
    });
    addEventListener("scroll", () => { if (pinned) hide(); }, true);
})();
