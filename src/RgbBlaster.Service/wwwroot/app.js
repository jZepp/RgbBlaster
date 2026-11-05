'use strict';

// ---------- API ----------
const api = {
  async get(url) {
    const r = await fetch(url, { cache: 'no-store' });
    if (!r.ok) throw new Error(`${url}: ${r.status}`);
    const t = r.headers.get('content-type') || '';
    return t.includes('json') ? r.json() : r;
  },
  async send(method, url, body, isForm) {
    const opts = { method, headers: { 'X-RgbBlaster': '1' } };
    if (isForm) opts.body = body;
    else if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
    const r = await fetch(url, opts);
    if (!r.ok) {
      let msg = `${r.status}`;
      try { msg = (await r.json()).error || msg; } catch { /* no body */ }
      throw new Error(msg);
    }
    const t = r.headers.get('content-type') || '';
    return t.includes('json') ? r.json() : null;
  },
};

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const h = (tag, attrs = {}, ...kids) => {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (v !== undefined && v !== null && v !== false) el.setAttribute(k, v === true ? '' : v);
  }
  for (const k of kids.flat()) if (k !== null && k !== undefined) el.append(k.nodeType ? k : document.createTextNode(k));
  return el;
};
const SVG = 'http://www.w3.org/2000/svg';
const s = (tag, attrs = {}) => {
  const el = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  return el;
};
const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
const fmtT = v => (v == null ? '--' : `${Math.round(v)}°`);

let toastTimer;
function toast(msg, err) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'toast' + (err ? ' err' : '');
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), err ? 6000 : 2500);
}
async function act(promise, ok) {
  try { const r = await promise; if (ok) toast(ok); return r; }
  catch (e) { toast(e.message, true); throw e; }
}

// ---------- State ----------
const state = { status: null, settings: null, leds: [], sensors: [], effects: [], preview: null, pw: 96, ph: 60 };

async function loadSettings() {
  state.settings = await api.get('/api/settings');
  const sel = $('#profile-select');
  sel.replaceChildren(...state.settings.profiles.map(p => h('option', { value: p.name, selected: p.name === state.settings.activeProfile }, p.name)));
}
const activeProfile = () => state.settings.profiles.find(p => p.name === state.settings.activeProfile);
async function loadLeds() { state.leds = await api.get('/api/leds'); }

// ---------- Live preview ----------
// Case views draw the effect canvas (blurred) with every LED as a dot, colored from the same frame.
const caseViews = [];
function makeCaseView(container) {
  const canvas = h('canvas', { width: state.pw, height: state.ph });
  const svg = s('svg', { viewBox: '0 0 1000 625', preserveAspectRatio: 'none' });
  container.replaceChildren(canvas, svg);
  const view = { container, canvas, svg, dots: [] };
  caseViews.push(view);
  rebuildDots(view);
  return view;
}
function rebuildDots(view) {
  view.svg.replaceChildren(s('rect', { x: 8, y: 8, width: 984, height: 609, rx: 18, class: 'case-outline' }));
  view.dots = [];
  for (const z of state.leds) {
    if (!z.placed) continue;
    for (const [x, y] of z.points) {
      const c = s('circle', { cx: x * 1000, cy: y * 625, r: 7, class: 'led', fill: '#000' });
      view.svg.append(c);
      view.dots.push({ el: c, x, y });
    }
  }
}
function sample(buf, x, y) {
  const px = Math.min(state.pw - 1, Math.floor(x * state.pw)), py = Math.min(state.ph - 1, Math.floor(y * state.ph));
  const o = (py * state.pw + px) * 3;
  return `rgb(${buf[o]},${buf[o + 1]},${buf[o + 2]})`;
}
let previewBusy = false;
async function tickPreview() {
  const visible = caseViews.filter(v => v.container.offsetParent !== null);
  if ((!visible.length && !layout.visible()) || previewBusy) return;
  previewBusy = true;
  try {
    const r = await fetch(`/api/preview?w=${state.pw}&h=${state.ph}`, { cache: 'no-store' });
    const buf = new Uint8Array(await r.arrayBuffer());
    state.preview = buf;
    const img = new ImageData(state.pw, state.ph);
    for (let i = 0, j = 0; i < buf.length; i += 3, j += 4) { img.data[j] = buf[i]; img.data[j + 1] = buf[i + 1]; img.data[j + 2] = buf[i + 2]; img.data[j + 3] = 255; }
    for (const v of visible) {
      v.canvas.getContext('2d').putImageData(img, 0, 0);
      for (const d of v.dots) d.el.setAttribute('fill', sample(buf, d.x, d.y));
    }
    if (layout.visible()) layout.paintBackground(img, buf);
  } catch { /* service restarting */ }
  finally { previewBusy = false; }
}
// Fetches the next frame as soon as the last one is drawn, timed to repaint, at most about 30 per second.
const PREVIEW_MIN_MS = 33;
let lastPreview = 0;
(function previewLoop(t) {
  if (t - lastPreview >= PREVIEW_MIN_MS && !previewBusy) { lastPreview = t; tickPreview(); }
  requestAnimationFrame(previewLoop);
})(0);

// ---------- Status polling ----------
async function tickStatus() {
  try {
    state.status = await api.get('/api/status');
    $('#conn').textContent = 'Service connected';
    $('#conn').className = 'nav-foot ok';
    $('#sim-badge').hidden = !state.status.simulated;
    if (state.settings && state.status.activeProfile !== state.settings.activeProfile) { await loadSettings(); pages[current]?.enter?.(); }
    pages[current]?.status?.(state.status);
  } catch {
    $('#conn').textContent = 'Service not reachable';
    $('#conn').className = 'nav-foot bad';
  }
}
setInterval(tickStatus, 1000);

