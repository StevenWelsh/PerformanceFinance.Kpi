// Shared KPI chart tooltip — the styled, cursor-following hover popup used across the KPI charts
// (Business Analysis pie/trend, Sales Performance columns, …). One tooltip element is appended to
// <body> and reused; it appears instantly (a 0.1s opacity fade, matching Business Analysis) and
// follows the cursor. Import this instead of re-implementing per chart.
//
//   import { showTip, hideTip, moveTip } from "/js/kpiTooltip.js";
//   el.addEventListener("mouseenter", e => showTip(e, "Susan Marshall\nTFI: £647,148"));
//   el.addEventListener("mousemove",  moveTip);
//   el.addEventListener("mouseleave", hideTip);
//
// `text` may contain "\n" for multiple lines (the .kpi-tip class renders them with white-space).

let tip;   // the single shared tooltip element

function ensureTip() {
    if (tip) return tip;
    tip = document.createElement("div");
    tip.className = "kpi-tip";
    document.body.appendChild(tip);
    return tip;
}

export function showTip(e, text) {
    const t = ensureTip();
    t.textContent = text;          // textContent is XSS-safe; \n honoured by the CSS white-space
    t.style.opacity = "1";
    moveTip(e);
}

export function moveTip(e) {
    if (!tip) return;
    tip.style.left = (e.clientX + 12) + "px";
    tip.style.top = (e.clientY - 10) + "px";
}

export function hideTip() {
    if (tip) tip.style.opacity = "0";
}
