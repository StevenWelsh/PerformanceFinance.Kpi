// Postcode Heat Map — Leaflet choropleth, ported from the standalone PostcodeHeatMap app.
// Blazor owns the data (WebKPI.spHeatMap via HeatMapService) and the Year/Arrangement/metric
// filters; this module draws and drives the map. Region + town come from the proc rows, so
// there is no regions.js/data.js here — the page hands us an array of rows:
//   { region, district, postCodeArea, remainingCapital, activeLeads, activeCustomers, af, tfi, activatedLeads }
// Requires window.L (Leaflet), window.AREAS_GEOJSON (geo.js), window.IRELAND_GEOJSON (ireland.js)
// loaded on the page before init().

const COLORS = ['#fbe0e8','#f4a9c0','#ec7199','#e13b73','#d20d43','#a10a34','#6f0724'];
const NO_FILL = '#eef0f2';   // no-value areas + Ireland (exact original) — reads as pale grey on the #eef4fa sea

// metric config: which row field + how to format. Sales metrics (af/tfi) added to the
// original three so the same map/legend/table serve both lanes.
const gbp = new Intl.NumberFormat('en-GB', { style:'currency', currency:'GBP', maximumFractionDigits:0 });
const num = new Intl.NumberFormat('en-GB');
function shortGBP(v){ if(v>=1e6){const n=v/1e6; return '£'+(n%1?n.toFixed(1):n)+'m';}
  if(v>=1e3){const n=v/1e3; return '£'+(n%1?n.toFixed(0):n)+'k';} return '£'+Math.round(v); }
const METRICS = {
  capital:   { field:'capital',   label:'Remaining Capital', fmt:v=>gbp.format(v), short:shortGBP },
  leads:     { field:'leads',     label:'Active Leads',      fmt:v=>num.format(v), short:v=>num.format(v) },
  customers: { field:'customers', label:'Active Customers',  fmt:v=>num.format(v), short:v=>num.format(v) },
  af:        { field:'af',        label:'Amount Financed',   fmt:v=>gbp.format(v), short:shortGBP },
  tfi:       { field:'tfi',       label:'Total Fee Income',  fmt:v=>gbp.format(v), short:shortGBP },
};

// one live map instance per module load (the page has a single map)
let map, layer, irl, DATA, ROWS, dotNet;
let metric = 'capital', level = 'region';
let GROUPS = {}, AREA2KEY = {};
let selectedKey = null, selOutline = null, hovOutline = null, HOMEZOOM = 0, fitB, NUDGE = 170;

const LEVELS = {
  region: { title:'Regions', nameHdr:'Region', keyOf:a=>DATA[a]?.region || 'Unmapped' },
  area:   { title:'Postcode Areas', nameHdr:'Area', keyOf:a=>a },
};

function rebuildGroups(){
  GROUPS = {}; AREA2KEY = {};
  Object.keys(DATA).forEach(a=>{
    const k = LEVELS[level].keyOf(a); AREA2KEY[a] = k;
    const g = GROUPS[k] || (GROUPS[k] = { key:k, members:[], capital:0, leads:0, customers:0, af:0, tfi:0 });
    const d = DATA[a];
    g.members.push(a);
    g.capital += d.capital; g.leads += d.leads; g.customers += d.customers; g.af += d.af; g.tfi += d.tfi;
  });
}
const mfield = () => METRICS[metric].field;
function groupValue(a){ const g = GROUPS[AREA2KEY[a]]; return g ? (g[mfield()]||0) : 0; }