// ---------- Dashboard ----------
const dashboard = {
  view: null,
  enter() {
    if (!this.view) this.view = makeCaseView($('#dash-case'));
    const p = activeProfile();
    $('#dash-effect').textContent = p ? `${p.effect.type} · ${p.name}` : '';
    $('#dash-lcd-mode').textContent = p?.lcd.mode === 'media' ? 'Custom screen' : `${cap(p?.lcd.screen ?? 'status')} screen`;
    this.history();
  },
  status(st) {
    $('#st-cpu').textContent = fmtT(st.cpu);
    $('#st-gpu').textContent = fmtT(st.gpu);
    const pump = st.fans.find(f => f.isPump);
    $('#st-pump').textContent = pump?.rpm ?? '--';
    $('#st-render').textContent = st.renderMs.toFixed(2);
    $('#dash-lcd').src = `/api/lcd/frame?t=${Date.now()}`;
    const curveOf = id => st.curves.find(c => c.fans.some(f => f.toLowerCase() === id.toLowerCase()));
    $('#fan-table tbody').replaceChildren(...st.fans.map(f => {
      const c = curveOf(f.id);
      return h('tr', {},
        h('td', {}, h('div', {}, f.name), c?.duty != null ? h('div', { class: 'bar' }, h('i', { style: `width:${c.duty}%` })) : null),
        h('td', { class: 'muted' }, c ? c.name : (f.isPump ? 'pump' : 'default')),
        h('td', { class: 'r' }, c?.duty != null ? `${c.duty}%` : '-'),
        h('td', { class: 'r' }, f.rpm ?? '-'));
    }));
    $('#device-chips').replaceChildren(...st.devices.map(d => h('div', { class: 'chip' }, h('b', {}, d.name), h('span', {}, `${d.zones.reduce((a, z) => a + z.leds, 0)} LEDs`))));
    if (Date.now() - (this.lastHist || 0) > 2000) this.history();
  },
  async history() {
    this.lastHist = Date.now();
    const pts = await api.get('/api/history');
    drawTempChart($('#temp-chart'), pts);
    const msgs = await api.get('/api/messages');
    $('#messages').textContent = msgs.join('\n');
  },
};

function drawTempChart(svg, pts) {
  const W = 600, H = 200, lo = 20, hi = 100;
  svg.replaceChildren();
  for (let t = 30; t <= 90; t += 20) {
    const y = H - (t - lo) / (hi - lo) * H;
    svg.append(s('line', { x1: 0, x2: W, y1: y, y2: y, stroke: '#222839', 'stroke-width': 1 }));
    const label = s('text', { x: 4, y: y - 4, fill: '#5d6680', 'font-size': 11 });
    label.textContent = `${t}°`;
    svg.append(label);
  }
  if (pts.length < 2) return;
  const t0 = pts[pts.length - 1].t - 300;
  const line = key => pts.filter(p => p[key] != null).map(p => `${(p.t - t0) / 300 * W},${H - (p[key] - lo) / (hi - lo) * H}`).join(' ');
  svg.append(s('polyline', { points: line('cpu'), fill: 'none', stroke: 'var(--cpu)', 'stroke-width': 2.5, 'vector-effect': 'non-scaling-stroke' }));
  svg.append(s('polyline', { points: line('gpu'), fill: 'none', stroke: 'var(--gpu)', 'stroke-width': 2.5, 'stroke-dasharray': '7 5', 'vector-effect': 'non-scaling-stroke' }));
}

