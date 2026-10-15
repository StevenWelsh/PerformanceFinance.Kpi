// Sales League "timelapse" bar race. Blazor hands over the weekly sales buckets for a year; we
// build each salesperson's cumulative year-to-date series and animate a horizontal-bar race —
// bars grow, reorder as people overtake each other, with a running £ ticker and the week date.
// Controls (owned by Blazor markup, driven here): play/pause, a scrubber to seek any week, and a
// speed toggle (1x / 2x / 4x). Our colour scheme, initials not photos, no black background.
//
// payload = { year, measure, people:[id], names:{id:name}, weeks:[{id, wk, end, val}], targets, compact }
// compact = the phone's landscape race: shorter rows (its CSS, .kpim-sp-race, shrinks the bars to match).

const PALETTE = ["#2b579a", "#d20d43", "#10B981", "#F59E0B", "#8B5CF6", "#06B6D4", "#F97316", "#EC4899"];
const ROW_H = 52, GAP = 16;               // bar row height + gap (px) — roomier now the table's gone
const ROW_H_COMPACT = 26, GAP_COMPACT = 8; // the phone's landscape race (payload.compact)
const WKS_TEXT = "Weeks @ #1: ";           // the weeks-at-#1 tally trailing each bar's £ value
const MS_PER_WEEK = 780;                   // time to advance one week (~20% slower than the first cut)
// avatar width + the gap before the £ label. Shared by measure() (which subtracts them from the
// track) and layout() (which offsets the target lines by BAR_X) so the lines can never drift out of
// step with the bars they are measured against.
const AV_W = 34, LABEL_GAP = 8;
const BAR_X = AV_W;                        // bars/lines start immediately after the avatar

// per-host race state, keyed off the host element
const races = new WeakMap();

const gbp = new Intl.NumberFormat("en-GB", { style: "currency", currency: "GBP", maximumFractionDigits: 0 });
function initials(name){ return name.split(/\s+/).filter(Boolean).map(w => w[0]).slice(0,2).join("").toUpperCase(); }

// Entry points take ELEMENT IDS (not ElementReferences) — see the .razor for why. Resolve live.
const $ = id => (id ? document.getElementById(id) : null);

// --- shared styled tooltip (same look + instant appear as Business Analysis) ---
import { showTip, moveTip, hideTip } from "/js/kpiTooltip.js";

// Wire the target chart columns' hover: each .slt-hit rect carries data-tip. Idempotent — safe to
// call after every render; re-binding replaces the old handlers via a marker flag. Called from the
// page's OnAfterRenderAsync so it re-runs when the measure/year data changes.
export function initTargetCharts(){
    document.querySelectorAll(".slt-tgtsvg .slt-hit").forEach(rect => {
        if (rect._tipWired) return;
        rect._tipWired = true;
        // data-tip already carries real newlines (HoverText joins with "\n"; HtmlEncode leaves LF
        // untouched), and .kpi-tip uses white-space:pre-line — so pass it through as-is.
        rect.addEventListener("mouseenter", e => showTip(e, rect.dataset.tip || ""));
        rect.addEventListener("mousemove", moveTip);
        rect.addEventListener("mouseleave", hideTip);
    });
}