// Fixed, hand-tuned breakpoints per metric per level — NON-LINEAR (geometric, weighted at the
// small end) exactly as the original. A linear scale dumps ~117 of 124 postcode areas into the
// bottom band and washes the map out; these geometric steps spread the areas across all 7 colours.
// 6 breaks + 7 COLORS = 7 bands (region totals are far bigger than single areas, hence per-level).
// Fixed, hand-tuned NON-LINEAR (geometric) breaks per metric per level — the original's values,
// which spread the skewed data across all 7 colours (a linear scale dumps the long tail of small
// areas into one band). 6 breaks + 7 COLORS = 7 bands; region totals dwarf single areas, hence
// per-level. These are FIXED (not data-driven): a computed scale kept overshooting the top and
// leaving empty upper bands — the fixed values are known-good and match the original report.
const BREAKS = {
  capital:   { area:[200000,500000,1000000,2000000,4000000,8000000],  region:[2000000,5000000,10000000,15000000,20000000,25000000] },
  leads:     { area:[10,25,50,100,150,200],                           region:[50,150,300,450,600,800] },
  customers: { area:[5,15,30,50,75,100],                              region:[25,75,150,250,350,450] },
  af:        { area:[100000,250000,500000,1000000,2000000,4000000],   region:[2000000,5000000,10000000,15000000,20000000,25000000] },
  tfi:       { area:[5000,15000,30000,60000,120000,250000],           region:[100000,250000,500000,1000000,1500000,2000000] },
};
let _breaks = BREAKS.capital.region;
function computeBreaks(){
  _breaks = (BREAKS[metric] && BREAKS[metric][level]) || BREAKS.capital.region;
}
function brk(){ return _breaks; }
function bandIndex(v){ const b = brk(); for(let i=0;i<b.length;i++) if(v<b[i]) return i; return b.length; }
function bandOf(a){ const v = groupValue(a); return v>0 ? bandIndex(v) : 'none'; }
function colorFor(a){ const v = groupValue(a); return v>0 ? COLORS[bandIndex(v)] : NO_FILL; }

function baseStyle(f){ const c = colorFor(f.properties.name);
  return { fillColor:c, weight:1, color:(level==='area'?'#fff':c), fillOpacity:0.9 }; }
function applyBase(l){ l.setStyle(baseStyle(l.feature)); }
function restyleAll(){ layer.eachLayer(applyBase); }

// group outer boundary (shared edges cancel, leaving the perimeter)
function outlineSegs(key){
  const edges = new Map();
  const addSeg = (p,q)=>{ const kp=p[0].toFixed(3)+','+p[1].toFixed(3), kq=q[0].toFixed(3)+','+q[1].toFixed(3);
    if(kp===kq) return; const k = kp<kq?kp+'|'+kq:kq+'|'+kp; const e=edges.get(k); if(e) e.n++; else edges.set(k,{n:1,p,q}); };
  const ring = r=>{ for(let i=0;i<r.length-1;i++) addSeg(r[i],r[i+1]); };
  const geom = g=>{ if(g.type==='Polygon') g.coordinates.forEach(ring);
    else if(g.type==='MultiPolygon') g.coordinates.forEach(poly=>poly.forEach(ring)); };
  layer.eachLayer(l=>{ if(AREA2KEY[l.feature.properties.name]===key) geom(l.feature.geometry); });
  const segs = []; edges.forEach(e=>{ if(e.n===1) segs.push([[e.p[1],e.p[0]],[e.q[1],e.q[0]]]); });
  return segs;
}
function clearSelOutline(){ if(selOutline){ map.removeLayer(selOutline); selOutline=null; } }
function drawSelOutline(key){ clearSelOutline(); const s=outlineSegs(key);
  if(s.length){ selOutline=L.polyline(s,{color:'#111',weight:1.25,interactive:false}).addTo(map); selOutline.bringToFront(); } }
function clearHovOutline(){ if(hovOutline){ map.removeLayer(hovOutline); hovOutline=null; } }
function drawHovOutline(key){ clearHovOutline(); const s=outlineSegs(key);
  if(s.length){ hovOutline=L.polyline(s,{color:'#111',weight:2,opacity:.75,dashArray:'5 3',interactive:false}).addTo(map); hovOutline.bringToFront(); } }