// ---------- Lighting ----------
const EFFECT_INFO = {
  solid: { label: 'Solid', params: ['color'] },
  breathe: { label: 'Breathe', params: ['palette', 'speed'] },
  rainbow: { label: 'Rainbow', params: ['speed', 'scale'] },
  wave: { label: 'Wave', params: ['palette', 'speed', 'angle', 'scale', 'spacing'] },
  radial: { label: 'Ripple', params: ['palette', 'speed', 'scale', 'spacing', 'center'] },
  cycle: { label: 'Color cycle', params: ['palette', 'speed'] },
  gradient: { label: 'Gradient', params: ['palette', 'angle'] },
  matrix: { label: 'Matrix', params: ['palette', 'speed', 'density'] },
  fire: { label: 'Fire', params: ['palette', 'speed', 'height'], palette: 'Fire' },
};
const MAX_COLORS = 16;
const cap = t => t.charAt(0).toUpperCase() + t.slice(1);
const PALETTES = {
  Rainbow: ['#ff0000', '#ffff00', '#00ff00', '#00ffff', '#0000ff', '#ff00ff'],
  Ocean: ['#00a0ff', '#00ffd0', '#0030ff', '#00e0ff'],
  Fire: ['#8a0000', '#ff2000', '#ff8000', '#ffd000', '#fff4c0'],
  Synthwave: ['#ff00a0', '#a000ff', '#00d0ff'],
  Matrix: ['#00ff40', '#00c030', '#80ff80'],
  Ice: ['#ffffff', '#80e0ff', '#2080ff'],
};
const lighting = {
  view: null,
  spec: null,
  push: debounce(spec => act(api.send('PUT', '/api/effect', spec)), 120),
  enter() {
    if (!this.view) this.view = makeCaseView($('#light-case'));
    this.spec = structuredClone(activeProfile().effect);
    if (!this.spec.colors?.length)
      this.spec.colors = this.spec.type === 'cycle' ? [...PALETTES.Rainbow] : [this.spec.color, this.spec.color2];
    this.kb = structuredClone(activeProfile().keyboard ?? { mode: null, color: '#ffffff', speed: 3, random: false });
    this.render();
    if (!this.kbModes) api.get('/api/keyboard/modes').then(m => { this.kbModes = m; this.renderKeyboard(); });
    else this.renderKeyboard();
  },
  pushKb: debounce(kb => act(api.send('PUT', '/api/keyboard', kb)), 300),
  renderKeyboard() {
    const kb = this.kb, modes = this.kbModes ?? [];
    const set = (k, v, redraw) => { kb[k] = v; activeProfile().keyboard = structuredClone(kb); this.pushKb(kb); if (redraw) this.renderKeyboard(); };
    const sel = h('select', { onchange: e => set('mode', e.target.value === '' ? null : +e.target.value, true) },
      h('option', { value: '', selected: kb.mode == null }, 'Follow the lighting effect'),
      ...modes.map(m => h('option', { value: m.mode, selected: kb.mode === m.mode }, m.name)));
    const kids = [h('label', { class: 'field' }, 'Mode', sel)];
    if (kb.mode != null) {
      const val = h('span', { class: 'val' }, `${kb.speed} (0 fastest, 5 slowest)`);
      kids.push(
        h('label', { class: 'field-row' }, 'Random colors', h('input', { type: 'checkbox', checked: kb.random, onchange: e => set('random', e.target.checked, true) })),
        kb.random ? null : h('label', { class: 'field-row' }, 'Color', h('input', { type: 'color', value: kb.color, oninput: e => set('color', e.target.value) })),
        h('div', { class: 'field' }, 'Direction', h('div', { class: 'seg' }, ...[[0, 'Left to right'], [1, 'Right to left']].map(([d, label]) =>
          h('button', { class: ((kb.direction ?? 0) === 0 ? 0 : 1) === d ? 'on' : '', onclick: () => set('direction', d, true) }, label)))),
        h('label', { class: 'field' }, 'Speed', val, h('input', { type: 'range', min: 0, max: 5, step: 1, value: kb.speed, oninput: e => { val.textContent = `${e.target.value} (0 fastest, 5 slowest)`; set('speed', +e.target.value); } })));
    }
    $('#kb-params').replaceChildren(...kids.filter(Boolean));
  },
  render() {
    const grid = $('#effect-grid');
    grid.replaceChildren(...state.effects.map(t => h('div', {
      class: `effect fx-${t}${t === this.spec.type ? ' on' : ''}`,
      onclick: () => {
        this.spec.type = t;
        // Fire looks wrong with arbitrary colors; start it on the fire palette.
        const preset = EFFECT_INFO[t]?.palette;
        if (preset) this.spec.colors = [...PALETTES[preset]];
        this.render(); this.push(this.spec);
      },
    }, h('span', {}, EFFECT_INFO[t]?.label ?? t))));
    const p = $('#effect-params');
    const want = new Set([...(EFFECT_INFO[this.spec.type]?.params ?? []), 'brightness']);
    const set = (k, v) => { this.spec[k] = v; this.push(this.spec); };
    const slider = (key, label, min, max, step, fmt) => {
      const val = h('span', { class: 'val' }, fmt(this.spec[key]));
      const input = h('input', { type: 'range', min, max, step, value: this.spec[key], oninput: e => { val.textContent = fmt(+e.target.value); set(key, +e.target.value); } });
      return h('label', { class: 'field' }, label, val, input);
    };
    const kids = [];
    if (want.has('color')) {
      kids.push(h('label', { class: 'field-row' }, 'Color', h('input', { type: 'color', value: this.spec.colors[0] ?? this.spec.color,
        oninput: e => { this.spec.colors[0] = e.target.value; set('color', e.target.value); } })));
    }
    if (want.has('palette')) kids.push(this.paletteEditor(set));
    if (want.has('speed')) kids.push(slider('speed', 'Speed', 0.02, 2, 0.02, v => `${(1 / v).toFixed(1)} s per cycle`));
    if (want.has('spacing')) kids.push(slider('spacing', 'Spacing (dark between bands)', 0, 0.95, 0.05, v => v > 0 ? `${Math.round(v * 100)}% dark` : 'none'));
    if (want.has('height')) kids.push(slider('height', 'Flame height', 0.1, 1, 0.05, v => `${Math.round(v * 100)}% of the case`));
    if (want.has('density')) kids.push(slider('density', 'Density', 0.05, 1, 0.05, v => `${Math.round(v * 100)}% of columns`));
    if (want.has('angle')) kids.push(slider('angle', 'Direction', 0, 359, 1, v => `${Math.round(v)}° ${arrow(v)}`));
    if (want.has('scale')) kids.push(slider('scale', 'Repeats across the case', 0.25, 6, 0.25, v => `× ${(+v).toFixed(2)}`));
    if (want.has('center')) {
      kids.push(slider('centerX', 'Centre, front to rear', 0, 1, 0.01, v => `${Math.round(v * 100)}%`));
      kids.push(slider('centerY', 'Centre, top to bottom', 0, 1, 0.01, v => `${Math.round(v * 100)}%`));
    }
    kids.push(slider('brightness', 'Brightness', 0, 1, 0.05, v => `${Math.round(v * 100)}%`));
    p.replaceChildren(...kids);
  },
  // Colors in order. Click a swatch to change it, arrows to move it, × to remove it; + adds a color.
  paletteEditor(set) {
    const colors = this.spec.colors;
    const commit = redraw => { set('colors', colors); if (redraw) this.render(); };
    const swatches = colors.map((c, i) => h('div', { class: 'swatch' },
      h('input', { type: 'color', value: c, title: `Color ${i + 1}`, oninput: e => { colors[i] = e.target.value; commit(false); } }),
      h('div', { class: 'swatch-tools' },
        h('button', { title: 'Move left', disabled: i === 0, onclick: () => { [colors[i - 1], colors[i]] = [colors[i], colors[i - 1]]; commit(true); } }, '‹'),
        h('button', { title: 'Remove', disabled: colors.length <= 1, onclick: () => { colors.splice(i, 1); commit(true); } }, '×'),
        h('button', { title: 'Move right', disabled: i === colors.length - 1, onclick: () => { [colors[i + 1], colors[i]] = [colors[i], colors[i + 1]]; commit(true); } }, '›'))));
    const add = h('button', { class: 'swatch add', title: 'Add a color', disabled: colors.length >= MAX_COLORS,
      onclick: () => { colors.push(colors[colors.length - 1] ?? '#ffffff'); commit(true); } }, '+');
    const presets = h('div', { class: 'presets' }, h('span', { class: 'muted' }, 'Presets'),
      ...Object.entries(PALETTES).map(([name, cs]) => h('button', { class: 'preset', title: name, style: `--g: linear-gradient(90deg, ${cs.join(', ')})`,
        onclick: () => { colors.splice(0, colors.length, ...cs); commit(true); } }, name)));
    const strip = h('div', { class: 'palette-strip', style: `background: linear-gradient(90deg, ${colors.length > 1 ? colors.join(', ') : `${colors[0]}, ${colors[0]}`})` });
    return h('div', { class: 'field' }, `Colors (${colors.length})`, strip, h('div', { class: 'palette' }, ...swatches, add), presets);
  },
};
const arrow = a => ['→', '↘', '↓', '↙', '←', '↖', '↑', '↗'][Math.round(((a % 360) + 360) % 360 / 45) % 8];

