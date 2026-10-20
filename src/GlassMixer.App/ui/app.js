'use strict';
/*
  Glass Mixer UI: the mockup's design and behaviour, driven by real data from the C# host.

  Messages through WebView2:

    host -> page   { type: 'state', deviceId, deviceName, devices: [{ id, name }], masterVolume, masterMuted,
                     apps: [{ id, name, volume, muted, isSystem, active, exe, icon }] }
                   { type: 'levels', master, apps: { id: peak } }      about 30 per second
                   { type: 'minimized', on }   { type: 'pinned', on }

    page -> host   { type: 'ready' }   { type: 'log', text }
                   { type: 'setVolume', id, value }   value 0.0 to 1.0, id 'master' for the device
                   { type: 'setMute', id, muted }     { type: 'setDevice', id }
                   { type: 'drag' }  { type: 'dock' }  { type: 'resize', edge }  { type: 'size', w, h }
                   { type: 'minimize', on }  { type: 'pin', on }  { type: 'hide' }  { type: 'blur', radius }

  Opened in a normal browser (no WebView2), createFakeHost() stands in with sample data.
*/

const $ = (s, r = document) => r.querySelector(s);
const host = window.chrome?.webview ?? createFakeHost();
const send = (msg) => host.postMessage(msg);

// Page errors go to the host's log file (a desktop app has no visible console).
window.addEventListener('error', (e) => send({ type: 'log', text: `${e.message} at ${e.filename}:${e.lineno}` }));
window.addEventListener('unhandledrejection', (e) => send({ type: 'log', text: `Unhandled promise: ${e.reason}` }));

const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const pos = (percent) => `calc(8px + (100% - 16px) * ${percent / 100})`;
const escapeHtml = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const byId = (id) => document.querySelectorAll(`[data-id="${CSS.escape(id)}"]`);

/* ---------- icons (same drawings as the mockup) ---------- */
const svg = (inner) => `<svg viewBox="0 0 24 24" aria-hidden="true">${inner}</svg>`;
const SPK = '<path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z"/>';
function speaker(level, muted) {
  if (muted || level === 0) return svg(SPK + '<path d="M15.5 9.5l5 5M20.5 9.5l-5 5"/>');
  let s = SPK + '<path d="M15.5 9.5a3.5 3.5 0 0 1 0 5"/>';
  if (level >= 34) s += '<path d="M17.8 7.2a6.8 6.8 0 0 1 0 9.6"/>';
  if (level >= 67) s += '<path d="M20 5a10 10 0 0 1 0 14"/>';
  return svg(s);
}
const I = {
  chev: svg('<path d="M6 9l6 6 6-6"/>'),
  pin: svg('<path d="M9 4h6l-1 6 3 3H7l3-3z"/><path d="M12 13v7"/>'),
  look: svg('<path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/>'),
  close: svg('<path d="M6.5 6.5l11 11M17.5 6.5l-11 11"/>'),
  min: svg('<path d="M6 12.5h12"/>'),
  restore: svg('<path d="M14 4h6v6M10 20H4v-6M20 4l-6.5 6.5M4 20l6.5-6.5"/>'),
  dots: svg('<path d="M9 6h.01M15 6h.01M9 12h.01M15 12h.01M9 18h.01M15 18h.01"/>'),
  grip: svg('<path d="M4 14L14 4M4 9l5-5"/>'),
  bell: svg('<path d="M6 16v-5a6 6 0 0 1 12 0v5l1.5 2h-15z"/><path d="M10 20.5a2 2 0 0 0 4 0"/>'),
  speakers: svg('<rect x="6" y="3" width="12" height="18" rx="2.5"/><circle cx="12" cy="14.5" r="3"/><circle cx="12" cy="7.5" r="1"/>'),
  headphones: svg('<path d="M4 15v-3a8 8 0 0 1 16 0v3"/><rect x="3" y="14" width="4" height="6" rx="1.5"/><rect x="17" y="14" width="4" height="6" rx="1.5"/>'),
  monitor: svg('<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M9 20h6M12 16v4"/>'),
};
$('#btn-pin').innerHTML = I.pin;
$('#btn-look').innerHTML = I.look;
$('#btn-min').innerHTML = I.min;
$('#btn-close').innerHTML = I.close;
$('#btn-restore').innerHTML = I.restore;
$('#mini-drag').innerHTML = I.dots;
$('#grip').innerHTML = I.grip;
$('#dev-chev').outerHTML = I.chev.replace('<svg', '<svg class="chev"');