function groupBounds(key){ let b=L.latLngBounds([]);
  layer.eachLayer(l=>{ if(AREA2KEY[l.feature.properties.name]===key) b.extend(l.getBounds()); }); return b; }
function resetView(){ map.fitBounds(fitB,{animate:false}); map.panBy([-NUDGE,0],{animate:false}); }

// selection -> tell Blazor which group + its rolled-up numbers (Blazor renders the info panel)
function selectGroup(key){
  selectedKey = key; drawSelOutline(key);
  const g = GROUPS[key];
  if(g && dotNet){
    dotNet.invokeMethodAsync('OnGroupSelected', {
      key, level,
      capital:g.capital, leads:g.leads, customers:g.customers, af:g.af, tfi:g.tfi,
      areas:g.members.length
    });
  }
}
function clearSelection(){ selectedKey=null; clearSelOutline(); if(dotNet) dotNet.invokeMethodAsync('OnGroupCleared'); }

let suppressMapClick = false;
function onAreaClick(e){ suppressMapClick=true; selectGroup(AREA2KEY[e.target.feature.properties.name]); }
function onAreaDbl(e){ const k=AREA2KEY[e.target.feature.properties.name];
  if(map.getZoom() > HOMEZOOM + 0.01) resetView(); else map.fitBounds(groupBounds(k).pad(0.3)); }

// ---- exported entry points (called from Blazor) ----

// build the map once. rows = array of HeatMapRow-shaped objects (camelCase from JSON); mk / lv = the measure
// and level the page has chosen (the module's own may be left from an earlier visit to the page).
export function init(container, rows, dotNetRef, zoomSlider, mk, lv){
  dotNet = dotNetRef;
  metric = METRICS[mk] ? mk : 'capital';
  level = LEVELS[lv] ? lv : 'region';
  ingest(rows);

  // Idempotent: if a map already exists (a background refresh can race the initial
  // build, so init may be called twice on the same container — Leaflet would throw
  // "Map container is already initialized"), tear the old one down first.
  if(map){ map.remove(); map=null; }

  // wheelPxPerZoomLevel 240 (default 60) quarters the scroll-wheel zoom sensitivity.
  map = L.map(container, { zoomControl:false, attributionControl:false, scrollWheelZoom:true, doubleClickZoom:false, zoomSnap:0.25, wheelPxPerZoomLevel:240 });
  irl = L.geoJSON(window.IRELAND_GEOJSON, { interactive:false, style:{ fillColor:NO_FILL, fillOpacity:1, color:'#e0e3e7', weight:1 } }).addTo(map);
  layer = L.geoJSON(window.AREAS_GEOJSON, { style:baseStyle, onEachFeature:(f,l)=>l.on({ click:onAreaClick, dblclick:onAreaDbl }) }).addTo(map);
  map.on('click', ()=>{ if(suppressMapClick){ suppressMapClick=false; return; } if(selectedKey) clearSelection(); });

  fitB = L.latLngBounds([]);
  layer.eachLayer(l=>{ const n=l.feature.properties.name; if(n!=='GY'&&n!=='JE') fitB.extend(l.getBounds()); });
  const FULL = layer.getBounds().extend(irl.getBounds());
  map.setMinZoom(map.getBoundsZoom(FULL));
  map.setMaxZoom(map.getBoundsZoom(FULL)+7);
  resetView();
  HOMEZOOM = map.getZoom();

  wireZoomSlider(zoomSlider);
  wireHelpHint(container);
  pushLegendAndTable();
}

// Show the "Click to select…" hint only while the mouse is moving over the map,
// fading it out ~1.5s after movement stops so it doesn't clutter the view.
function wireHelpHint(container){
  const hint = container.closest('.hm-map, .hm-body, .kpi-body')?.querySelector('.hm-helpkey')
            || document.querySelector('.hm-helpkey');
  if(!hint) return;
  let t = 0;
  container.addEventListener('mousemove', () => {
    hint.classList.add('show');
    clearTimeout(t);
    t = setTimeout(() => hint.classList.remove('show'), 1500);
  });
  container.addEventListener('mouseleave', () => { clearTimeout(t); hint.classList.remove('show'); });
}