// ---------- Layout editor ----------
const VW = 1000, VH = 625;
const layout = {
  // sel is the zone shown in the editor; group holds every selected zone (keys). Dragging one moves the whole group.
  work: null, sel: null, group: new Set(), drag: null,
  visible: () => !$('#page-layout').hidden,
  enter() {
    this.work = structuredClone(state.settings.layout);
    this.points = state.leds;
    this.draw();
    this.list();
  },
  key: (d, z) => `${d}/${z}`.toLowerCase(),
  find(d, z) { return this.work.find(p => this.key(p.device, p.zone) === this.key(d, z)); },
  paintBackground(img) {
    const c = $('#layout-canvas');
    if (!$('#layout-bg').checked) { c.getContext('2d').clearRect(0, 0, c.width, c.height); return; }
    c.width = state.pw; c.height = state.ph;
    c.getContext('2d').putImageData(img, 0, 0);
    if (this.dotEls) for (const d of this.dotEls) d.el.setAttribute('fill', sample(state.preview, d.x, d.y));
  },
  refreshPoints: debounce(async () => {
    layout.points = await api.send('POST', '/api/leds/preview', layout.work);
    layout.drawDots();
  }, 150),
  draw() {
    const svg = $('#layout-svg');
    svg.replaceChildren(s('rect', { x: 8, y: 8, width: 984, height: 609, rx: 18, class: 'case-outline' }));
    const lab = (x, y, t, anchor) => { const e = s('text', { x, y, class: 'case-label', 'text-anchor': anchor }); e.textContent = t; return e; };
    svg.append(lab(24, 40, 'FRONT', 'start'), lab(976, 40, 'REAR', 'end'), lab(500, 40, 'TOP', 'middle'), lab(500, 605, 'BOTTOM', 'middle'));
    this.shapes = s('g'); this.dots = s('g');
    svg.append(this.shapes, this.dots);
    for (const p of this.work) this.shapes.append(this.shape(p));
    this.drawDots();
  },
  drawDots() {
    this.dots.replaceChildren();
    this.dotEls = [];
    // LED 1 of each zone is drawn larger with a ring, to match the editor to the hardware.
    for (const z of this.points) z.points.forEach(([x, y], i) => {
      const c = s('circle', { cx: x * VW, cy: y * VH, r: i === 0 ? 9 : 6, class: i === 0 ? 'led first' : 'led', fill: '#888' });
      this.dots.append(c);
      this.dotEls.push({ el: c, x, y });
    });
  },
  shape(p) {
    const g = s('g');
    const k = this.key(p.device, p.zone);
    const sel = this.group.has(k);
    const name = state.leds.find(z => this.key(z.device, z.zone) === this.key(p.device, p.zone))?.name ?? p.zone;
    const startDrag = (e, mode) => {
      e.stopPropagation();
      if (e.ctrlKey || e.shiftKey) { this.toggle(p.device, p.zone); return; }
      // The map is redrawn on every press, so a native dblclick never reaches the same element.
      const now = performance.now(), dbl = this.lastPress?.k === k && now - this.lastPress.t < 400;
      this.lastPress = { k, t: now };
      if (dbl && mode === 'move') { this.selectDevice(p.device); }
      else if (mode !== 'move' || !this.group.has(k)) this.select(p.device, p.zone);
      else { this.sel = k; this.editor(); var collapse = true; }
      const pt = this.toSvg(e);
      const members = mode === 'move' ? this.work.filter(q => this.group.has(this.key(q.device, q.zone))) : [p];
      this.drag = { p, mode, x0: pt.x, y0: pt.y, orig: structuredClone(p), members: members.map(q => ({ q, o: structuredClone(q) })), moved: false, collapse: !!collapse };
      $('#layout-svg').setPointerCapture(e.pointerId);
    };
    if (p.shape === 'Ring') {
      const fans = Math.max(1, p.fans || 1);
      for (let i = 0; i < fans; i++) {
        const cx = (p.x + i * (p.fanDx || 0)) * VW, cy = (p.y + i * (p.fanDy || 0)) * VH;
        const e = s('ellipse', { cx, cy, rx: p.radius * VW, ry: p.radius * VH, class: 'zone-shape' + (sel ? ' sel' : '') });
        e.addEventListener('pointerdown', ev => startDrag(ev, 'move'));
        g.append(e);
      }
      if (sel) {
        const hd = s('circle', { cx: (p.x + p.radius) * VW, cy: p.y * VH, r: 9, class: 'handle' });
        hd.addEventListener('pointerdown', ev => startDrag(ev, 'radius'));
        g.append(hd);
      }
      const t = s('text', { x: p.x * VW, y: (p.y + p.radius) * VH + 20, class: 'zone-label', 'text-anchor': 'middle' }); t.textContent = name; g.append(t);
    } else if (p.shape === 'Grid') {
      const x = Math.min(p.x, p.x2) * VW, y = Math.min(p.y, p.y2) * VH;
      const r = s('rect', { x, y, width: Math.abs(p.x2 - p.x) * VW, height: Math.abs(p.y2 - p.y) * VH, rx: 8, class: 'zone-shape' + (sel ? ' sel' : '') });
      r.addEventListener('pointerdown', ev => startDrag(ev, 'move'));
      g.append(r);
      if (sel) {
        const hd = s('circle', { cx: p.x2 * VW, cy: p.y2 * VH, r: 9, class: 'handle' });
        hd.addEventListener('pointerdown', ev => startDrag(ev, 'end'));
        g.append(hd);
      }
      const t = s('text', { x: x + 8, y: y - 8, class: 'zone-label' }); t.textContent = name; g.append(t);
    } else {
      const l = s('line', { x1: p.x * VW, y1: p.y * VH, x2: p.x2 * VW, y2: p.y2 * VH, class: 'zone-line' + (sel ? ' sel' : '') });
      l.addEventListener('pointerdown', ev => startDrag(ev, 'move'));
      g.append(l);
      if (sel) {
        const hd = s('circle', { cx: p.x2 * VW, cy: p.y2 * VH, r: 9, class: 'handle' });
        hd.addEventListener('pointerdown', ev => startDrag(ev, 'end'));
        g.append(hd);
      }
      const t = s('text', { x: (p.x + p.x2) / 2 * VW, y: (p.y + p.y2) / 2 * VH - 14, class: 'zone-label', 'text-anchor': 'middle' }); t.textContent = name; g.append(t);
    }
    return g;
  },
  toSvg(e) {
    const r = $('#layout-svg').getBoundingClientRect();
    return { x: (e.clientX - r.left) / r.width * VW, y: (e.clientY - r.top) / r.height * VH };
  },
  move(e) {
    if (!this.drag) return;
    const { p, mode, x0, y0, orig } = this.drag;
    const pt = this.toSvg(e);
    const dx = (pt.x - x0) / VW, dy = (pt.y - y0) / VH;
    if (!this.drag.moved && Math.hypot(pt.x - x0, pt.y - y0) < 3) return;
    this.drag.moved = true;
    this.lastPress = null; // a drag is not the first half of a double-click
    const cl = v => Math.min(1, Math.max(0, +v.toFixed(3)));
    if (mode === 'move') {
      // Clamp the shift so the whole group stays on the map and keeps its shape.
      let lo = -Infinity, hi = Infinity, loY = -Infinity, hiY = Infinity;
      for (const { q, o } of this.drag.members) {
        const xs = q.shape === 'Ring' ? [o.x] : [o.x, o.x2], ys = q.shape === 'Ring' ? [o.y] : [o.y, o.y2];
        lo = Math.max(lo, -Math.min(...xs)); hi = Math.min(hi, 1 - Math.max(...xs));
        loY = Math.max(loY, -Math.min(...ys)); hiY = Math.min(hiY, 1 - Math.max(...ys));
      }
      const mx = Math.min(hi, Math.max(lo, dx)), my = Math.min(hiY, Math.max(loY, dy));
      for (const { q, o } of this.drag.members) {
        q.x = cl(o.x + mx); q.y = cl(o.y + my);
        if (q.shape !== 'Ring') { q.x2 = cl(o.x2 + mx); q.y2 = cl(o.y2 + my); }
      }
    } else if (mode === 'radius') {
      p.radius = Math.max(0.01, +Math.abs(pt.x / VW - p.x).toFixed(3));
    } else {
      p.x2 = cl(pt.x / VW); p.y2 = cl(pt.y / VH);
    }
    this.draw();
    this.refreshPoints();
    this.editor();
  },
  refresh() { this.draw(); this.list(); this.editor(); },
  select(d, z) { this.sel = this.key(d, z); this.group = new Set([this.sel]); this.refresh(); },
  toggle(d, z) {
    const k = this.key(d, z);
    if (this.group.has(k)) { this.group.delete(k); if (this.sel === k) this.sel = [...this.group][0] ?? null; }
    else { this.group.add(k); this.sel = k; }
    this.refresh();
  },
  selectDevice(device) {
    const keys = state.leds.filter(z => z.device === device).map(z => this.key(z.device, z.zone));
    this.group = new Set(keys);
    this.sel = keys[0] ?? null;
    this.refresh();
  },
  list() {
    const devices = [...new Set(state.leds.map(z => z.device))];
    $('#zone-list').replaceChildren(...devices.flatMap(dev => {
      const zones = state.leds.filter(z => z.device === dev);
      const all = zones.every(z => this.group.has(this.key(z.device, z.zone)));
      const head = h('div', { class: 'zone-group' + (all && this.group.size > 1 ? ' sel' : ''), title: 'Select every zone of this device, to move them together', onclick: () => this.selectDevice(dev) },
        h('span', {}, dev), h('small', {}, `${zones.length} zone${zones.length === 1 ? '' : 's'} · select all`));
      return [head, ...zones.map(z => {
        const k = this.key(z.device, z.zone);
        const placed = this.find(z.device, z.zone);
        return h('div', { class: 'zone-item' + (this.group.has(k) ? ' sel' : ''), onclick: e => (e.ctrlKey || e.shiftKey) ? this.toggle(z.device, z.zone) : this.select(z.device, z.zone) },
          h('div', {}, h('div', {}, z.name), h('small', {}, `${z.zone} · ${z.points.length || '?'} LEDs`)),
          placed ? h('small', {}, placed.shape) : h('span', { class: 'unplaced' }, 'not placed'));
      })];
    }));
  },
  editor() {
    const box = $('#zone-edit');
    if (!this.sel) { box.replaceChildren(h('p', { class: 'muted' }, 'Select a zone.')); return; }
    if (this.group.size > 1) {
      const placed = this.work.filter(q => this.group.has(this.key(q.device, q.zone))).length;
      const members = this.work.filter(q => this.group.has(this.key(q.device, q.zone)));
      const turn = deg => { for (const q of members) q.rotation = (((q.rotation ?? 0) + deg + 540) % 360) - 180; this.draw(); this.refreshPoints(); };
      box.replaceChildren(
        h('p', { class: 'muted' }, `${this.group.size} zones selected (${placed} placed). Drag any of them to move the group. Click one zone to edit it alone.`),
        h('div', { class: 'field' }, 'Turn LEDs of every selected zone', h('div', { class: 'seg' },
          ...[[-15, '↺ 15°'], [-5, '↺ 5°'], [5, '↻ 5°'], [15, '↻ 15°']].map(([d, t]) => h('button', { onclick: () => turn(d) }, t)))));
      return;
    }
    const [d, z] = this.sel.split('/');
    const info = state.leds.find(x => this.key(x.device, x.zone) === this.sel);
    let p = this.find(d, z);
    if (!p) {
      box.replaceChildren(h('button', { class: 'btn primary', onclick: () => {
        this.work.push({ device: info.device, zone: info.zone, shape: 'Line', x: 0.4, y: 0.5, x2: 0.6, y2: 0.5, radius: 0.06, fans: 1, fanDx: 0, fanDy: 0 });
        this.select(d, z); this.refreshPoints();
      } }, 'Place this zone'));
      return;
    }
    const num = (key, label, step = 0.01) => h('label', { class: 'field' }, label,
      h('input', { type: 'number', step, value: p[key] ?? 0, onchange: e => { p[key] = +e.target.value; this.draw(); this.refreshPoints(); } }));
    const shapeSel = h('select', { onchange: e => { p.shape = e.target.value; if (p.shape !== 'Ring' && !p.x2) { p.x2 = Math.min(1, p.x + 0.1); p.y2 = p.y; } this.draw(); this.editor(); this.refreshPoints(); } },
      ...['Ring', 'Line', 'Grid'].map(v => h('option', { value: v, selected: p.shape === v }, v)));
    const rot = label => {
      const val = h('span', { class: 'val' }, `${Math.round(p.rotation ?? 0)}°`);
      return h('label', { class: 'field wide' }, label, val, h('input', { type: 'range', min: -180, max: 180, step: 1, value: p.rotation ?? 0,
        oninput: e => { p.rotation = +e.target.value; val.textContent = `${e.target.value}°`; this.draw(); this.refreshPoints(); } }));
    };
    const rev = label => h('label', { class: 'field-row' }, label, h('input', { type: 'checkbox', checked: !!p.reverse,
      onchange: e => { p.reverse = e.target.checked; this.refreshPoints(); } }));
    const fields = p.shape === 'Ring'
      ? [num('x', 'Centre X'), num('y', 'Centre Y'), num('radius', 'Radius'), num('fans', 'Fans in a row', 1), num('fanDx', 'Fan spacing X'), num('fanDy', 'Fan spacing Y'),
         rot('LED 1 position (0° = top)'), rev('Counter-clockwise')]
      : p.shape === 'Grid'
        ? [num('x', 'Start X'), num('y', 'Start Y'), num('x2', 'End X'), num('y2', 'End Y'), rot('Rotation')]
        : [num('x', 'Start X'), num('y', 'Start Y'), num('x2', 'End X'), num('y2', 'End Y'), rev('LED 1 at the end')];
    box.replaceChildren(
      h('div', { class: 'card-head' }, h('h2', {}, info?.name ?? z), h('button', { class: 'btn small danger', onclick: () => { this.work = this.work.filter(x => x !== p); this.refresh(); this.refreshPoints(); } }, 'Remove')),
      h('label', { class: 'field' }, 'Shape', shapeSel),
      h('div', { class: 'curve-meta' }, ...fields));
  },
};
$('#layout-svg').addEventListener('pointermove', e => layout.move(e));
$('#layout-svg').addEventListener('pointerup', () => {
  const d = layout.drag;
  layout.drag = null;
  // A click without a drag on a member of a group edits that zone alone.
  if (d && !d.moved && d.collapse) layout.select(d.p.device, d.p.zone);
});
$('#layout-revert').onclick = () => { layout.enter(); toast('Layout reverted.'); };
$('#layout-save').onclick = async () => {
  await act(api.send('PUT', '/api/layout', layout.work), 'Layout saved.');
  await loadSettings(); await loadLeds();
  for (const v of caseViews) rebuildDots(v);
  layout.enter();
};