const hue = (id) => [...id].reduce((h, c) => (h * 31 + c.charCodeAt(0)) % 360, 7);
function appIcon(a) {
  if (a.icon) return `<span class="app-icon has-img"><img src="${a.icon}" alt=""></span>`;
  if (a.isSystem) return `<span class="app-icon" style="--c:#6f8284">${I.bell}</span>`;
  return `<span class="app-icon" style="--c:hsl(${hue(a.id)} 45% 40%)">${escapeHtml((a.name.trim()[0] || '?').toUpperCase())}</span>`;
}
const deviceIcon = (name) =>
  /headphone|headset|earbud|buds|airpods/i.test(name) ? I.headphones
  : /hdmi|display|monitor|\btv\b/i.test(name) ? I.monitor
  : I.speakers;
const deviceShort = (name) => name.replace(/\s*\(.*\)\s*$/, '') || name;

/* ---------- state ---------- */
const PROFILES = { default: 'Default', gaming: 'Gaming', focus: 'Focus', meeting: 'Meeting' };
const SIZES = { compact: [300, 560], regular: [404, 620], wide: [780, 560] };

let model = { deviceId: '', deviceName: '', devices: [], masterVolume: 0, masterMuted: false, apps: [], perAppOutputSupported: false };
const touchedAt = new Map(); // id -> when the user last moved that slider here
const meterState = new Map(); // id -> { level, peak, peakT }
let lastCountUpdate = 0;

const prefs = { mode: 'list', clear: 70, blur: 20, meters: true, size: 'regular', profiles: {}, profile: null };
try { Object.assign(prefs, JSON.parse(localStorage.getItem('glass-mixer') || '{}')); } catch { /* first run */ }
const savePrefs = () => { try { localStorage.setItem('glass-mixer', JSON.stringify(prefs)); } catch { /* not critical */ } };

// Look version 2: transparency works for real from this version on, so start once
// from the mockup's look. Settings chosen while it was broken don't carry over.
if (prefs.v !== 2) {
  Object.assign(prefs, { v: 2, blur: 20, clear: 70, meters: true });
  savePrefs();
}
// The glass-style buttons became the mockup's blur slider (0 = clear, up to 40px).
if (typeof prefs.blur !== 'number') { prefs.blur = 20; delete prefs.glass; savePrefs(); }

const appById = (id) => model.apps.find((a) => a.id === id);
const volOf = (id) => Math.round((id === 'master' ? model.masterVolume : appById(id)?.volume ?? 0) * 100);
const isMuted = (id) => (id === 'master' ? model.masterMuted : appById(id)?.muted) ?? false;
const nameOf = (id) => (id === 'master' ? 'all' : appById(id)?.name ?? id);

/* ---------- building the rows ---------- */
function extraHTML(a) {
  const id = escapeHtml(a.id), name = escapeHtml(a.name);
  if (a.isSystem) {
    return `<p class="extra-note">System sounds always play on the default device, centred.</p>`;
  }
  const outputs = model.perAppOutputSupported
    ? `<option value="default">Default (${escapeHtml(deviceShort(model.deviceName))})</option>` +
      model.devices.map((d) => `<option value="${escapeHtml(d.id)}">${escapeHtml(d.name)}</option>`).join('')
    : `<option>Default (${escapeHtml(deviceShort(model.deviceName))})</option>`;
  return `
    <label class="extra-row">Output
      <select data-out="${id}" aria-label="${name} output device"${model.perAppOutputSupported ? '' : ' disabled'}>${outputs}</select>
    </label>
    <label class="extra-row">Balance
      <input type="range" data-bal="${id}" min="-100" max="100" step="5" value="0" aria-label="${name} left-right balance">
      <span class="val bval">Center</span>
    </label>`;
}

