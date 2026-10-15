// Annual margin chart — Margin as bars, Buy Rate & Sell Rate as lines, per year. Drawn
// client-side (SVG) like the Business Analysis charts. The page hands over the annual points
// (rates as fractions) + colours; we draw to fill the host. A ResizeObserver redraws on resize.
// Each year band has an invisible hit-rect wired to the shared styled tooltip (kpiTooltip.js),
// the same hover treatment as Sales Performance.
//   data = { points:[{label, margin, buy, sell}], marginColour, buyColour, sellColour }

import { showTip, hideTip } from "/js/kpiTooltip.js";

const SVGNS = "http://www.w3.org/2000/svg";
const pct = v => (v * 100).toFixed(2) + "%";

export function renderMarginChart(host, data){
  if (!host) return;
  host._marginData = data;
  if (!host._ro){
    let raf = 0;
    host._ro = new ResizeObserver(() => { cancelAnimationFrame(raf); raf = requestAnimationFrame(() => draw(host)); });
    host._ro.observe(host);
  }
  draw(host);
}

function el(name, attrs){ const e = document.createElementNS(SVGNS, name); for (const k in attrs) e.setAttribute(k, attrs[k]); return e; }

function draw(host){
  const data = host._marginData;
  if (!data || !data.points || !data.points.length) { host.replaceChildren(); return; }
  const pts = data.points;

  const W = Math.max(host.clientWidth, 320);
  const H = Math.max(host.clientHeight, 200);
  const padL = 48, padR = 14, padT = 12, padB = 26;
  const plotW = Math.max(W - padL - padR, 10);
  const plotH = Math.max(H - padT - padB, 10);

  // y-axis: 0 up to a nice ceiling above the biggest rate on show (buy/sell/margin)
  const maxRate = Math.max(...pts.flatMap(p => [p.buy, p.sell, p.margin]), 0.01);
  const top = niceCeil(maxRate);
  const y = v => padT + plotH * (1 - v / top);
  const n = pts.length;
  const bandW = plotW / n;
  const barW = Math.min(bandW * 0.5, 46);

  const svg = el("svg", { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: "none", class: "ma-chart" });

  // y gridlines + % labels (5 ticks)
  for (let i = 0; i <= 5; i++){
    const v = top * i / 5;
    const yy = y(v);
    svg.appendChild(el("line", { x1: padL, x2: W - padR, y1: yy, y2: yy, class: "ma-axisline" }));
    const t = el("text", { x: padL - 6, y: yy + 3, "text-anchor": "end", class: "ma-axistext" });
    t.textContent = (v * 100).toFixed(0) + "%";
    svg.appendChild(t);
  }

  // margin bars
  pts.forEach((p, i) => {
    const cx = padL + bandW * (i + 0.5);
    const h = Math.max(0, (p.margin / top) * plotH);
    svg.appendChild(el("rect", {
      x: (cx - barW / 2).toFixed(1), y: (padT + plotH - h).toFixed(1),
      width: barW.toFixed(1), height: h.toFixed(1), fill: data.marginColour, rx: 2
    }));
    const xlab = el("text", { x: cx.toFixed(1), y: H - 8, "text-anchor": "middle", class: "ma-axistext" });
    xlab.textContent = p.label;
    svg.appendChild(xlab);
  });

  // buy + sell lines with markers
  const line = (key, colour) => {
    const d = pts.map((p, i) => {
      const cx = padL + bandW * (i + 0.5);
      return `${i === 0 ? "M" : "L"}${cx.toFixed(1)},${y(p[key]).toFixed(1)}`;
    }).join(" ");
    svg.appendChild(el("path", { d, fill: "none", stroke: colour, "stroke-width": 2.5, "stroke-linejoin": "round" }));
    pts.forEach((p, i) => {
      const cx = padL + bandW * (i + 0.5);
      svg.appendChild(el("circle", { cx: cx.toFixed(1), cy: y(p[key]).toFixed(1), r: 3.5, fill: colour }));
    });
  };
  line("buy", data.buyColour);
  line("sell", data.sellColour);

  // Invisible per-year hit-rects on top, wired to the shared styled tooltip (as Sales
  // Performance). Tip lines are real "\n" — .kpi-tip renders them via white-space:pre-line.
  pts.forEach((p, i) => {
    const hit = el("rect", {
      x: (padL + bandW * i).toFixed(1), y: padT.toFixed(1),
      width: bandW.toFixed(1), height: plotH.toFixed(1),
      fill: "transparent", style: "cursor:default"
    });
    const tip = `${p.label}\nMargin: ${pct(p.margin)}\nBuy Rate: ${pct(p.buy)}\nSell Rate: ${pct(p.sell)}`;
    hit.addEventListener("mouseenter", e => showTip(e, tip));
    hit.addEventListener("mousemove", e => showTip(e, tip));
    hit.addEventListener("mouseleave", hideTip);
    svg.appendChild(hit);
  });

  host.replaceChildren(svg);
}

// round a rate fraction up to a clean % ceiling (…, 10%, 12.5%, 15%, 20%)
function niceCeil(v){
  const pct = v * 100;
  const steps = [1, 2, 2.5, 5, 7.5, 10, 12.5, 15, 20, 25, 30, 40, 50];
  for (const s of steps) if (pct <= s) return s / 100;
  return Math.ceil(pct / 10) * 10 / 100;
}

export function dispose(host){
  if (host && host._ro){ host._ro.disconnect(); host._ro = null; }
}
