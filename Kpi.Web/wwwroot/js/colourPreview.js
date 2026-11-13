// SETTINGS > COLOURS' PREVIEW (13 Nov 2026): a small drawing of the section being edited, in the colours chosen so far
// (as the mock-up docs/colours-mockup.html). `colours` is every colour of the office: "section.item" -> hex.

const NAMES = ["Legal", "Dental", "Optical", "Misc", "Veterinary", "Medical", "Wealth", "Aesthetics"];

function bars(heights, colour, curIdx, curColour) {
  return `<div class="cp-bars">${heights.map((h, i) =>
    `<div style="height:${h}%;background:${i === curIdx ? curColour : colour}"></div>`).join("")}</div>`;
}

function mix(hex, t) {   // t of the way to white
  const a = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
  return "#" + a.map(x => Math.round(x + (255 - x) * t).toString(16).padStart(2, "0")).join("");
}
function shade(hex, t) {
  const a = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
  return "#" + a.map(x => Math.round(x * (1 - t)).toString(16).padStart(2, "0")).join("");
}

export function render(host, office, section, colours) {
  if (!host) return;
  const v = k => colours[section + "." + k], T = k => colours["tables." + k], C = k => colours["charts." + k];
  const shire = office === "mobberley";
  let html = "";

  if (section === "palette") {
    html = `<p class="cp-sub">Every colour in the other sections is chosen from these, or their lighter tints.</p>`;
  } else if (section === "site" && shire) {
    html = `<div class="cp-side"><div class="cp-menu" style="background:#d20d43">
      <div class="cp-h">SALES</div><div>Year on Year</div>
      <div class="cp-h" style="background:${v("menu")};opacity:1">MOBBERLEY</div>
      <div style="background:${v("menu")};box-shadow:inset 3px 0 0 ${v("open")}"><b>Year on Year</b></div>
      <div style="background:${v("menu")}">Sales Performance</div></div>
      <div class="cp-page"><div class="cp-title" style="color:${v("menu")}">Mobberley: Year on Year</div></div></div>`;
  } else if (section === "site") {
    const star = `<span style="color:${v("star")}">&#9733;</span>`;
    html = `<div class="cp-side"><div class="cp-menu" style="background:${v("menu")}">
      <div class="cp-h">SALES</div><div>Sales Summary ${star}</div><div class="cp-on">Year on Year ${star}</div>
      <div>Sales Performance <span style="opacity:.6">&#9734;</span></div><div class="cp-h">ANALYSIS</div><div>Business Analysis ${star}</div></div>
      <div class="cp-page"><div class="cp-title">Year on Year</div></div></div>`;
  } else if (section === "tables") {
    const foot = T("foot") || T("title");
    html = `<div class="cp-grptitle" style="background:${T("title")}">Year on Year &mdash; Amount Financed</div>
      <div class="cp-tbox"><table class="cp-t" style="--rl:${T("line")}">
       <tr style="background:${T("head")};border-bottom:2px solid ${T("rule")}"><th>Month</th><th>2024</th><th>2025</th><th style="background:${T("nowhead")}">2026</th></tr>
       ${[["Jan", "5.1m", "5.6m", "6.0m"], ["Feb", "5.4m", "5.2m", "5.9m"], ["Mar", "6.2m", "6.8m", "7.1m"]].map(r =>
         `<tr><td>${r[0]}</td><td>${r[1]}</td><td>${r[2]}</td><td style="background:${T("now")}">${r[3]}</td></tr>`).join("")}
       <tr style="background:${foot};font-weight:700;border-top:2px solid ${T("rule")}"><td>Total</td><td>16.7m</td><td>17.6m</td><td style="background:${T("now")}">19.0m</td></tr>
      </table></div>
      ${T("pick") ? `<div class="cp-tbox cp-tbox-round"><table class="cp-t" style="--rl:${T("line")}">
       <tr style="background:${T("head")}"><th>Profession</th><th>Full 2025</th><th>YTD 2026</th></tr>
       <tr style="background:${T("pick")}"><td>Dental (pointed at)</td><td>&pound;20.4m</td><td>&pound;21.9m</td></tr>
       <tr><td>Optical</td><td>&pound;22.8m</td><td>&pound;17.3m</td></tr></table></div>` : ""}`;
  } else if (section === "charts") {
    const vals = [37, 33, 26, 2, 1, 0.6, 0.3, 0.1], R = 45, Cc = 2 * Math.PI * R; let off = 0, svg = "";
    vals.forEach((x, i) => { const len = x / 100 * Cc;
      svg += `<circle cx="60" cy="60" r="${R}" fill="none" stroke="${C("s" + (i + 1))}" stroke-width="18" stroke-dasharray="${len} ${Cc - len}" stroke-dashoffset="${-off}" transform="rotate(-90 60 60)"/>`; off += len; });
    html = `<div class="cp-pie"><svg viewBox="0 0 120 120" width="140" height="140">${svg}<text x="60" y="65" text-anchor="middle" font-size="14" font-weight="700" fill="#1a1a1a">&pound;66.7m</text></svg>
      <div class="cp-legend cp-legend-col">${NAMES.map((n, i) => `<span><i style="background:${C("s" + (i + 1))}"></i>${n}</span>`).join("")}</div></div>
      ${bars([80, 45, 60, 52, 74, 76, 70, 50], C("bar"), 7, C("part"))}
      <div class="cp-legend"><span><i style="background:${C("bar")}"></i>Whole year</span><span><i style="background:${C("part")}"></i>This year so far</span></div>`;
  } else if (section === "sp") {
    const race = shire ? "" : `<div class="cp-race-head"><span style="background:${v("play")}">&#9654;</span> Timelapse race</div>` +
      ["Andy", "Kate", "Ross", "Sam"].map((n, i) => `<div class="cp-race"><span>${n}</span><div style="width:${[86, 70, 58, 44][i]}%;background:${colours["charts.s" + (i + 1)]}"></div></div>`).join("");
    html = `<div class="cp-rel">${bars([88, 74, 66, 58, 51, 40, 33, 25], v("col"), 2, v("colon"))}
      ${v("ytd") ? `<div class="cp-line" style="bottom:55%;border-top:3px dotted ${v("ytd")}"></div><div class="cp-line" style="bottom:78%;border-top:3px solid ${v("year")}"></div>` : ""}</div>
      <div class="cp-legend"><span><i style="background:${v("col")}"></i>Salesperson</span><span><i style="background:${v("colon")}"></i>Tapped</span>
      ${v("ytd") ? `<span><i class="cp-l" style="background:${v("ytd")}"></i>Year to date target</span><span><i class="cp-l" style="background:${v("year")}"></i>Year target</span>` : ""}</div>${race}`;
  } else if (section === "spr") {
    html = ["Enquiries 100%", "Proposals 64%", "Approved 41%"].map((t, i) => `<div class="cp-funnel">${t}
      <div><div style="width:${[100, 64, 41][i]}%;background:${i === 2 ? v("appr") : colours["charts.bar"] || "#d20d43"}"></div></div></div>`).join("");
  } else if (section === "rp") {
    html = `<div class="cp-tbox cp-tbox-round"><table class="cp-t" style="--rl:#ececec">
      <tr style="background:#f5f5f5"><th>Record</th><th>Figure</th><th>Year</th></tr>
      <tr><td>Best month</td><td>&pound;9.4m</td><td>2023</td></tr>
      <tr style="background:${v("rec")}"><td>Best quarter <span style="color:${v("trophy")}">&#9733;</span></td><td>&pound;24.1m</td><td>2026</td></tr>
      <tr><td>Best year</td><td>&pound;81.2m</td><td>2024</td></tr></table></div>`;
  } else if (section === "sd") {
    html = bars([40, 55, 48, 62, 70, 74, 81, 60], v("bar"), 7, v("cur")) +
      `<div class="cp-legend"><span><i style="background:${v("bar")}"></i>Past years</span><span><i style="background:${v("cur")}"></i>This year</span></div>`;
  } else if (section === "hm") {
    const b = v("base"), steps = [mix(b, .86), mix(b, .64), mix(b, .42), mix(b, .2), b, shade(b, .24), shade(b, .48)];
    html = `<div class="cp-scale">${steps.map(c => `<div style="background:${c}"></div>`).join("")}</div>
      <div class="cp-scale-ends"><span>Least</span><span>Most</span></div>
      <svg viewBox="0 0 200 120" class="cp-map">${[[20, 20, 3], [60, 10, 5], [100, 25, 1], [140, 15, 6], [40, 60, 2], [90, 65, 4], [140, 60, 0], [60, 95, 5], [120, 95, 3]]
        .map(([x, y, i]) => `<rect x="${x}" y="${y}" width="38" height="24" rx="4" fill="${steps[i]}" stroke="#fff"/>`).join("")}</svg>`;
  } else if (section === "ma") {
    html = `<div class="cp-rel">${bars([62, 48, 70, 55, 80, 66], v("margin"), -1, "")}
      <svg viewBox="0 0 100 110" preserveAspectRatio="none" class="cp-over">
       <polygon fill="${v("band")}" opacity=".6" points="8,70 25,66 42,72 58,64 75,60 92,62 92,28 75,22 58,32 42,26 25,34 8,30"/>
       <polyline fill="none" stroke="${v("buy")}" stroke-width="2" vector-effect="non-scaling-stroke" points="8,70 25,66 42,72 58,64 75,60 92,62"/>
       <polyline fill="none" stroke="${v("sell")}" stroke-width="2" vector-effect="non-scaling-stroke" points="8,30 25,34 42,26 58,32 75,22 92,28"/></svg></div>
      <div class="cp-legend"><span><i style="background:${v("margin")}"></i>Margin</span><span><i class="cp-l" style="background:${v("buy")}"></i>Buy rate</span><span><i class="cp-l" style="background:${v("sell")}"></i>Sell rate</span></div>`;
  }
  host.innerHTML = html;
}

// Export: hands the browser a file to save
export function download(name, base64) {
  const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
  const url = URL.createObjectURL(new Blob([bytes], { type: "application/json" }));
  const a = document.createElement("a");
  a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