function drawExtras() {
  for (const a of model.apps) {
    if (a.isSystem) continue;
    const row = document.querySelector(`.app[data-id="${CSS.escape(a.id)}"]`);
    if (!row) continue;
    const out = row.querySelector('[data-out]');
    if (out && document.activeElement !== out) out.value = a.outputId ?? 'default';
    const bal = row.querySelector('[data-bal]');
    const touched = performance.now() - (touchedAt.get('bal:' + a.id) ?? -1e9) < 800;
    if (bal && !touched) drawBalance(bal, Math.round(a.balance * 100));
  }
}

function drawBalance(input, value) {
  input.value = value;
  const at = (value + 100) / 2; // -100..100 -> 0..100 along the track
  input.style.setProperty('--a', pos(Math.min(50, at)));
  input.style.setProperty('--p', pos(Math.max(50, at)));
  input.closest('.extra-row').querySelector('.bval').textContent =
    value === 0 ? 'Center' : `${value < 0 ? 'L' : 'R'} ${Math.abs(value)}`;
}

function buildList() {
  $('#list').innerHTML = model.apps.map((a) => {
    const id = escapeHtml(a.id), name = escapeHtml(a.name);
    return `
    <li class="app" data-id="${id}">
      <div class="app-main">
        ${appIcon(a)}
        <div class="app-body">
          <div class="app-top"><span class="app-name">${name}</span><span class="val vval"></span></div>
          <div class="app-sub"></div>
          <input type="range" class="vol" min="0" max="100" aria-label="${name} volume">
          <div class="meter"><i></i><b></b></div>
        </div>
        <button class="icon-btn mute" data-mute="${id}" aria-pressed="false"></button>
        <button class="icon-btn more" data-more="${id}" aria-expanded="false" aria-label="More options for ${name}">${I.chev}</button>
      </div>
      <div class="app-extra" hidden>${extraHTML(a)}</div>
    </li>`;
  }).join('');
}

function stripHTML(id, name, iconHTML, extra = '') {
  return `
    <div class="strip ${extra}" data-id="${escapeHtml(id)}">
      ${iconHTML}
      <div class="fader-zone">
        <div class="vmeter"><i></i></div>
        <div class="fader" role="slider" tabindex="0" aria-label="${escapeHtml(name)} volume" aria-valuemin="0" aria-valuemax="100" aria-orientation="vertical">
          <span class="fill"></span><span class="knob"></span>
        </div>
      </div>
      <span class="val vval"></span>
      <button class="icon-btn mute" data-mute="${escapeHtml(id)}" aria-pressed="false"></button>
      <span class="strip-name" title="${escapeHtml(name)}">${escapeHtml(name)}</span>
    </div>`;
}

function buildBoard() {
  const board = $('#board');
  board.innerHTML =
    stripHTML('master', 'Master', `<span class="app-icon" style="--c: var(--accent)" id="master-chip"></span>`, 'master-strip') +
    model.apps.map((a) => stripHTML(a.id, a.name.replace(/^Google /, '').replace(/ media player$/i, ''), appIcon(a))).join('');
  board.querySelectorAll('.fader').forEach(attachFader);
}

function buildMiniApps() {
  $('#mini-apps').innerHTML = model.apps.map((a) =>
    `<button class="mini-app" data-mini="${escapeHtml(a.id)}" aria-label="Open ${escapeHtml(a.name)}" title="${escapeHtml(a.name)}">${appIcon(a)}</button>`).join('');
}