// bind the vertical zoom slider directly to the map (two-way, smooth) — same as the original
function wireZoomSlider(el){
  if(!el) return;
  el.min = map.getMinZoom(); el.max = map.getMaxZoom(); el.step = 0.25; el.value = map.getZoom();
  el.oninput = () => map.setZoom(+el.value);
  map.on('zoom zoomend', () => { el.value = map.getZoom(); });
}

// turn the flat proc rows into the DATA lookup (+ town) the map groups on
function ingest(rows){
  ROWS = rows || [];
  DATA = {};
  ROWS.forEach(r=>{
    if(r.postCodeArea === 'U') return;   // overseas/unmapped: kept out of the map, shown in the table
    DATA[r.postCodeArea] = {
      town: r.district, region: r.region,
      capital:+r.remainingCapital||0, leads:+r.activeLeads||0, customers:+r.activeCustomers||0,
      af:+r.af||0, tfi:+r.tfi||0
    };
  });
  rebuildGroups();
  computeBreaks();     // data/level/arrangement/year changed -> recompute the colour bands
}

// Blazor calls this after a Year/Arrangement reload with fresh rows
export function update(rows){
  ingest(rows);
  clearSelection();
  restyleAll();
  pushLegendAndTable();
}

export function setMetric(mk){ metric = mk; computeBreaks(); restyleAll(); pushLegendAndTable(); }
// the Region / Postcode area switch (both views); BREAKS are per level, so the colour bands are worked out again
export function setLevel(lv){ level = lv; computeBreaks(); rebuildGroups(); clearSelection(); restyleAll(); pushLegendAndTable(); }

export function zoomIn(){ map.zoomIn(0.25); }
export function zoomOut(){ map.zoomOut(0.25); }
export function reset(){ resetView(); }

// table row hover/click from Blazor -> map outline/selection
export function hoverGroup(key){ if(key) drawHovOutline(key); else clearHovOutline(); }
export function pickGroup(key){ selectGroup(key); }

// legend band click -> flash every area in that band on the map (from the original)
let flashTimer = null;
export function flashBand(band){
  if(!layer) return;
  if(flashTimer){ clearInterval(flashTimer); layer.eachLayer(applyBase); flashTimer=null; }
  const targets = [];
  layer.eachLayer(l=>{ if(String(bandOf(l.feature.properties.name)) === String(band)) targets.push(l); });
  if(!targets.length) return;
  let c = 0;
  flashTimer = setInterval(()=>{ const on = c%2===0;
    targets.forEach(l=> on ? l.setStyle({fillColor:'#111',color:'#111',weight:2.5,fillOpacity:1}) : applyBase(l));
    if(++c>=6){ clearInterval(flashTimer); flashTimer=null; targets.forEach(applyBase); } }, 170);
}