// ---------- Fan curves ----------
const T0 = 20, T1 = 100;
const fans = {
  work: null,
  enter() { this.work = structuredClone(activeProfile().fanCurves); this.render(); },
  render() {
    $('#curves').replaceChildren(...this.work.map((c, i) => this.card(c, i)));
    if (!this.work.length) $('#curves').append(h('p', { class: 'muted' }, 'No curves. Fans not in a curve keep their default speed.'));
  },
  card(c, i) {
    const svg = s('svg', { class: 'curve-chart', viewBox: '0 0 640 360' });
    const card = h('div', { class: 'card' },
      h('div', { class: 'card-head' },
        h('input', { type: 'text', value: c.name, onchange: e => (c.name = e.target.value) }),
        h('button', { class: 'btn small danger', onclick: () => { this.work.splice(i, 1); this.render(); } }, 'Delete curve')),
      svg);
    this.chart(svg, c);
    const sensorIds = ['cpu', 'gpu', ...state.sensors.map(x => x.id)];
    const sensorName = id => id === 'cpu' ? 'CPU' : id === 'gpu' ? 'GPU' : (state.sensors.find(x => x.id === id)?.name ?? id);
    const checks = (list, all, label) => h('div', { class: 'checks' }, ...all.map(id => h('label', { class: 'check' },
      h('input', { type: 'checkbox', checked: list.some(x => x.toLowerCase() === id.toLowerCase()), onchange: e => {
        const idx = list.findIndex(x => x.toLowerCase() === id.toLowerCase());
        if (e.target.checked && idx < 0) list.push(id); else if (!e.target.checked && idx >= 0) list.splice(idx, 1);
      } }), label(id))));
    const num = (key, label, step) => h('label', { class: 'field' }, label, h('input', { type: 'number', step, value: c[key], onchange: e => (c[key] = +e.target.value) }));
    card.append(
      h('div', { class: 'field' }, 'Sensors (the hottest one drives the curve)', checks(c.sensors, [...new Set([...sensorIds.slice(0, 2), ...c.sensors, ...sensorIds.slice(2, 14)])], sensorName)),
      h('div', { class: 'field', style: 'margin-top:12px' }, 'Fans', checks(c.fans, [...new Set([...(state.status?.fans ?? []).map(f => f.id), ...c.fans])], id => state.status?.fans.find(f => f.id === id)?.name ?? id)),
      h('div', { class: 'curve-meta' }, num('averageSeconds', 'Average over (s)', 1), num('risePercentPerSecond', 'Rise %/s', 1), num('fallPercentPerSecond', 'Fall %/s', 0.5), num('failsafePercent', 'No-sensor duty %', 5)));
    return card;
  },
  chart(svg, c) {
    const L = 46, R = 620, T = 34, B = 330;
    const X = t => L + (t - T0) / (T1 - T0) * (R - L), Y = d => B - d / 100 * (B - T);
    const fromXY = (x, y) => ({ temp: Math.round(Math.min(T1, Math.max(T0, T0 + (x - L) / (R - L) * (T1 - T0)))), duty: Math.round(Math.min(100, Math.max(0, (B - y) / (B - T) * 100))) });
    const draw = () => {
      svg.replaceChildren();
      const defs = s('defs');
      defs.innerHTML = '<linearGradient id="curveStroke" x1="0" x2="1"><stop offset="0" stop-color="#22d3ee"/><stop offset="1" stop-color="#a855f7"/></linearGradient><linearGradient id="curveFill" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#22d3ee" stop-opacity=".25"/><stop offset="1" stop-color="#a855f7" stop-opacity="0"/></linearGradient>';
      svg.append(defs);
      const grid = s('g', { class: 'curve-grid' }), axis = s('g', { class: 'curve-axis' });
      for (let t = T0; t <= T1; t += 10) { grid.append(s('line', { x1: X(t), x2: X(t), y1: T, y2: B })); const e = s('text', { x: X(t), y: B + 18, 'text-anchor': 'middle' }); e.textContent = `${t}°`; axis.append(e); }
      for (let d = 0; d <= 100; d += 20) { grid.append(s('line', { x1: L, x2: R, y1: Y(d), y2: Y(d) })); const e = s('text', { x: L - 8, y: Y(d) + 4, 'text-anchor': 'end' }); e.textContent = `${d}%`; axis.append(e); }
      svg.append(grid, axis);
      const pts = [...c.points].sort((a, b) => a.temp - b.temp);
      if (pts.length) {
        const path = [[X(T0), Y(pts[0].duty)], ...pts.map(p => [X(p.temp), Y(p.duty)]), [X(T1), Y(pts[pts.length - 1].duty)]];
        svg.append(s('polygon', { class: 'curve-fill', points: [...path, [X(T1), B], [X(T0), B]].map(p => p.join(',')).join(' ') }));
        svg.append(s('polyline', { class: 'curve-line', points: path.map(p => p.join(',')).join(' ') }));
      }
      const live = state.status?.curves.find(x => x.name === c.name);
      if (live?.temp != null) {
        svg.append(s('line', { class: 'curve-now', x1: X(live.temp), x2: X(live.temp), y1: T, y2: B }));
        if (live.duty != null) {
          svg.append(s('circle', { class: 'curve-now-dot', cx: X(live.temp), cy: Y(live.duty), r: 5 }));
          const tip = s('text', { class: 'curve-tip', x: X(live.temp) + 8, y: T + 14 }); tip.textContent = `now ${Math.round(live.temp)}° → ${live.duty}%`; svg.append(tip);
        }
      }
      c.points.forEach(p => {
        const dot = s('circle', { class: 'curve-pt', cx: X(p.temp), cy: Y(p.duty), r: 8 });
        dot.addEventListener('pointerdown', e => { e.stopPropagation(); drag = p; svg.setPointerCapture(e.pointerId); });
        dot.addEventListener('dblclick', e => { e.stopPropagation(); if (c.points.length > 1) { c.points.splice(c.points.indexOf(p), 1); draw(); } });
        const lbl = s('text', { class: 'curve-tip', x: X(p.temp), y: Y(p.duty) - 14, 'text-anchor': 'middle' }); lbl.textContent = `${p.temp}° ${p.duty}%`;
        svg.append(dot, lbl);
      });
    };
    let drag = null;
    const pos = e => { const r = svg.getBoundingClientRect(); return { x: (e.clientX - r.left) / r.width * 640, y: (e.clientY - r.top) / r.height * 360 }; };
    svg.addEventListener('pointermove', e => { if (!drag) return; const p = pos(e); Object.assign(drag, fromXY(p.x, p.y)); draw(); });
    svg.addEventListener('pointerup', () => (drag = null));
    svg.addEventListener('dblclick', e => { const p = pos(e); c.points.push(fromXY(p.x, p.y)); draw(); });
    svg._redraw = draw;
    draw();
  },
  status() { for (const svg of $$('#curves svg.curve-chart')) svg._redraw?.(); },
};
$('#curve-add').onclick = () => {
  fans.work.push({ name: `curve ${fans.work.length + 1}`, sensors: ['cpu'], points: [{ temp: 40, duty: 30 }, { temp: 70, duty: 60 }, { temp: 85, duty: 100 }], fans: [], averageSeconds: 10, risePercentPerSecond: 10, fallPercentPerSecond: 2, failsafePercent: 100 });
  fans.render();
};
$('#curve-revert').onclick = () => { fans.enter(); toast('Curves reverted.'); };
$('#curve-save').onclick = async () => {
  for (const c of fans.work) c.points.sort((a, b) => a.temp - b.temp);
  const r = await act(api.send('PUT', '/api/fancurves', fans.work), 'Fan curves saved.');
  const w = $('#curve-warnings');
  w.hidden = !r?.warnings?.length;
  w.textContent = (r?.warnings ?? []).join('\n');
  await loadSettings();
};