/* ---------- drawing ---------- */
function drawRow(id) {
  const v = volOf(id), m = isMuted(id);
  const touched = performance.now() - (touchedAt.get(id) ?? -1e9) < 800; // don't fight the user's own drag
  for (const el of byId(id)) {
    el.classList.toggle('is-muted', m);
    const range = el.querySelector('input.vol');
    if (range) {
      if (!touched && +range.value !== v) range.value = v;
      range.style.setProperty('--p', pos(+range.value));
    }
    const fader = el.querySelector('.fader');
    if (fader) {
      fader.style.setProperty('--p', `calc(7px + (100% - 14px) * ${v / 100})`);
      fader.setAttribute('aria-valuenow', v);
      fader.setAttribute('aria-valuetext', m ? `${v}%, muted` : `${v}%`);
    }
    const val = el.querySelector('.vval');
    if (val) val.textContent = range && touched ? range.value : v;
  }
  document.querySelectorAll(`[data-mute="${CSS.escape(id)}"]`).forEach((b) => {
    b.innerHTML = speaker(v, m);
    b.setAttribute('aria-pressed', m);
    b.setAttribute('aria-label', `${m ? 'Unmute' : 'Mute'} ${nameOf(id)}`);
  });
  if (id === 'master') {
    const chip = $('#master-chip');
    if (chip) chip.innerHTML = speaker(v, m);
  }
}

function drawSubtitles() {
  for (const a of model.apps) {
    const sub = document.querySelector(`.app[data-id="${CSS.escape(a.id)}"] .app-sub`);
    if (!sub) continue;
    sub.textContent = a.isSystem
      ? 'Notifications and alerts'
      : `${a.active ? 'Playing' : 'Silent'} · ${a.exe ?? a.id}`;
  }
}

function drawDevices() {
  const select = $('#dev');
  if (document.activeElement !== select) {
    const options = model.devices.map((d) => `<option value="${escapeHtml(d.id)}">${escapeHtml(d.name)}</option>`).join('');
    if (select.dataset.options !== options) { select.innerHTML = options; select.dataset.options = options; }
    select.value = model.deviceId;
  }
  $('#dev-icon').innerHTML = deviceIcon(model.deviceName);
  $('#foot-live').textContent = `Output: ${deviceShort(model.deviceName)}`;
}

function onState(next) {
  const key = (m) => m.apps.map((a) => a.id + (a.icon ? '+' : '')).join('|') + '/' + m.devices.map((d) => d.id).join('|');
  const rebuild = key(next) !== key(model) || next.deviceName !== model.deviceName;
  model = next;
  drawDevices();
  if (rebuild) {
    const open = [...document.querySelectorAll('.more[aria-expanded="true"]')].map((b) => b.dataset.more);
    buildList(); buildBoard(); buildMiniApps();
    open.forEach((id) => toggleExtra(id, true));
  }
  drawRow('master');
  model.apps.forEach((a) => drawRow(a.id));
  drawSubtitles();
  drawExtras();
  $('#empty').hidden = model.apps.length > 0;
  updateCounts();
}

/* ---------- live meters (same smoothing and peak hold as the mockup) ----------
   Windows sends peak levels about 30 times a second. Painting only when one arrives
   looks steppy, so the meters run their own animation at the screen's frame rate,
   easing toward the latest level exactly the way the mockup's meters do. */
const latestPeak = new Map(); // id -> last peak from the host, 0.0 to 1.0

function stepMeter(id, t) {
  const s = meterState.get(id) ?? { level: 0, peak: 0, peakT: 0 };
  // Square root: quiet sounds stay visible. 0.9: leave the red end for real peaks, as the mockup does.
  const target = Math.min(1, Math.sqrt(latestPeak.get(id) || 0) * 0.9);
  s.level += (target - s.level) * (target > s.level ? 0.55 : 0.14); // fast rise, slow fall
  if (s.level >= s.peak) { s.peak = s.level; s.peakT = t; }
  else if (t - s.peakT > 0.9) s.peak = Math.max(s.level, s.peak - 0.012);
  meterState.set(id, s);
  for (const el of byId(id)) {
    el.querySelectorAll('.meter').forEach((m) => {
      m.firstElementChild.style.clipPath = `inset(0 ${(1 - s.level) * 100}% 0 0)`;
      const b = m.querySelector('b');
      b.style.left = `calc(${s.peak * 100}% - 2px)`;
      b.style.opacity = s.peak > 0.02 ? 0.55 : 0;
    });
    const v = el.querySelector('.vmeter i');
    if (v) v.style.clipPath = `inset(${(1 - s.level) * 100}% 0 0 0)`;
  }
}