// build the legend + table payloads and hand them to Blazor to render as HTML
function pushLegendAndTable(){
  if(!dotNet) return;
  const m = METRICS[metric], edges = [0, ...brk()];
  // legend bands + counts
  const counts = { none:0 }; for(let i=0;i<COLORS.length;i++) counts[i]=0;
  Object.keys(DATA).forEach(a=>{ counts[bandOf(a)]++; });
  const bands = [];
  if(counts.none>0) bands.push({ key:'none', colour:NO_FILL, label:'None', count:counts.none });
  for(let i=0;i<COLORS.length;i++){ const from=edges[i], to=edges[i+1];
    bands.push({ key:String(i), colour:COLORS[i], label:(to!==undefined?`${m.short(from)} – ${m.short(to)}`:`${m.short(from)}+`), count:counts[i]||0 }); }

  // table rows (grouped), sorted by the active metric desc. At both levels the group key
  // is the name (region name, or the postcode-area code) — no town column.
  const rows = Object.values(GROUPS).map(g=>({
    key:g.key, name:g.key,
    capital:g.capital, leads:g.leads, customers:g.customers, af:g.af, tfi:g.tfi,
    v:g[mfield()], areas:g.members.length
  })).sort((x,y)=>y.v-x.v);
  const um = ROWS.find(r=>r.postCodeArea==='U');
  const umHasValue = um && (+um[metricField(metric)]||0) > 0;

  dotNet.invokeMethodAsync('OnMapPainted', {
    metric, metricLabel:m.label, level, levelTitle:LEVELS[level].title, nameHdr:LEVELS[level].nameHdr,
    isArea: level==='area', bands, rows,
    unmapped: umHasValue ? { capital:+um.remainingCapital||0, leads:+um.activeLeads||0, customers:+um.activeCustomers||0, af:+um.af||0, tfi:+um.tfi||0 } : null
  });
}
// map the JS metric key to the proc/row property for the unmapped bucket
function metricField(mk){ return { capital:'remainingCapital', leads:'activeLeads', customers:'activeCustomers', af:'af', tfi:'tfi' }[mk]; }

export function dispose(){
  if(map){ map.remove(); map=null; }
  layer=irl=DATA=ROWS=dotNet=null; GROUPS={}; AREA2KEY={};
}

// ---- the PHONE map (HeatMapMobile.razor holds its container) ----
// A second Leaflet map over the same data, groups, measure, level and colour bands as the map above, and
// the same selection (selectGroup / clearSelection, so the page hears about it as it does from the full
// view). Built only while the phone view is showing - the media query is kpi.css's - and only once
// its container has a size, because a Leaflet map built in a hidden container draws wrongly; re-sized
// whenever the container changes size (shown again, the phone turned). Tap an area to select its region
// or area, tap it again (or the sea) to clear. Pinch zooms; one finger scrolls the page until the map is
// zoomed in, and then pans the map. The selection outline is a crimson casing that pulses
// (kpi.css, kpim-hm-sel-casing) with a white line on it, drawn above its neighbours.
const PHONE_QUERY = '(max-width: 599px), (max-height: 499px) and (orientation: landscape) and (pointer: coarse)';
let pmap = null, pLayer = null, pHost = null, pHome = null, pSel = null, pMql = null, pRo = null;
let pFitting = false, pSuppressClick = false;

// build when the phone view shows; afterwards keep the map's size (and, at the whole-UK view, its fit) right
function phoneCheck(){
  if(!pHost || !pMql || !pMql.matches || !layer) return;
  if(pHost.clientWidth === 0 || pHost.clientHeight === 0) return;   // hidden: wait until it shows
  if(!pmap){ phoneBuild(); return; }
  const atHome = pmap.getZoom() <= pmap.getMinZoom() + 0.01;
  pmap.invalidateSize({ animate:false });
  if(atHome || pmap.getZoom() <= phoneLimits() + 0.01) phoneFitHome();
}

function phoneBuild(){
  pmap = L.map(pHost, { zoomControl:false, attributionControl:false, dragging:false, touchZoom:true,
    scrollWheelZoom:false, doubleClickZoom:false, boxZoom:false, keyboard:false, zoomSnap:0 });
  L.geoJSON(window.IRELAND_GEOJSON, { interactive:false, style:{ fillColor:NO_FILL, fillOpacity:1, color:'#e0e3e7', weight:1 } }).addTo(pmap);
  pLayer = L.geoJSON(window.AREAS_GEOJSON, { style:baseStyle, onEachFeature:(f,l)=>l.on('click', onPhoneAreaClick) }).addTo(pmap);
  pmap.on('click', onPhoneMapClick);
  pmap.on('zoomend', onPhoneZoomEnd);
  pHome = pLayer.getBounds();     // the UK, Channel Islands and Shetland included
  phoneFitHome();
  phoneDrawSel();
}