// Returns true once the race is built, false if the host isn't in the DOM yet (DxPopup renders its
// body lazily on show, so an early OnAfterRender can call this before the element exists — the
// caller keeps retrying until it returns true).
export function initRace(hostId, scrubId, dateId, playId, payload){
  const host = $(hostId);
  if (!host) return false;
  disposeRace(hostId);
  const scrub = $(scrubId), dateSpan = $(dateId), playBtn = $(playId);

  // build cumulative series: cum[personId][weekNo] = running YTD total
  const people = payload.people.map(String);
  const names = payload.names || {};
  const maxWeek = Math.max(1, ...payload.weeks.map(w => w.wk));
  // week -> end date label (first seen)
  const weekEnd = {};
  payload.weeks.forEach(w => { if (!weekEnd[w.wk]) weekEnd[w.wk] = w.end; });

  const perWeek = {};                       // id -> {week -> value that week}
  people.forEach(id => perWeek[id] = {});
  payload.weeks.forEach(w => { perWeek[String(w.id)][w.wk] = (perWeek[String(w.id)][w.wk] || 0) + w.val; });

  const cum = {};                            // id -> [w0..maxWeek] cumulative
  let grandMax = 0;
  people.forEach(id => {
    const arr = [0]; let run = 0;
    for (let wk = 1; wk <= maxWeek; wk++){ run += (perWeek[id][wk] || 0); arr[wk] = run; }
    cum[id] = arr; grandMax = Math.max(grandMax, run);
  });

  // Per-person targets (optional): { id: {year, ytd} }. Only the YTD line is drawn here (the year
  // target is deliberately NOT shown in the race — it dwarfs part-year totals and wrecked the
  // scale). The bar scale stays the year-END cumulative leader, so the YTD line — which tracks the
  // pro-rata target as-of the current week (year * week/maxWeek) — always sits within the track.
  const targets = payload.targets || {};

  // leader (by cumulative total) at the end of each week 1..maxWeek. weekLeader[wk] = id | null.
  // Nobody is uniquely #1 at £0, so an all-£0 week has no leader. Cumulative weeks-at-#1 up to any
  // week is derived from this in the standings render (so the count grows live as the race plays).
  const weekLeader = [null];
  for (let wk = 1; wk <= maxWeek; wk++){
    let leader = null, best = 0;
    people.forEach(id => { const v = cum[id][wk] || 0; if (v > best){ best = v; leader = id; } });
    weekLeader[wk] = leader;
  }
  // the most weeks anyone leads by year-end: the widest tally the race will ever show (counts only grow)
  const ledWeeks = {};
  weekLeader.forEach(id => { if (id) ledWeeks[id] = (ledWeeks[id] || 0) + 1; });
  const maxWks = Math.max(0, ...Object.values(ledWeeks));
  const rowH = payload.compact ? ROW_H_COMPACT : ROW_H, gap = payload.compact ? GAP_COMPACT : GAP;

  // colour per person (stable by their order in the list)
  const colour = {};
  people.forEach((id, i) => colour[id] = PALETTE[i % PALETTE.length]);

  // DOM: a positioned track holding one row per person
  host.innerHTML = "";
  const track = document.createElement("div");
  track.className = "slt-track";
  track.style.height = (people.length * (rowH + gap)) + "px";
  const rows = {};
  people.forEach(id => {
    const row = document.createElement("div");
    row.className = "slt-bar-row";
    row.style.height = rowH + "px";
    // avatar (initials) caps the left of the pill; the running £ value sits OUTSIDE the bar to its
    // right, so it stays readable and visibly counts up even in early weeks when the bar (scaled to
    // the year-end total) is only a sliver.
    // the YTD-target line is absolutely positioned over the track, drawn BEFORE the bar in source
    // order but lifted above it with z-index so it stays visible when the bar passes it. It moves
    // rightward each frame (pro-rata target for the current week), positioned in layout(). Anyone
    // with a year target gets one. The trailing label carries the running £ plus a "Weeks @ #1" tally.
    const t = targets[id];
    const hasT = !!(t && t.year > 0);
    row.innerHTML =
      `<div class="slt-av" style="background:${colour[id]}" title="${names[id] || ""}">${initials(names[id] || "")}</div>` +
      `<div class="slt-bar" style="background:${colour[id]}"></div>` +
      (hasT && t.ytd > 0 ? `<div class="slt-rline slt-rline-ytd" title="YTD target (as at this week)"></div>` : "") +
      `<span class="slt-barval">£0</span>` +
      `<span class="slt-barwks">${WKS_TEXT}0</span>`;
    track.appendChild(row);
    rows[id] = row;
    // styled hover tooltip (shared kpiTooltip.js) — live current-week figures for this person.
    // mousemove refreshes the text too, so it updates while the race plays under the cursor.
    row.addEventListener("mouseenter", e => showTip(e, raceTip(state, id)));
    row.addEventListener("mousemove", e => showTip(e, raceTip(state, id)));
    row.addEventListener("mouseleave", hideTip);
  });
  host.appendChild(track);

  // Graph mode (line chart) shares all the same data + controls — it's a second renderer over the
  // same state, hidden by default. £ on Y (fixed to the year-end max), weeks on X; one line per
  // person drawn progressively to the current week, with a labelled (initials) dot at each head.
  const graph = document.createElement("div");
  graph.className = "slt-graph";
  graph.style.display = "none";
  host.appendChild(graph);

  const state = {
    host, scrub, dateSpan, playBtn, people, names, cum, colour, rows, track, targets,
    maxWeek, weekEnd, weekLeader, maxWks, rowH, gap, grandMax: grandMax || 1,
    measureLabel: payload.measure || "",
    week: 0, playing: false, raf: 0, startRaf: 0, lastTs: 0, acc: 0,
    trackPx: 0,
    mode: "bar", graph, graphEls: null
  };
  races.set(host, state);

  // scrubber
  if (scrub){
    scrub.min = 0; scrub.max = maxWeek; scrub.step = 1; scrub.value = 0;
    scrub.oninput = () => { pause(state); seek(state, +scrub.value); };
  }

  // Bars are sized in PIXELS against the measured track (host width − avatar − the £ label's own
  // width − gaps), NOT as a % of the row. That reserves room for the label that now sits outside
  // the bar, so the leader's bar travels the full track right up to year-end instead of maxing out
  // early and playing out with no movement. Re-measure (and re-layout) whenever the host resizes.
  measure(state);
  state.ro = new ResizeObserver(() => { measure(state); layout(state, state.week, true); updateDate(state); });
  state.ro.observe(host);

  if (scrub) scrub.value = 0;                 // reset the control bar for a fresh race
  layout(state, 0, false);                    // initial frame (all at £0)
  updateDate(state);
  syncPlayBtn(state);                         // show ▶ — the race waits for the user to press play

  // The popup animates open, so the host can have 0 width on the first frame. Poll with rAF until
  // it has a real width, then draw the £0 start frame at the correct scale. We do NOT autoplay —
  // the user presses play to begin. Bail if the race was disposed while we waited.
  const drawWhenReady = () => {
    if (races.get(host) !== state) return;    // disposed / superseded → abort
    if (host.clientWidth > 0){ measure(state); layout(state, 0, true); updateDate(state); }
    else state.startRaf = requestAnimationFrame(drawWhenReady);
  };
  drawWhenReady();
  return true;
}