const sounding = (id) => (meterState.get(id)?.level ?? 0) > 0.05;

function updateCounts() {
  const live = model.apps.filter((a) => sounding(a.id)).length;
  $('#app-count').textContent = `${live} of ${model.apps.length} making sound`;
  document.querySelectorAll('[data-mini]').forEach((b) => b.classList.toggle('sounding', sounding(b.dataset.mini)));
}

function onLevels(levels) {
  latestPeak.set('master', levels.master);
  for (const a of model.apps) latestPeak.set(a.id, levels.apps[a.id] ?? 0);
}

function animateMeters(now) {
  const t = now / 1000;
  if (prefs.meters && !document.hidden) {
    stepMeter('master', t);
    for (const a of model.apps) stepMeter(a.id, t);
  }
  if (t - lastCountUpdate > 0.4) { updateCounts(); lastCountUpdate = t; }
  requestAnimationFrame(animateMeters);
}

/* ---------- changing volumes ---------- */
function setVolume(id, percent, opts = {}) {
  percent = clamp(Math.round(percent), 0, 100);
  touchedAt.set(id, performance.now());
  send({ type: 'setVolume', id, value: percent / 100 });
  if (id === 'master') model.masterVolume = percent / 100;
  else if (appById(id)) appById(id).volume = percent / 100;
  if (!opts.fromProfile && isMuted(id) && percent > 0) setMute(id, false); // moving a muted slider unmutes it
  if (!opts.fromProfile && id !== 'master' && prefs.profile) { prefs.profile = null; savePrefs(); drawChips(); }
  for (const el of byId(id)) { const r = el.querySelector('input.vol'); if (r) r.value = percent; }
  drawRow(id);
}

function setMute(id, muted) {
  send({ type: 'setMute', id, muted });
  if (id === 'master') model.masterMuted = muted;
  else if (appById(id)) appById(id).muted = muted;
  drawRow(id);
}

/* ---------- profiles: saved mixes ---------- */
function drawChips() {
  $('#chips').innerHTML = Object.entries(PROFILES).map(([key, label]) => {
    const saved = !!prefs.profiles[key];
    const title = saved ? 'Click to apply. Right-click to save the current mix here.' : `Click to save the current mix as ${label}.`;
    return `<button class="chip${saved ? '' : ' unsaved'}" data-profile="${key}" aria-pressed="${prefs.profile === key}" title="${title}">${label}</button>`;
  }).join('');
}

function saveProfile(key) {
  prefs.profiles[key] = Object.fromEntries(model.apps.map((a) => [a.id, { v: a.volume, m: a.muted }]));
  prefs.profile = key;
  savePrefs(); drawChips();
  toast(`Saved this mix as ${PROFILES[key]}. Right-click a profile to save over it.`);
}