// ---------- LCD ----------
const lcd = {
  enter() { this.cfg = structuredClone(activeProfile().lcd); this.controls(); this.gallery(); },
  status(st) {
    $('#lcd-live').src = `/api/lcd/frame?t=${Date.now()}`;
    $('#lcd-present').textContent = st.lcd.present ? (st.lcd.error ? `Error: ${st.lcd.error}` : 'Connected') : (st.simulated ? 'Preview only' : 'No LCD found');
  },
  push: debounce(function () { act(api.send('PUT', '/api/lcd', lcd.cfg)).then(loadSettings); }, 150),
  controls() {
    const b = $('#lcd-bright');
    b.value = this.cfg.brightness;
    $('#lcd-bright-val').textContent = `${this.cfg.brightness}%`;
    b.oninput = () => { this.cfg.brightness = +b.value; $('#lcd-bright-val').textContent = `${b.value}%`; this.push(); };
    for (const btn of $$('#lcd-rot button')) {
      btn.classList.toggle('on', +btn.dataset.r === this.cfg.rotation);
      btn.onclick = () => { this.cfg.rotation = +btn.dataset.r; this.controls(); this.push(); };
    }
  },
  async gallery() {
    const [media, screens] = await Promise.all([api.get('/api/lcd/media'), this.screens ??= api.get('/api/lcd/screens')]);
    const pick = (mode, id, screen) => { this.cfg.mode = mode; this.cfg.media = id; if (screen) this.cfg.screen = screen; this.gallery(); this.push(); };
    const builtIn = screens.map(sc => h('div', { class: 'tile' + (this.cfg.mode !== 'media' && (this.cfg.screen ?? 'status') === sc.id ? ' on' : ''), title: sc.name, onclick: () => pick('dashboard', null, sc.id) },
      h('img', { src: `/api/lcd/screens/${sc.id}/preview?t=${Date.now()}`, alt: sc.name }),
      h('span', { class: 'tag' }, sc.name)));
    $('#lcd-gallery').replaceChildren(...builtIn, ...media.map(m => h('div', { class: 'tile' + (this.cfg.mode === 'media' && this.cfg.media === m.id ? ' on' : ''), onclick: () => pick('media', m.id) },
      h('img', { src: `/api/lcd/media/${encodeURIComponent(m.id)}`, alt: '', loading: 'lazy' }),
      m.animated ? h('span', { class: 'tag' }, 'Animated') : null,
      h('button', { class: 'del', title: 'Delete', onclick: async e => { e.stopPropagation(); await act(api.send('DELETE', `/api/lcd/media/${encodeURIComponent(m.id)}`), 'Deleted.'); this.gallery(); } }, '×'))));
  },
  async upload(files) {
    for (const f of files) {
      const fd = new FormData();
      fd.append('file', f);
      const r = await act(api.send('POST', '/api/lcd/media', fd, true), `Uploaded ${f.name}.`);
      this.cfg.mode = 'media'; this.cfg.media = r.id;
    }
    this.push();
    this.gallery();
  },
};
$('#lcd-file').onchange = e => lcd.upload([...e.target.files]);
const drop = $('#lcd-drop');
drop.addEventListener('dragover', e => { e.preventDefault(); drop.classList.add('over'); });
drop.addEventListener('dragleave', () => drop.classList.remove('over'));
drop.addEventListener('drop', e => { e.preventDefault(); drop.classList.remove('over'); lcd.upload([...e.dataTransfer.files]); });