// compute the pixel width of the bar "track" = the host's inner width minus the fixed avatar,
// minus the widest £ label (the year-end leader — the label never shrinks the track mid-race), and
// minus the widest weeks-at-#1 tally, with the margins around both. Cached on state; recomputed on resize.
// Widths are LAYOUT widths (offsetWidth), never getBoundingClientRect: the popup opens with a scale
// animation, and a rect measured mid-animation came back too small, so the leader's labels ran off the end.
function measure(state){
  const host = state.host;
  const cs = getComputedStyle(host);
  const inner = host.clientWidth - parseFloat(cs.paddingLeft || 0) - parseFloat(cs.paddingRight || 0);
  // measure the widest £ label by temporarily writing the year-end leader's value into a real label
  const probe = state.track.querySelector(".slt-barval");
  let labelW = 90;                                   // sensible fallback
  if (probe){
    const prev = probe.textContent;
    probe.textContent = gbp.format(Math.round(state.grandMax));
    labelW = Math.ceil(probe.offsetWidth);
    probe.textContent = prev;
  }
  // the tally trails the £ value — reserve the widest it will reach (the year's most weeks at #1)
  const wksProbe = state.track.querySelector(".slt-barwks");
  let wksW = 110;
  if (wksProbe){
    const prev = wksProbe.textContent;
    wksProbe.textContent = WKS_TEXT + state.maxWks;
    wksW = Math.ceil(wksProbe.offsetWidth) + (parseFloat(getComputedStyle(wksProbe).marginLeft) || 0);
    wksProbe.textContent = prev;
  }
  // AV_W / LABEL_GAP are module-level so layout() offsets the target lines by the same amount.
  // The trailing 12px is an end pad so the last label never touches the card edge.
  state.trackPx = Math.max(20, inner - AV_W - LABEL_GAP - labelW - wksW - 12);
}