function applyProfile(key) {
  const mix = prefs.profiles[key];
  prefs.profile = key; savePrefs(); drawChips();
  const targets = model.apps.filter((a) => mix[a.id]);
  for (const a of targets) if (a.muted !== mix[a.id].m) setMute(a.id, mix[a.id].m);
  const from = Object.fromEntries(targets.map((a) => [a.id, a.volume * 100]));
  const step = (k) => targets.forEach((a) => setVolume(a.id, from[a.id] + (mix[a.id].v * 100 - from[a.id]) * k, { fromProfile: true }));
  if (reduce) { step(1); return; }
  const t0 = performance.now();
  const frame = (now) => {
    const k = Math.min(1, (now - t0) / 280);
    step(1 - Math.pow(1 - k, 3));
    if (k < 1) requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
}

/* ---------- appearance ---------- */
function applyLook() {
  document.documentElement.style.setProperty('--glass-a', (0.92 - prefs.clear / 100 * 0.88).toFixed(3));
  document.body.classList.toggle('no-meters', !prefs.meters);
  const clear = $('#opt-clear');
  clear.value = prefs.clear;
  clear.style.setProperty('--p', pos(prefs.clear));
  $('#val-clear').textContent = `${prefs.clear}%`;
  $('#opt-meters').checked = prefs.meters;
  const blur = $('#opt-blur');
  blur.value = prefs.blur;
  blur.style.setProperty('--p', pos(prefs.blur / 40 * 100));
  $('#val-blur').textContent = prefs.blur === 0 ? 'Off' : `${prefs.blur}px`;
  document.querySelectorAll('[data-size]').forEach((b) => b.setAttribute('aria-pressed', b.dataset.size === prefs.size));
}

function setMode(mode) {
  prefs.mode = mode;
  $('#list').hidden = mode !== 'list';
  $('#board').hidden = mode !== 'board';
  $('#mode-list').setAttribute('aria-pressed', mode === 'list');
  $('#mode-board').setAttribute('aria-pressed', mode === 'board');
}

function toggleExtra(id, open) {
  const btn = document.querySelector(`[data-more="${CSS.escape(id)}"]`);
  if (!btn) return;
  btn.setAttribute('aria-expanded', open);
  btn.closest('.app').querySelector('.app-extra').hidden = !open;
}

// The mockup's pop-in on whichever view is showing (restart the animation each time).
function popIn() {
  const el = document.body.classList.contains('is-min') ? $('#mini') : $('#panel');
  el.classList.remove('pop');
  void el.offsetWidth; // reading layout here lets the same animation play again
  el.classList.add('pop');
}

let pendingFlash = null;
function setMinimizedView(on) {
  const changed = document.body.classList.contains('is-min') !== on;
  document.body.classList.toggle('is-min', on);
  $('#panel').hidden = on;
  $('#mini').hidden = !on;
  if (changed) popIn();
  if (!on && pendingFlash) {
    const row = document.querySelector(`.app[data-id="${CSS.escape(pendingFlash)}"]`);
    if (prefs.mode !== 'list') setMode('list');
    row?.scrollIntoView({ block: 'nearest', behavior: reduce ? 'auto' : 'smooth' });
    row?.classList.remove('flash'); void row?.offsetWidth; row?.classList.add('flash');
    pendingFlash = null;
  }
}

/* ---------- size badge (shown while the window changes size) ---------- */
const badge = $('#size-badge');
const tier = (w) => (w <= 290 ? 'Mini' : w <= 340 ? 'Compact' : w < 560 ? 'Regular' : 'Wide');
let badgeTimer, presetAt = 0;
window.addEventListener('resize', () => {
  if (document.body.classList.contains('is-min')) return;
  badge.textContent = `${tier(innerWidth)} · ${innerWidth} × ${innerHeight}`;
  badge.hidden = false;
  clearTimeout(badgeTimer);
  badgeTimer = setTimeout(() => { badge.hidden = true; }, 1200);
  if (performance.now() - presetAt > 600 && prefs.size) { prefs.size = null; savePrefs(); applyLook(); }
});

function toast(text) {
  const el = $('#toast');
  el.textContent = text;
  el.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { el.hidden = true; }, 2600);
}

/* ---------- messages from the host ---------- */
host.addEventListener('message', (e) => {
  const msg = e.data;
  if (msg.type === 'state') onState(msg);
  else if (msg.type === 'levels') onLevels(msg);
  else if (msg.type === 'minimized') setMinimizedView(msg.on);
  else if (msg.type === 'shown') popIn();
  else if (msg.type === 'pinned') $('#btn-pin').setAttribute('aria-pressed', msg.on);
});