// the whole UK fills the container (a little sea round it); no zooming out past it. The limits are set
// directly, not through setMinZoom, which would start an animated re-zoom that swallows the fit that
// follows; they are cleared first because getBoundsZoom clamps its answer to them.
function phoneLimits(){
  pmap.options.minZoom = 0; pmap.options.maxZoom = 30;
  const z = pmap.getBoundsZoom(pHome, false, [12,12]);
  pmap.options.minZoom = z; pmap.options.maxZoom = z + 7;
  return z;
}
function phoneFitHome(){
  pFitting = true;
  try {
    phoneLimits();
    pmap.fitBounds(pHome, { padding:[6,6], animate:false });
    if(pmap.dragging.enabled()) pmap.dragging.disable();
  } finally { pFitting = false; }
}
// pinched back out to the whole UK: settle on the fitted view and hand one-finger moves back to the page
function onPhoneZoomEnd(){
  if(pFitting) return;
  if(pmap.getZoom() <= pmap.getMinZoom() + 0.01) phoneFitHome();
  else if(!pmap.dragging.enabled()) pmap.dragging.enable();
}

function phoneToggle(key){
  if(key === undefined || key === null || key === selectedKey){ if(selectedKey) clearSelection(); }
  else selectGroup(key);
  phoneDrawSel();
}
function onPhoneAreaClick(e){ pSuppressClick = true; phoneToggle(AREA2KEY[e.target.feature.properties.name]); }
function onPhoneMapClick(){
  if(pSuppressClick){ pSuppressClick = false; return; }
  if(selectedKey){ clearSelection(); phoneDrawSel(); }
}

function phoneDrawSel(){
  if(!pmap) return;
  if(pSel){ pSel.forEach(l=>pmap.removeLayer(l)); pSel = null; }
  if(!selectedKey || !layer) return;
  const s = outlineSegs(selectedKey);
  if(!s.length) return;
  pSel = [
    L.polyline(s, { className:'kpim-hm-sel-casing', weight:5, interactive:false }).addTo(pmap),
    L.polyline(s, { className:'kpim-hm-sel-line', weight:2, interactive:false }).addTo(pmap),
  ];
  pSel.forEach(l=>l.bringToFront());
}

// called once, after init(): watch for the phone view and build the phone map when it shows
export function phoneInit(container){
  phoneDispose();
  if(!container) return;
  pHost = container;
  pMql = window.matchMedia(PHONE_QUERY);
  if(pMql.addEventListener) pMql.addEventListener('change', phoneCheck); else pMql.addListener(phoneCheck);
  if(window.ResizeObserver){ pRo = new ResizeObserver(()=>phoneCheck()); pRo.observe(container); }
  phoneCheck();
}

// after init / update / setMetric / setLevel: recolour the phone map and redraw its selection. Returns
// each group's colour band (its swatch in the phone list) for the current measure and level.
export function phoneSync(){
  if(pmap && pLayer){ pLayer.eachLayer(applyBase); phoneDrawSel(); }
  const swatch = {};
  Object.values(GROUPS).forEach(g=>{ const v = g[mfield()] || 0; swatch[g.key] = v>0 ? COLORS[bandIndex(v)] : NO_FILL; });
  return swatch;
}

// a row in the phone list: select it, or clear it when it is already selected
export function phonePick(key){ phoneToggle(key); }

export function phoneDispose(){
  if(pMql){ if(pMql.removeEventListener) pMql.removeEventListener('change', phoneCheck); else pMql.removeListener(phoneCheck); pMql = null; }
  if(pRo){ pRo.disconnect(); pRo = null; }
  if(pmap){ pmap.remove(); pmap = null; }
  pLayer = pHome = pSel = pHost = null;
}