// place every row at its rank for the (fractional) week. `snap` = jump instantly (initial
// frame / scrub); otherwise POSITION always glides (so overtakes animate) — the width is driven
// every frame so it needs no transition during playback.
//
// The bar width uses a FIXED denominator (the year-end leader total). That is deliberate: a
// person's value only ever climbs week-to-week, so against a constant scale every bar grows
// monotonically and can never appear to "go backwards" — which it would if we rescaled to the
// current leader each frame (the denominator would jump on an overtake and shrink everyone).
function layout(state, week, snap){
  const w = Math.max(0, Math.min(week, state.maxWeek));
  const wa = Math.floor(w), wb = Math.min(wa + 1, state.maxWeek), f = w - wa;

  // interpolated value per person between week wa and wb
  const val = {};
  state.people.forEach(id => {
    const a = state.cum[id][wa] || 0, b = state.cum[id][wb] || 0;
    val[id] = a + (b - a) * f;
  });
  state.val = val;   // expose the current-frame values so the hover tooltip reads live figures (both modes)

  if (state.mode === "graph"){ layoutGraph(state, w, snap); return; }
  // rank order (desc by value)
  const order = [...state.people].sort((x, y) => val[y] - val[x]);
  const maxVal = Math.max(state.grandMax, 1);   // constant across the whole race → monotonic bars

  const trackPx = state.trackPx || 1;

  // weeks-at-#1 up to the current (rounded) week — drives the "Weeks @ #1: n" label on each bar
  const upto = Math.round(w);
  state.wksTop = {};
  state.people.forEach(id => state.wksTop[id] = 0);
  for (let wk = 1; wk <= upto && wk <= state.maxWeek; wk++){
    const ldr = state.weekLeader[wk]; if (ldr) state.wksTop[ldr]++;
  }

  order.forEach((id, rank) => {
    const row = state.rows[id];
    // position always transitions (glides on overtake) unless we're snapping
    row.style.transition = snap ? "none" : "transform 0.6s cubic-bezier(.22,.61,.36,1)";
    row.style.transform = `translateY(${rank * (state.rowH + state.gap)}px)`;
    const bar = row.querySelector(".slt-bar");
    // pixel width against the measured track → the leader fills the track exactly at year-end
    const px = Math.max(6, (val[id] / maxVal) * trackPx);
    // width is set every frame, so no transition during playback (that would double-animate and
    // lag the ticker); only ease it when snapping from a scrub so the jump isn't jarring.
    bar.style.transition = "none";
    bar.style.width = px + "px";
    row.querySelector(".slt-barval").textContent = gbp.format(Math.round(val[id]));

    // "Weeks @ #1: n" trailing the value — weeks this person has led up to the current (rounded) week
    const wksEl = row.querySelector(".slt-barwks");
    if (wksEl) wksEl.textContent = WKS_TEXT + (state.wksTop ? state.wksTop[id] || 0 : 0);

    // YTD-target line: moves from £0 to the person's ACTUAL YTD target (t.ytd, the proc's
    // working-day-prorated figure for "today") across the available weeks. Using t.ytd (not the full
    // YEAR target) is the fix — the earlier year×week/maxWeek reached the FULL target mid-year and
    // shot the line off-screen. It lands exactly on the real YTD target at the last data week, and
    // grows linearly before that. Same scale + AV_W offset as the bar, so it lines up with the tip.
    const t = state.targets[id];
    const dl = row.querySelector(".slt-rline-ytd");
    if (t && t.ytd > 0 && dl){
      const proRata = t.ytd * (state.maxWeek > 0 ? w / state.maxWeek : 0);
      dl.style.left = (BAR_X + (proRata / maxVal) * trackPx) + "px";
    }
  });
}

// ---- Graph (line-chart) mode ------------------------------------------------------------------
const SVGNS = "http://www.w3.org/2000/svg";
const GRAPH_PAD = { l: 64, r: 20, t: 16, b: 30 };

function svgEl(name, attrs){ const e = document.createElementNS(SVGNS, name); for (const k in attrs) e.setAttribute(k, attrs[k]); return e; }

// round an axis LABEL to ~2 significant figures — clean to read and stable frame-to-frame (the
// smooth ceiling changes continuously, but the displayed label only ticks at the 2-sig-fig step).
function roundLabel(v){
  if (v <= 0) return 0;
  const pow = Math.pow(10, Math.floor(Math.log10(v)) - 1);
  return Math.round(v / pow) * pow;
}