/* ---------- user input ---------- */
document.addEventListener('input', (e) => {
  const t = e.target;
  const row = t.closest('[data-id]');
  if (t.matches('input.vol') && row) setVolume(row.dataset.id, +t.value);
  else if (t.dataset.bal) {
    touchedAt.set('bal:' + t.dataset.bal, performance.now());
    drawBalance(t, +t.value);
    send({ type: 'setBalance', id: t.dataset.bal, value: +t.value / 100 });
  }
  else if (t.id === 'opt-clear') { prefs.clear = +t.value; applyLook(); savePrefs(); }
  else if (t.id === 'opt-blur') { prefs.blur = +t.value; applyLook(); savePrefs(); send({ type: 'blur', radius: prefs.blur }); }
});

document.addEventListener('change', (e) => {
  const t = e.target;
  if (t.id === 'opt-meters') { prefs.meters = t.checked; applyLook(); savePrefs(); }
  else if (t.id === 'dev') { send({ type: 'setDevice', id: t.value }); t.blur(); }
  else if (t.dataset.out) {
    send({ type: 'setOutput', id: t.dataset.out, deviceId: t.value === 'default' ? null : t.value });
    t.blur();
    toast(`${appById(t.dataset.out)?.name ?? 'The app'} switches when it next starts a sound. Pause and play if it doesn't move.`);
  }
});

document.addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  if (b.dataset.mute) toggleMute(b.dataset.mute);
  else if (b.dataset.more) toggleExtra(b.dataset.more, b.getAttribute('aria-expanded') !== 'true');
  else if (b.dataset.profile) (prefs.profiles[b.dataset.profile] ? applyProfile : saveProfile)(b.dataset.profile);
  else if (b.dataset.mini) { pendingFlash = b.dataset.mini; send({ type: 'minimize', on: false }); }
  else if (b.dataset.size) {
    prefs.size = b.dataset.size; presetAt = performance.now();
    const [w, h] = SIZES[prefs.size];
    send({ type: 'size', w, h }); applyLook(); savePrefs();
  }
  else if (b.id === 'mode-list') { setMode('list'); savePrefs(); }
  else if (b.id === 'mode-board') { setMode('board'); savePrefs(); }
  else if (b.id === 'btn-pin') { const on = b.getAttribute('aria-pressed') !== 'true'; b.setAttribute('aria-pressed', on); send({ type: 'pin', on }); }
  else if (b.id === 'btn-look') { const open = b.getAttribute('aria-expanded') !== 'true'; b.setAttribute('aria-expanded', open); $('#look').hidden = !open; }
  else if (b.id === 'btn-min') send({ type: 'minimize', on: true });
  else if (b.id === 'btn-restore') send({ type: 'minimize', on: false });
  else if (b.id === 'btn-close') send({ type: 'hide' });
});

function toggleMute(id) {
  setMute(id, !isMuted(id));
  if (id !== 'master' && prefs.profile) { prefs.profile = null; savePrefs(); drawChips(); }
}

// Right-click a profile to save the current mix over it.
$('#chips').addEventListener('contextmenu', (e) => {
  const chip = e.target.closest('[data-profile]');
  if (!chip) return;
  e.preventDefault();
  saveProfile(chip.dataset.profile);
});

// Scroll the wheel over any app (row or strip) or the mini bar to nudge its volume.
document.addEventListener('wheel', (e) => {
  const row = e.target.closest('[data-id]');
  if (!row || e.target.closest('select')) return;
  e.preventDefault();
  setVolume(row.dataset.id, volOf(row.dataset.id) + (e.deltaY < 0 ? 2 : -2));
}, { passive: false });