// ---------- Profiles ----------
const profiles = {
  enter() {
    $('#profile-list').replaceChildren(...state.settings.profiles.map(p => {
      const on = p.name === state.settings.activeProfile;
      return h('div', { class: 'profile' + (on ? ' on' : '') },
        h('div', {}, h('div', { class: 'name' }, p.name), h('div', { class: 'sub' }, `${EFFECT_INFO[p.effect.type]?.label ?? p.effect.type} · ${p.fanCurves.length} fan curve(s) · ${p.lcd.mode === 'media' ? 'custom screen' : `${p.lcd.screen ?? 'status'} screen`}`)),
        h('div', { class: 'btns' },
          on ? h('span', { class: 'muted' }, 'Active') : h('button', { class: 'btn small', onclick: () => activate(p.name) }, 'Activate'),
          on ? null : h('button', { class: 'btn small danger', onclick: async () => { await act(api.send('DELETE', `/api/profiles/${encodeURIComponent(p.name)}`), 'Profile deleted.'); await loadSettings(); this.enter(); } }, 'Delete')));
    }));
  },
};
async function activate(name) {
  await act(api.send('POST', `/api/profiles/${encodeURIComponent(name)}/activate`), `Profile "${name}" active.`);
  await loadSettings();
  pages[current]?.enter?.();
}
$('#profile-select').onchange = e => activate(e.target.value);
$('#profile-save').onclick = async () => {
  const name = $('#profile-name').value.trim();
  if (!name) return toast('Enter a name.', true);
  await act(api.send('POST', '/api/profiles', { name }), `Saved "${name}".`);
  $('#profile-name').value = '';
  await loadSettings();
  profiles.enter();
};

// ---------- Router ----------
const pages = { dashboard, lighting, layout, fans, lcd, profiles };
const titles = { dashboard: 'Dashboard', lighting: 'Lighting', layout: 'Layout', fans: 'Fan curves', lcd: 'Pump LCD', profiles: 'Profiles' };
let current = 'dashboard';
function route() {
  const page = (location.hash || '#dashboard').slice(1);
  current = pages[page] ? page : 'dashboard';
  for (const sec of $$('.page')) sec.hidden = sec.id !== `page-${current}`;
  for (const a of $$('.nav a')) a.classList.toggle('active', a.dataset.page === current);
  $('#page-title').textContent = titles[current];
  pages[current].enter?.();
  if (state.status) pages[current].status?.(state.status);
}
window.addEventListener('hashchange', route);

(async function init() {
  try {
    [state.effects] = await Promise.all([api.get('/api/effects'), loadSettings(), loadLeds()]);
    state.sensors = await api.get('/api/sensors');
  } catch (e) {
    toast(`Cannot reach the service: ${e.message}`, true);
  }
  await tickStatus();
  route();
  setInterval(async () => { try { state.sensors = await api.get('/api/sensors'); } catch { } }, 15000);
})();