// Build the graph frame: axes shell, 6 reusable Y gridline+label pairs (repositioned/relabelled per
// frame because the scale grows), the month X labels (static), and a polyline + labelled head dot per
// person. Stored on state.graphEls; layoutGraph does the per-frame work.
function buildGraph(state){
  const g = state.graph;
  const W = Math.max(g.clientWidth, 320), H = Math.max(g.clientHeight, 240);
  const plotW = W - GRAPH_PAD.l - GRAPH_PAD.r, plotH = H - GRAPH_PAD.t - GRAPH_PAD.b;

  const svg = svgEl("svg", { viewBox: `0 0 ${W} ${H}`, class: "slt-graph-svg", width: "100%", height: "100%" });

  // 6 Y gridline + label pairs, repositioned + relabelled each frame as the scale ratchets up.
  const yGrid = [], yLabel = [];
  for (let i = 0; i <= 5; i++){
    const ln = svgEl("line", { x1: GRAPH_PAD.l, x2: W - GRAPH_PAD.r, class: "slt-graph-grid" });
    const tx = svgEl("text", { x: GRAPH_PAD.l - 8, "text-anchor": "end", class: "slt-graph-axis" });
    svg.appendChild(ln); svg.appendChild(tx);
    yGrid.push(ln); yLabel.push(tx);
  }

  // X axis: month labels along the weeks (static — the time axis doesn't rescale).
  const xAt = wk => GRAPH_PAD.l + (state.maxWeek > 0 ? wk / state.maxWeek : 0) * plotW;
  const seenMonth = {};
  for (let wk = 1; wk <= state.maxWeek; wk++){
    const d = state.weekEnd[wk]; if (!d) continue;
    const dt = new Date(d);
    if (isNaN(dt)) continue;
    const key = dt.getFullYear() + "-" + dt.getMonth();
    if (seenMonth[key]) continue;
    seenMonth[key] = true;
    const t = svgEl("text", { x: xAt(wk), y: H - 10, "text-anchor": "middle", class: "slt-graph-axis" });
    t.textContent = dt.toLocaleString("en-GB", { month: "short" });
    svg.appendChild(t);
  }

  // one polyline + head dot (with initials) per person
  const lines = {}, dots = {}, dotLabels = {};
  state.people.forEach(id => {
    const pl = svgEl("polyline", { fill: "none", stroke: state.colour[id], "stroke-width": 2.5, "stroke-linejoin": "round", "stroke-linecap": "round", points: "" });
    svg.appendChild(pl); lines[id] = pl;
  });
  state.people.forEach(id => {
    const dot = svgEl("circle", { r: 12, fill: state.colour[id], stroke: "#fff", "stroke-width": 1.5 });
    const lbl = svgEl("text", { class: "slt-graph-dotlbl", "text-anchor": "middle", "dominant-baseline": "central" });
    lbl.textContent = initials(state.names[id] || "");
    svg.appendChild(dot); svg.appendChild(lbl);
    dots[id] = dot; dotLabels[id] = lbl;
  });

  g.innerHTML = "";
  g.appendChild(svg);
  state.graphEls = { svg, yGrid, yLabel, lines, dots, dotLabels, plotW, plotH, xAt, W, H, yMax: 1 };
}