// Vertical faders in the Mixer layout.
function attachFader(fd) {
  const id = fd.closest('[data-id]').dataset.id;
  const fromY = (y) => { const r = fd.getBoundingClientRect(); return (1 - (y - r.top - 7) / (r.height - 14)) * 100; };
  fd.addEventListener('pointerdown', (e) => { fd.setPointerCapture(e.pointerId); fd.focus(); setVolume(id, fromY(e.clientY)); });
  fd.addEventListener('pointermove', (e) => { if (fd.hasPointerCapture(e.pointerId)) setVolume(id, fromY(e.clientY)); });
  fd.addEventListener('keydown', (e) => {
    const step = e.shiftKey ? 10 : 1, v = volOf(id);
    const map = { ArrowUp: v + step, ArrowRight: v + step, ArrowDown: v - step, ArrowLeft: v - step, PageUp: v + 10, PageDown: v - 10, Home: 0, End: 100 };
    if (e.key in map) { e.preventDefault(); setVolume(id, map[e.key]); }
  });
}

// Moving and resizing are done by Windows itself; the page only asks for it.
let lastHeadDown = 0;
$('#p-head').addEventListener('mousedown', (e) => {
  if (e.button !== 0 || e.target.closest('button')) return;
  const now = performance.now();
  if (now - lastHeadDown < 350) send({ type: 'dock' }); // double-click: back to the corner
  else send({ type: 'drag' });
  lastHeadDown = now;
});
$('#mini-drag').addEventListener('mousedown', (e) => { if (e.button === 0) send({ type: 'drag' }); });
$('#grip').addEventListener('mousedown', (e) => { if (e.button === 0) send({ type: 'resize', edge: 'top-left' }); });

/* ---------- fake host for working in a normal browser ---------- */
function createFakeHost() {
  const listeners = [];
  const fake = {
    deviceId: 'spk', deviceName: 'Speakers (Realtek(R) Audio)',
    devices: [{ id: 'spk', name: 'Speakers (Realtek(R) Audio)' }, { id: 'hp', name: 'Headphones (USB-C DAC)' }],
    masterVolume: 0.78, masterMuted: false,
    apps: [
      { id: 'spotify', name: 'Spotify', volume: 0.72, muted: false, isSystem: false, active: true, exe: 'Spotify.exe', icon: null },
      { id: 'chrome', name: 'Google Chrome', volume: 0.55, muted: false, isSystem: false, active: true, exe: 'chrome.exe', icon: null },
      { id: 'discord', name: 'Discord', volume: 0.85, muted: false, isSystem: false, active: true, exe: 'Discord.exe', icon: null },
      { id: 'system', name: 'System sounds', volume: 0.3, muted: false, isSystem: true, active: false, exe: null, icon: null },
    ],
  };
  const emit = (data) => listeners.forEach((fn) => fn({ data: structuredClone(data) }));
  const emitState = () => emit({ type: 'state', ...fake });
  return {
    addEventListener: (_type, fn) => listeners.push(fn),
    postMessage(msg) {
      const app = fake.apps.find((a) => a.id === msg.id);
      switch (msg.type) {
        case 'ready':
          emitState();
          setInterval(() => emit({
            type: 'levels',
            master: fake.masterMuted ? 0 : Math.random() * 0.4,
            apps: Object.fromEntries(fake.apps.map((a) => [a.id, a.muted || !a.active ? 0 : Math.random() * a.volume * 0.5])),
          }), 33);
          break;
        case 'setVolume': if (msg.id === 'master') fake.masterVolume = msg.value; else if (app) app.volume = msg.value; emitState(); break;
        case 'setMute': if (msg.id === 'master') fake.masterMuted = msg.muted; else if (app) app.muted = msg.muted; emitState(); break;
        case 'setDevice': { const d = fake.devices.find((x) => x.id === msg.id); if (d) { fake.deviceId = d.id; fake.deviceName = d.name; } emitState(); break; }
        case 'minimize': emit({ type: 'minimized', on: msg.on }); break;
      }
    },
  };
}

/* ---------- start ---------- */
applyLook();
setMode(prefs.mode);
drawChips();
send({ type: 'blur', radius: prefs.blur });
requestAnimationFrame(animateMeters);
popIn(); // the first open: the host's "shown" arrives before the page is listening
send({ type: 'ready' });