// Position the lines (progressive to the current fractional week) + head dots for the frame.
// The Y scale GROWS with the playhead so early weeks aren't crammed at the bottom: ceiling = the
// current highest cumulative × 1.15 headroom, nice-rounded, and RATCHETED (never shrinks) so an
// overtake never makes the chart jump downward. Capped at the year-end max.
function layoutGraph(state, w, snap){
  if (!state.graphEls || state.graphEls.W !== Math.max(state.graph.clientWidth, 320)) buildGraph(state);
  const els = state.graphEls;
  const wa = Math.floor(w), f = w - wa;

  // current highest cumulative value across everyone at this fractional week
  let curMax = 0;
  state.people.forEach(id => {
    const cum = state.cum[id];
    const v = (f > 0 && wa < state.maxWeek) ? (cum[wa] || 0) + ((cum[wa + 1] || 0) - (cum[wa] || 0)) * f : (cum[wa] || 0);
    if (v > curMax) curMax = v;
  });
  // Target ceiling: current highest × headroom, capped at the year-end leader. NOT nice-rounded —
  // rounding created discrete threshold jumps that made the early weeks snap. The scale is a smooth
  // continuous value; only the axis LABELS are rounded for display (below).
  const cap = state.grandMax * 1.15;
  const target = Math.max(1, Math.min(curMax * 1.15, cap));
  // ratchet the target so it only grows, then EASE the actual ceiling toward it each frame so the
  // rescale glides instead of snapping. On a fresh start (w≈0) snap straight to the target.
  els.yTarget = (w <= 0.01) ? target : Math.max(els.yTarget || target, target);
  // Ease toward the target during playback so the rescale glides; snap straight to it on a scrub /
  // seek / initial frame (there are no follow-up frames to finish the ease).
  const yMax = (w <= 0.01 || snap) ? els.yTarget : (els.yMax + (els.yTarget - els.yMax) * 0.12);
  els.yMax = yMax;

  const yAt = v => GRAPH_PAD.t + els.plotH * (1 - Math.max(0, Math.min(1, v / yMax)));

  // reposition + relabel the Y gridlines. Labels are rounded to a clean-ish figure for readability
  // (the geometry uses the smooth yMax, so no snap); a coarse round keeps the label from flickering
  // every frame.
  for (let i = 0; i <= 5; i++){
    const y = GRAPH_PAD.t + els.plotH * (1 - i / 5);
    els.yGrid[i].setAttribute("y1", y); els.yGrid[i].setAttribute("y2", y);
    els.yLabel[i].setAttribute("y", y + 4);
    els.yLabel[i].textContent = gbp.format(roundLabel(yMax * i / 5));
  }

  state.people.forEach(id => {
    const cum = state.cum[id];
    const pts = [];
    for (let wk = 0; wk <= wa; wk++) pts.push(`${els.xAt(wk).toFixed(1)},${yAt(cum[wk] || 0).toFixed(1)}`);
    let headX, headY;
    if (f > 0 && wa < state.maxWeek){
      const a = cum[wa] || 0, b = cum[wa + 1] || 0, v = a + (b - a) * f;
      headX = els.xAt(w); headY = yAt(v);
      pts.push(`${headX.toFixed(1)},${headY.toFixed(1)}`);
    } else {
      headX = els.xAt(wa); headY = yAt(cum[wa] || 0);
    }
    els.lines[id].setAttribute("points", pts.join(" "));
    els.dots[id].setAttribute("cx", headX.toFixed(1));
    els.dots[id].setAttribute("cy", headY.toFixed(1));
    els.dotLabels[id].setAttribute("x", headX.toFixed(1));
    els.dotLabels[id].setAttribute("y", headY.toFixed(1));
    const show = w > 0.01 ? "visible" : "hidden";
    els.dots[id].setAttribute("visibility", show);
    els.dotLabels[id].setAttribute("visibility", show);
  });
}

// Flip a race between "bar" and "graph". Re-lays out at the current week so switching mid-play
// (or while paused) is seamless. Called by the header radio in SalesPerformance.razor.
export function setRaceMode(hostId, mode){
  const host = $(hostId); if (!host) return;
  const state = races.get(host); if (!state) return;
  state.mode = mode === "graph" ? "graph" : "bar";
  const bar = state.mode === "bar";
  state.track.style.display = bar ? "" : "none";
  state.graph.style.display = bar ? "none" : "block";
  if (!bar) buildGraph(state);
  layout(state, state.week, true);
}

// hover tooltip text for a race bar, from the CURRENT frame: date, salesperson, measure value, the
// YTD target as-of this week (+ % of it achieved), and the overall year target (+ % achieved).
function raceTip(state, id){
  const wk = Math.round(state.week);
  const date = wk <= 0 ? "" : (state.weekEnd[wk] || state.weekEnd[Math.min(wk, state.maxWeek)] || `Week ${wk}`);
  const actual = state.val ? (state.val[id] || 0) : 0;
  const lines = [];
  if (date) lines.push(date);
  lines.push(state.names[id] || "");
  lines.push(`${state.measureLabel || ""}: ${gbp.format(Math.round(actual))}`);
  const t = state.targets[id];
  if (t && t.ytd > 0){
    // YTD target scaled to the current week (matches the moving line), and % of it achieved
    const ytdNow = t.ytd * (state.maxWeek > 0 ? Math.min(state.week, state.maxWeek) / state.maxWeek : 0);
    const pctYtd = ytdNow > 0 ? Math.round(actual / ytdNow * 100) : 0;
    lines.push(`YTD target: ${gbp.format(Math.round(ytdNow))} (${pctYtd}%)`);
  }
  if (t && t.year > 0){
    const pctYr = Math.round(actual / t.year * 100);
    lines.push(`Year target: ${gbp.format(Math.round(t.year))} (${pctYr}%)`);
  }
  return lines.join("\n");
}

function updateDate(state){
  if (!state.dateSpan) return;
  const wk = Math.round(state.week);
  // nothing at week 0 (no "Start of …"); once it moves, show that week's end date
  state.dateSpan.textContent = wk <= 0 ? ""
    : (state.weekEnd[wk] || state.weekEnd[Math.min(wk, state.maxWeek)] || `Week ${wk}`);
  positionDate(state);
}

// float the date label horizontally over the scrubber thumb, tracking it as the race plays. The
// thumb centre travels from THUMB/2 to (width - THUMB/2) across the track; the label is centred on
// it (CSS translateX(-50%)) but clamped so it never spills past the wrapper's edges.
const THUMB = 16;   // approx native range-thumb width (px)
function positionDate(state){
  const el = state.dateSpan, scrub = state.scrub;
  if (!el || !scrub) return;
  const w = scrub.clientWidth || 0;
  if (w <= 0) return;
  const frac = state.maxWeek > 0 ? Math.max(0, Math.min(1, state.week / state.maxWeek)) : 0;
  const centre = THUMB / 2 + frac * (w - THUMB);
  const halfLabel = el.offsetWidth / 2;
  const left = Math.max(halfLabel, Math.min(w - halfLabel, centre));   // keep the label on-track
  el.style.left = left + "px";
}

function seek(state, week){
  state.week = Math.max(0, Math.min(week, state.maxWeek));
  if (state.scrub) state.scrub.value = Math.round(state.week);
  layout(state, state.week, true);
  updateDate(state);
}

function frame(state, ts){
  if (!state.playing) return;
  if (!state.lastTs) state.lastTs = ts;
  const dt = ts - state.lastTs; state.lastTs = ts;
  state.week += dt / MS_PER_WEEK;
  let ended = false;
  if (state.week >= state.maxWeek){ state.week = state.maxWeek; state.playing = false; ended = true; }
  if (state.scrub) state.scrub.value = Math.round(state.week);
  layout(state, state.week, false);         // per-frame: no CSS transition, we drive it
  updateDate(state);
  if (state.playing) state.raf = requestAnimationFrame(t => frame(state, t));
  else if (ended) syncPlayBtn(state);       // reached year-end → show ▶ (press to replay)
}

// reflect play/pause on the button icon (▶ when paused/ended, ⏸ while playing)
function syncPlayBtn(state){
  const b = state.playBtn; if (!b) return;
  const i = b.querySelector("i");
  if (i) i.className = state.playing ? "bi bi-pause-fill" : "bi bi-play-fill";
}

function play(state){
  if (state.playing) return;
  if (state.week >= state.maxWeek) state.week = 0;   // replay from the start
  state.playing = true; state.lastTs = 0;
  syncPlayBtn(state);
  state.raf = requestAnimationFrame(t => frame(state, t));
}
function pause(state){
  state.playing = false; if (state.raf) cancelAnimationFrame(state.raf); state.raf = 0;
  syncPlayBtn(state);
}

export function togglePlay(hostId){
  const host = $(hostId); if (!host) return;
  const s = races.get(host); if (!s) return;
  if (s.playing) pause(s); else play(s);
}

export function disposeRace(hostId){
  const host = $(hostId);
  if (!host) return;
  const s = races.get(host);
  if (s){
    pause(s);
    if (s.startRaf) cancelAnimationFrame(s.startRaf);
    if (s.ro){ try { s.ro.disconnect(); } catch {} }
    races.delete(host);
  }
  host.innerHTML = "";
}
export function disposeAll(){ /* WeakMap auto-clears; nothing global to free */ }
