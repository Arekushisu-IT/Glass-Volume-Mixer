'use strict';
/*
  Glass Mixer UI: the mockup's design and behaviour, driven by real data from the C# host.

  The window lives at a screen edge and has two shapes: the panel (the mixer) and the pill
  (the mixer tucked away). The host sizes and places the window; this page draws whichever
  shape it is told to.

  Messages through WebView2:

    host -> page   { type: 'state', deviceId, deviceName, devices: [{ id, name }], masterVolume, masterMuted,
                     apps: [{ id, name, volume, muted, isSystem, active, exe, icon }] }
                   { type: 'levels', master, apps: { id: peak }, mic }   about 30 per second
                   { type: 'volume', masterVolume, masterMuted }        the volume keys (or anyone) changed it
                   { type: 'mic', mic: { available, name, muted, users: [app names] } }
                   { type: 'media', tracks: [{ id, title, artist, art, playing, position, duration, canPrev, canNext }], current }
                   { type: 'game', on }                                 a full-screen game is in front
                   { type: 'shape', shape: 'panel' | 'pill', edge }     clear the old shape, then say 'shapeReady'
                   { type: 'placed', shape }                            the window is in place: show the shape
                   { type: 'edge', edge }                               it was dragged to another edge
                   { type: 'backdrop', light }

    page -> host   { type: 'ready' }   { type: 'log', text }
                   { type: 'setVolume', id, value }   value 0.0 to 1.0, id 'master' for the device
                   { type: 'setMute', id, muted }     { type: 'setDevice', id }   { type: 'setMicMute', muted }
                   { type: 'media', id, action }      action: 'toggle', 'next', 'prev'
                   { type: 'drag' }  { type: 'tuck' }  { type: 'peek', click }  { type: 'shapeReady' }
                   { type: 'content', h }  the panel's natural height     { type: 'pill', w, h, r }  the pill's size
                   { type: 'blur', radius }  { type: 'autoTuck', on }  { type: 'busy', on }

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
const fmt = (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;

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
  look: svg('<path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/>'),
  bell: svg('<path d="M6 16v-5a6 6 0 0 1 12 0v5l1.5 2h-15z"/><path d="M10 20.5a2 2 0 0 0 4 0"/>'),
  music: svg('<path d="M9 17.5V6l10-2v11.5"/><circle cx="6.5" cy="17.5" r="2.5"/><circle cx="16.5" cy="15.5" r="2.5"/>'),
  speakers: svg('<rect x="6" y="3" width="12" height="18" rx="2.5"/><circle cx="12" cy="14.5" r="3"/><circle cx="12" cy="7.5" r="1"/>'),
  headphones: svg('<path d="M4 15v-3a8 8 0 0 1 16 0v3"/><rect x="3" y="14" width="4" height="6" rx="1.5"/><rect x="17" y="14" width="4" height="6" rx="1.5"/>'),
  monitor: svg('<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M9 20h6M12 16v4"/>'),
  mic: svg('<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M8.5 21h7"/>'),
  micOff: svg('<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M8.5 21h7M4 4l16 16"/>'),
  prev: svg('<path d="M18 6.5v11L9.5 12z" fill="currentColor"/><path d="M6.5 6.5v11"/>'),
  next: svg('<path d="M6 6.5v11l8.5-5.5z" fill="currentColor"/><path d="M17.5 6.5v11"/>'),
  play: svg('<path d="M8.5 5.5v13l10-6.5z"/>'),
  pause: svg('<rect x="7" y="5.5" width="3.5" height="13" rx="1"/><rect x="13.5" y="5.5" width="3.5" height="13" rx="1"/>'),
  auto: svg('<path d="M12 3.5v2.5M12 18v2.5M3.5 12H6M18 12h2.5M6 6l1.8 1.8M16.2 16.2 18 18M6 18l1.8-1.8M16.2 7.8 18 6"/><circle cx="12" cy="12" r="3"/>'),
};
// The tuck button's arrow points at the edge the mixer is on.
const TUCK = {
  top: svg('<path d="M5 4h14"/><path d="M12 20V9"/><path d="M7.5 13.5 12 9l4.5 4.5"/>'),
  left: svg('<path d="M4 5v14"/><path d="M20 12H9"/><path d="M13.5 7.5 9 12l4.5 4.5"/>'),
  right: svg('<path d="M20 5v14"/><path d="M4 12h11"/><path d="M10.5 7.5 15 12l-4.5 4.5"/>'),
};
$('#btn-look').innerHTML = I.look;
$('#dev-chev').outerHTML = I.chev.replace('<svg', '<svg class="chev"');
$('#np-prev').innerHTML = I.prev;
$('#np-next').innerHTML = I.next;

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

let model = { deviceId: '', deviceName: '', devices: [], masterVolume: 0, masterMuted: false, apps: [], perAppOutputSupported: false };
const touchedAt = new Map(); // id -> when the user last moved that slider here
const meterState = new Map(); // id -> { level, peak, peakT }
let lastCountUpdate = 0;

const prefs = { mode: 'list', clear: 70, blur: 20, profiles: {}, profile: null, tuck: true, activity: true,
  auto: { meeting: true, gaming: true, focus: false } };
try { Object.assign(prefs, JSON.parse(localStorage.getItem('glass-mixer') || '{}')); } catch { /* first run */ }
const savePrefs = () => { try { localStorage.setItem('glass-mixer', JSON.stringify(prefs)); } catch { /* not critical */ } };

// Look version 2: transparency works for real from this version on, so start once
// from the mockup's look. Settings chosen while it was broken don't carry over.
if (prefs.v !== 2) {
  Object.assign(prefs, { v: 2, blur: 20, clear: 70 });
  savePrefs();
}
if (typeof prefs.blur !== 'number') { prefs.blur = 20; delete prefs.glass; savePrefs(); }
prefs.auto = { meeting: true, gaming: true, focus: false, ...(prefs.auto ?? {}) };
// Gone in 1.1, with the edge-docked mixer: pin, size presets, the meters switch.
delete prefs.pinned; delete prefs.size; delete prefs.meters;

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
    const t = trackOfApp(a);
    sub.textContent = a.isSystem ? 'Notifications and alerts'
      : t ? `${t.playing ? 'Now playing' : 'Paused'} · ${t.title}`
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
}

const wasActive = new Map(); // id -> when it was last seen silent, to spot an app that starts playing
let stateSince = 0;
function onState(next) {
  const key = (m) => m.apps.map((a) => a.id + (a.icon ? '+' : '')).join('|') + '/' + m.devices.map((d) => d.id).join('|');
  const rebuild = key(next) !== key(model) || next.deviceName !== model.deviceName;
  const deviceChanged = model.deviceId && next.deviceId !== model.deviceId;
  // Keep a master value the user is dragging or the volume keys just sent.
  if (performance.now() - (touchedAt.get('master') ?? -1e9) < 800) { next.masterVolume = model.masterVolume; next.masterMuted = model.masterMuted; }
  const before = new Map(model.apps.map((a) => [a.id, a.active]));
  model = next;
  drawDevices();
  if (rebuild) {
    const open = [...document.querySelectorAll('.more[aria-expanded="true"]')].map((b) => b.dataset.more);
    buildList(); buildBoard();
    open.forEach((id) => toggleExtra(id, true));
  }
  drawRow('master');
  model.apps.forEach((a) => drawRow(a.id));
  drawSubtitles();
  drawExtras();
  drawNP();
  $('#empty').hidden = model.apps.length > 0;
  updateCounts();
  measureSoon();

  // The pill's news: another output, or an app that starts playing.
  if (deviceChanged) notifyIsland('device');
  const now = performance.now();
  if (!stateSince) stateSince = now;
  for (const a of model.apps) {
    if (a.isSystem) continue;
    if (!a.active) wasActive.set(a.id, now);
    else if (before.get(a.id) === false && now - stateSince > 3000 && !trackOfApp(a)) notifyIsland('play', a.id);
  }
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
  if (view !== 'panel') return;
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
}

function onLevels(levels) {
  latestPeak.set('master', levels.master);
  for (const a of model.apps) latestPeak.set(a.id, levels.apps[a.id] ?? 0);
  latestPeak.set('mic', levels.mic ?? 0);
}

function animateMeters(now) {
  const t = now / 1000;
  if (!document.hidden) {
    stepMeter('master', t);
    if (view === 'panel') {
      for (const a of model.apps) stepMeter(a.id, t);
      const s = meterState.get('mic') ?? { level: 0 };
      const target = mic.muted ? 0 : Math.min(1, Math.sqrt(latestPeak.get('mic') || 0) * 0.9);
      s.level += (target - s.level) * (target > s.level ? 0.5 : 0.18);
      meterState.set('mic', s);
      $('#mic-level').style.clipPath = `inset(0 ${(1 - s.level) * 100}% 0 0)`;
      drawNPTime();
    } else if (view === 'pill') {
      drawPill(now);
    }
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
    const on = prefs.profile === key;
    return `<button class="chip${saved ? '' : ' unsaved'}" data-profile="${key}" aria-pressed="${on}" data-auto="${on && auto.active === key}" title="${title}">${label}</button>`;
  }).join('');
  // An automatic profile needs a saved mix to switch to.
  document.querySelectorAll('[data-note]').forEach((n) => {
    n.textContent = prefs.profiles[n.dataset.note] ? '' : `save a ${PROFILES[n.dataset.note]} mix first`;
  });
}

function saveProfile(key) {
  prefs.profiles[key] = Object.fromEntries(model.apps.map((a) => [a.id, { v: a.volume, m: a.muted }]));
  prefs.profile = key;
  savePrefs(); drawChips();
  toast(`Saved this mix as ${PROFILES[key]}. Right-click a profile to save over it.`);
}

// A mix: { appId: { v: volume 0..1, m: muted } }. Apps not in it keep their level.
function applyMix(mix, key) {
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
const applyProfile = (key) => applyMix(prefs.profiles[key], key);

/* ---------- automatic profiles ----------
   Like macOS Focus modes: a rule switches a saved profile on while something is true (a call,
   a game, work hours) and puts your own mix back when it stops. Picking a profile yourself wins. */
const cond = { call: false, game: false, work: false };
const RULES = [ // first match wins
  { profile: 'meeting', when: 'call', on: 'A call started', off: 'The call ended' },
  { profile: 'gaming', when: 'game', on: 'A game is open', off: 'The game closed' },
  { profile: 'focus', when: 'work', on: 'Work hours', off: 'Work hours are over' },
];
const auto = { active: null, snap: null, skip: null };
const ruleFor = (p) => RULES.find((r) => r.profile === p);
function evaluateAuto() {
  const want = RULES.find((r) => prefs.auto[r.profile] && cond[r.when] && prefs.profiles[r.profile])?.profile ?? null;
  if (auto.skip !== null) { if (want === auto.skip) return; auto.skip = null; }
  if (want === auto.active) return;
  const old = auto.active;
  if (!want) {
    const s = auto.snap;
    auto.active = null; auto.snap = null;
    applyMix(s.mix, s.profile);
    notifyIsland('profile-off', { from: old, to: s.profile });
    return;
  }
  if (!old) auto.snap = { profile: prefs.profile, mix: Object.fromEntries(model.apps.map((a) => [a.id, { v: a.volume, m: a.muted }])) };
  auto.active = want;
  applyProfile(want);
  notifyIsland('profile', want);
}
function pickProfile(key) { // a profile you choose yourself
  if (auto.active) { auto.skip = auto.active; auto.active = null; auto.snap = null; }
  applyProfile(key);
}
const isWorkHours = (d = new Date()) => d.getDay() >= 1 && d.getDay() <= 5 && d.getHours() >= 9 && d.getHours() < 17;
setInterval(() => { cond.work = isWorkHours(); evaluateAuto(); }, 30000);

/* ---------- Now Playing ----------
   From Windows' media controls (MediaWatcher on the host): the same song, artist and cover
   the Windows volume flyout shows, with play/pause, next and previous. */
let tracks = [], currentTrack = null, npPick = null, mediaAt = 0;
const appForTrack = (t) => {
  const key = t.id.replace(/\.exe$/i, '').split('!').pop().toLowerCase();
  return model.apps.find((a) => a.id === key || (a.exe ?? '').replace(/\.exe$/i, '').toLowerCase() === key);
};
const trackOfApp = (a) => tracks.find((t) => appForTrack(t) === a);
function trackName(t) {
  const a = appForTrack(t);
  if (a) return a.name.replace(/ media player$/i, '');
  if (/ZuneMusic/i.test(t.id)) return 'Media Player';
  if (/ZuneVideo/i.test(t.id)) return 'Movies & TV';
  return t.id.split('!')[0].replace(/\.exe$/i, '').replace(/_.*$/, '').split('.').pop();
}
function npTrack() {
  return tracks.find((t) => t.id === npPick) ?? tracks.find((t) => t.id === currentTrack) ?? tracks.find((t) => t.playing) ?? tracks[0] ?? null;
}
const artHTML = (t) => t.art ? `<img src="${t.art}" alt="">` : I.music;
function artColors(el, t) {
  const h = hue(t.id);
  el.style.setProperty('--a1', `hsl(${h} 70% 62%)`);
  el.style.setProperty('--a2', `hsl(${(h + 70) % 360} 60% 42%)`);
}
function onMedia(msg) {
  const before = npTrack();
  tracks = msg.tracks; currentTrack = msg.current; mediaAt = performance.now();
  const now = npTrack();
  drawNP(); drawSubtitles(); measureSoon();
  // A new song, or play/pause (the keyboard's media keys, or the app itself): show it on the pill.
  if (now && (!before || before.id !== now.id || before.title !== now.title || before.playing !== now.playing)) notifyIsland('track');
}
function drawNP() {
  const t = npTrack();
  $('#np').hidden = !t;
  if (!t) return;
  const a = appForTrack(t);
  artColors($('#np'), t);
  const art = $('#np-art');
  if (art.dataset.key !== t.id + t.title) { art.innerHTML = artHTML(t); art.dataset.key = t.id + t.title; }
  $('#np-src').innerHTML = `${a ? appIcon(a) : ''}<span>${escapeHtml(trackName(t))}</span>`;
  $('#np-src').disabled = tracks.length < 2;
  $('#np-title').textContent = t.title;
  $('#np-artist').textContent = t.artist;
  $('#np-play').innerHTML = t.playing ? I.pause : I.play;
  $('#np-play').setAttribute('aria-label', t.playing ? 'Pause' : 'Play');
  $('#np-prev').disabled = !t.canPrev;
  $('#np-next').disabled = !t.canNext;
  $('#np-progress').hidden = !(t.duration > 0);
  $('#np-len').textContent = fmt(t.duration);
  drawNPTime();
}
function drawNPTime() {
  const t = npTrack();
  if (!t || !(t.duration > 0)) return;
  // Between updates from Windows, count the seconds here.
  const at = Math.min(t.duration, t.position + (t.playing ? (performance.now() - mediaAt) / 1000 : 0));
  const text = fmt(at);
  if ($('#np-time').textContent !== text) {
    $('#np-time').textContent = text;
    $('#np-fill').style.width = `${(at / t.duration) * 100}%`;
  }
}

/* ---------- microphone ---------- */
let mic = { available: false, name: '', muted: false, users: [] };
function onMic(next) {
  const was = mic;
  mic = next;
  cond.call = mic.users.length > 0;
  const el = $('#mic');
  el.hidden = !mic.available && !mic.users.length;
  el.classList.toggle('in-use', mic.users.length > 0);
  el.classList.toggle('muted', mic.muted);
  $('#mic-name').title = mic.name;
  $('#mic-use').textContent = mic.users.length ? `In use by ${mic.users.join(', ')}` : 'Not in use';
  const b = $('#mic-mute');
  b.innerHTML = mic.muted ? I.micOff : I.mic;
  b.setAttribute('aria-pressed', mic.muted);
  b.setAttribute('aria-label', mic.muted ? 'Unmute microphone' : 'Mute microphone');
  b.title = `${mic.muted ? 'Unmute' : 'Mute'} microphone (Ctrl+Alt+K)`;
  $('#isl-mic').hidden = !mic.users.length;
  measureSoon();
  if (mic.users.length && !was.users.length) notifyIsland('mic');
  if (mic.muted !== was.muted && was.available) notifyIsland('micmute');
  evaluateAuto();
}

/* ---------- appearance ---------- */
// Adaptive contrast. The host reports how bright the screen behind the mixer is (0 to 1).
// Light text on glass over a white page is unreadable, so the tint is never allowed to
// drop below what the text needs: nothing over a dark background, more as it gets brighter.
// In the light theme (dark text) it works the other way round.
let backdropLight = null;
const darkTheme = matchMedia('(prefers-color-scheme: dark)');
function tintNeeded() {
  if (backdropLight === null) return 0;
  const clash = darkTheme.matches ? backdropLight : 1 - backdropLight; // how much the backdrop fights the text
  return clamp((clash - 0.3) / 0.7, 0, 1) * 0.74;
}

function applyLook() {
  const chosen = 0.92 - prefs.clear / 100 * 0.88; // the Transparency slider
  document.documentElement.style.setProperty('--glass-a', Math.max(chosen, tintNeeded()).toFixed(3));
  const clear = $('#opt-clear');
  clear.value = prefs.clear;
  clear.style.setProperty('--p', pos(prefs.clear));
  $('#val-clear').textContent = `${prefs.clear}%`;
  const blur = $('#opt-blur');
  blur.value = prefs.blur;
  blur.style.setProperty('--p', pos(prefs.blur / 40 * 100));
  $('#val-blur').textContent = prefs.blur === 0 ? 'Off' : `${prefs.blur}px`;
  $('#opt-tuck').checked = prefs.tuck;
  $('#opt-activity').checked = prefs.activity;
  for (const k of Object.keys(prefs.auto)) { const box = $('#auto-' + k); if (box) box.checked = prefs.auto[k]; }
}

function setMode(mode) {
  prefs.mode = mode;
  $('#list').hidden = mode !== 'list';
  $('#board').hidden = mode !== 'board';
  $('#mode-list').setAttribute('aria-pressed', mode === 'list');
  $('#mode-board').setAttribute('aria-pressed', mode === 'board');
  measureSoon();
}

function toggleExtra(id, open) {
  const btn = document.querySelector(`[data-more="${CSS.escape(id)}"]`);
  if (!btn) return;
  btn.setAttribute('aria-expanded', open);
  btn.closest('.app').querySelector('.app-extra').hidden = !open;
  measureSoon();
}

function toast(text) {
  const el = $('#toast');
  el.textContent = text;
  el.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { el.hidden = true; }, 2600);
}

/* ---------- the panel's height ----------
   The window is as tall as the panel's content (up to what the screen allows), so it
   never shows empty glass. Report the natural height whenever it changes. */
let measured = 0, measureQueued = false;
function measureSoon() {
  if (measureQueued) return;
  measureQueued = true;
  requestAnimationFrame(() => {
    measureQueued = false;
    if (view !== 'panel') return;
    // + bottom padding + border, and 2px so rounding at 125% or 150% scaling never adds a scrollbar
    const h = Math.ceil($('#p-head').getBoundingClientRect().height + $('#p-inner').getBoundingClientRect().height) + 12 + 2 + 2;
    if (Math.abs(h - measured) >= 1) { measured = h; send({ type: 'content', h }); }
  });
}
new ResizeObserver(measureSoon).observe($('#p-inner'));

/* ---------- the two shapes: panel and pill ---------- */
const panel = $('#panel'), island = $('#island');
let view = 'none', edge = 'top';
function setEdge(e) {
  edge = e;
  document.body.dataset.edge = e;
  $('#btn-tuck').innerHTML = TUCK[e] ?? TUCK.top;
  if (view === 'pill') sendPillSize();
}

// The host asks for a shape: clear the current one first, so the window can change size
// without it showing squeezed or stretched for a moment.
let shapeTimer;
function onShape(msg) {
  setEdge(msg.edge);
  clearTimeout(shapeTimer);
  const ready = () => requestAnimationFrame(() => requestAnimationFrame(() => {
    if (msg.shape === 'pill') sendPillSize();
    send({ type: 'shapeReady' });
  }));
  if (view === 'panel' && msg.shape === 'pill' && !reduce) {
    // Slide out toward the edge, then go.
    panel.classList.remove('in'); panel.classList.add('out');
    view = 'none';
    shapeTimer = setTimeout(() => { panel.hidden = true; panel.classList.remove('out'); ready(); }, 140);
  } else {
    view = 'none';
    panel.hidden = true; island.hidden = true;
    ready();
  }
}

function onPlaced(msg) {
  if (msg.shape === 'panel') {
    if (view === 'panel') return;
    view = 'panel';
    island.hidden = true; island.classList.remove('live');
    panel.hidden = false;
    panel.classList.remove('in', 'out'); void panel.offsetWidth; panel.classList.add('in');
    islQueue.length = 0; islNow = null; clearTimeout(liveTimer);
    measured = 0; measureSoon();
  } else {
    if (view === 'pill') return;
    view = 'pill';
    panel.hidden = true;
    $('#look').hidden = true; $('#btn-look').setAttribute('aria-expanded', false);
    island.hidden = false;
    island.classList.remove('pop'); void island.offsetWidth; island.classList.add('pop');
  }
}

/* ---------- the pill ---------- */
const islLive = $('#isl-live'), islVal = $('#isl-val'), islArt = $('#isl-art');
const islBars = [...island.querySelectorAll('.isl-bars i')];

// Its size: compact (a bit longer with the cover of what's playing), or grown for a message.
let pillSize = '';
function sendPillSize() {
  const t = npTrack(), art = !!(t && t.playing);
  const long = art ? 152 : 124;
  const [w, h, r] = island.classList.contains('live') ? [318, 58, 22] : edge === 'top' ? [long, 32, 16] : [32, long, 16];
  const key = `${w}x${h}`;
  if (key === pillSize && view === 'pill') return;
  pillSize = key;
  send({ type: 'pill', w, h, r });
}

function drawPill(now) {
  const level = model.masterMuted ? 0 : (reduce ? 0.5 : meterState.get('master')?.level ?? 0);
  const vertical = edge !== 'top';
  islBars.forEach((b, i) => {
    const s = Math.max(0.15, level * (0.4 + 0.6 * Math.abs(Math.sin(now / 240 + i * 1.3))));
    b.style.transform = vertical ? `scaleX(${s})` : `scaleY(${s})`;
  });
  island.querySelectorAll('.isl-eq i').forEach((b, i) => { b.style.transform = `scaleY(${0.25 + 0.75 * Math.abs(Math.sin(now / 190 + i * 1.7))})`; });
  island.classList.toggle('muted', model.masterMuted);
  const v = String(model.masterMuted ? '–' : volOf('master'));
  if (islVal.textContent !== v) islVal.textContent = v;
  const t = npTrack(), art = !!(t && t.playing);
  if (islArt.hidden === art) {
    islArt.hidden = !art;
    if (art) sendPillSize(); else if (!island.classList.contains('live')) sendPillSize();
  }
  if (art && islArt.dataset.key !== t.id + t.title) { islArt.innerHTML = t.art ? `<img src="${t.art}" alt="">` : ''; artColors(islArt, t); islArt.dataset.key = t.id + t.title; }
  island.setAttribute('aria-label', `Volume ${model.masterMuted ? 'muted' : volOf('master') + '%'}${mic.users.length ? ', microphone in use' : ''}. Show the volume mixer`);
}

// Hovering the pill opens the mixer (the host watches the mouse); clicking opens it too.
island.addEventListener('click', () => send({ type: 'peek', click: true }));
island.addEventListener('wheel', (e) => {
  e.preventDefault();
  setVolume('master', volOf('master') + (e.deltaY < 0 ? 2 : -2));
}, { passive: false });
island.addEventListener('keydown', (e) => {
  const step = { ArrowUp: 2, ArrowRight: 2, ArrowDown: -2, ArrowLeft: -2 }[e.key];
  if (step) { e.preventDefault(); setVolume('master', volOf('master') + step); }
});

/* ---------- the pill's news ----------
   While tucked away, the pill grows for a moment to show what changed. One message at a
   time: a new one waits until the current one is done, except volume changes, which
   update in place while you keep pressing the key. */
const islQueue = [];
let islNow = null, liveTimer;
const sameGroup = (a, b) => a === b || (['volume', 'mute'].includes(a) && ['volume', 'mute'].includes(b));
function notifyIsland(kind, arg) {
  if (view !== 'pill' || !prefs.activity) return;
  const n = { kind, arg };
  if (islNow && !sameGroup(islNow.kind, kind)) {
    const i = islQueue.findIndex((q) => sameGroup(q.kind, kind));
    if (i >= 0) islQueue[i] = n; else islQueue.push(n);
    return;
  }
  showLive(n);
}
function showLive(n) {
  islNow = n;
  const { kind, arg } = n;
  const muted = model.masterMuted, v = volOf('master');
  const EQ = '<span class="isl-eq"><i></i><i></i><i></i><i></i></span>';
  let iconHTML, iconCls = '', title, sub, right = EQ, style = '';
  if (kind === 'volume' || kind === 'mute') {
    const value = muted ? 0 : v;
    iconHTML = speaker(v, muted); iconCls = muted ? ' muted' : '';
    title = muted ? 'Muted' : 'Volume'; sub = deviceShort(model.deviceName);
    right = `<span class="isl-bar"><i style="width:${value}%"></i></span><span class="isl-num">${value}</span>`;
  } else if (kind === 'play') {
    const a = appById(arg);
    if (!a) return next();
    iconHTML = appIcon(a); title = a.name; sub = 'Started playing';
  } else if (kind === 'device') {
    iconHTML = deviceIcon(model.deviceName); title = deviceShort(model.deviceName); sub = 'Now playing through';
  } else if (kind === 'track') {
    const t = npTrack();
    if (!t) return next();
    const h = hue(t.id);
    style = ` style="--a1:hsl(${h} 70% 62%);--a2:hsl(${(h + 70) % 360} 60% 42%)"`;
    iconHTML = artHTML(t); iconCls = ' art';
    title = t.title; sub = t.playing ? (t.artist || trackName(t)) : `Paused · ${trackName(t)}`;
    if (!t.playing) right = '';
  } else if (kind === 'mic') {
    iconHTML = I.mic; iconCls = ' mic';
    title = mic.users.join(', '); sub = 'is using your microphone';
    right = '<span class="isl-dot"></span>';
  } else if (kind === 'micmute') {
    iconHTML = mic.muted ? I.micOff : I.mic; iconCls = mic.muted ? ' muted' : '';
    title = mic.muted ? 'Microphone muted' : 'Microphone on';
    sub = mic.users.length ? `${mic.users.join(', ')} ${mic.muted ? "can't hear you" : 'can hear you'}` : deviceShort(mic.name);
    right = '';
  } else if (kind === 'profile') {
    iconHTML = I.auto;
    title = `${PROFILES[arg]} profile on`; sub = ruleFor(arg).on; right = '';
  } else if (kind === 'profile-off') {
    iconHTML = I.auto; iconCls = ' muted';
    title = arg.to ? `Back to ${PROFILES[arg.to]}` : 'Back to your mix';
    sub = ruleFor(arg.from).off; right = '';
  }
  islLive.innerHTML = `
    <span class="isl-icon${iconCls}"${style}>${iconHTML}</span>
    <span class="isl-text"><b>${escapeHtml(title)}</b><small>${escapeHtml(sub)}</small></span>
    ${right}`;
  island.classList.add('live');
  sendPillSize();
  clearTimeout(liveTimer);
  liveTimer = setTimeout(next, 2600);

  function next() {
    islNow = null;
    island.classList.remove('live');
    sendPillSize();
    const n2 = islQueue.shift();
    if (n2) liveTimer = setTimeout(() => view === 'pill' && showLive(n2), 380);
  }
}

/* ---------- messages from the host ---------- */
host.addEventListener('message', (e) => {
  const msg = e.data;
  switch (msg.type) {
    case 'state': onState(msg); break;
    case 'levels': onLevels(msg); break;
    case 'volume': {
      const changedMute = msg.masterMuted !== model.masterMuted;
      if (Math.round(msg.masterVolume * 100) === volOf('master') && !changedMute) break;
      model.masterVolume = msg.masterVolume; model.masterMuted = msg.masterMuted;
      if (performance.now() - (touchedAt.get('master') ?? -1e9) > 800 || view === 'pill') drawRow('master');
      notifyIsland(changedMute ? 'mute' : 'volume');
      break;
    }
    case 'mic': onMic(msg.mic); break;
    case 'media': onMedia(msg); break;
    case 'game': cond.game = msg.on; evaluateAuto(); break;
    case 'shape': onShape(msg); break;
    case 'placed': onPlaced(msg); break;
    case 'edge': setEdge(msg.edge); break;
    case 'backdrop': backdropLight = msg.light; applyLook(); break;
  }
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
  if (t.id === 'opt-tuck') { prefs.tuck = t.checked; savePrefs(); send({ type: 'autoTuck', on: prefs.tuck }); }
  else if (t.id === 'opt-activity') { prefs.activity = t.checked; savePrefs(); }
  else if (t.id.startsWith('auto-')) { prefs.auto[t.id.slice(5)] = t.checked; savePrefs(); evaluateAuto(); }
  else if (t.id === 'dev') { send({ type: 'setDevice', id: t.value }); t.blur(); }
  else if (t.dataset.out) {
    send({ type: 'setOutput', id: t.dataset.out, deviceId: t.value === 'default' ? null : t.value });
    t.blur();
    toast(`${appById(t.dataset.out)?.name ?? 'The app'} switches when it next starts a sound. Pause and play if it doesn't move.`);
  }
});

// A drop-down list opens in a window of its own: tell the host, so it doesn't tuck away under it.
document.addEventListener('focusin', (e) => { if (e.target.matches('select')) send({ type: 'busy', on: true }); });
document.addEventListener('focusout', (e) => { if (e.target.matches('select')) send({ type: 'busy', on: false }); });

document.addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b || b === island) return;
  if (b.dataset.mute) toggleMute(b.dataset.mute);
  else if (b.dataset.more) toggleExtra(b.dataset.more, b.getAttribute('aria-expanded') !== 'true');
  else if (b.dataset.profile) (prefs.profiles[b.dataset.profile] ? pickProfile : saveProfile)(b.dataset.profile);
  else if (b.id === 'mode-list') { setMode('list'); savePrefs(); }
  else if (b.id === 'mode-board') { setMode('board'); savePrefs(); }
  else if (b.id === 'btn-look') { const open = b.getAttribute('aria-expanded') !== 'true'; b.setAttribute('aria-expanded', open); $('#look').hidden = !open; }
  else if (b.id === 'btn-tuck') send({ type: 'tuck' });
  else if (b.id === 'np-play' || b.id === 'np-next' || b.id === 'np-prev') {
    const t = npTrack();
    if (t) send({ type: 'media', id: t.id, action: { 'np-play': 'toggle', 'np-next': 'next', 'np-prev': 'prev' }[b.id] });
  }
  else if (b.id === 'np-src') {
    const i = tracks.findIndex((t) => t.id === npTrack()?.id);
    npPick = tracks[(i + 1) % tracks.length]?.id ?? null;
    drawNP();
  }
  else if (b.id === 'mic-mute') send({ type: 'setMicMute', muted: !mic.muted });
});
document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && view === 'panel') send({ type: 'tuck' }); });

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

// Scroll the wheel over any app (row or strip) to nudge its volume.
document.addEventListener('wheel', (e) => {
  if (view !== 'panel') return;
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

// Moving is done by Windows itself; the page only asks for it. Where you let go, the host
// snaps the mixer to the nearest edge.
$('#p-head').addEventListener('mousedown', (e) => {
  if (e.button !== 0 || e.target.closest('button')) return;
  send({ type: 'drag' });
});

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
  const media = { type: 'media', current: 'Spotify.exe', tracks: [
    { id: 'Spotify.exe', title: 'Liwanag sa Dilim', artist: 'Kundiman Sessions', art: null, playing: true, position: 48, duration: 214, canPrev: true, canNext: true },
  ] };
  let mic = { available: true, name: 'Microphone (Realtek(R) Audio)', muted: false, users: [] };
  let shape = 'panel';
  const emit = (data) => setTimeout(() => listeners.forEach((fn) => fn({ data: structuredClone(data) })), 0);
  const emitState = () => emit({ type: 'state', ...fake });
  const fakeApi = {
    addEventListener: (_type, fn) => listeners.push(fn),
    postMessage(msg) {
      const app = fake.apps.find((a) => a.id === msg.id);
      switch (msg.type) {
        case 'ready':
          emitState(); emit(media); emit({ type: 'mic', mic });
          emit({ type: 'shape', shape, edge: 'top' });
          setInterval(() => emit({
            type: 'levels',
            master: fake.masterMuted ? 0 : Math.random() * 0.4,
            apps: Object.fromEntries(fake.apps.map((a) => [a.id, a.muted || !a.active ? 0 : Math.random() * a.volume * 0.5])),
            mic: mic.users.length ? Math.random() * 0.3 : 0,
          }), 33);
          break;
        case 'setVolume':
          if (msg.id === 'master') { fake.masterVolume = msg.value; emit({ type: 'volume', masterVolume: msg.value, masterMuted: fake.masterMuted }); }
          else if (app) app.volume = msg.value;
          emitState(); break;
        case 'setMute':
          if (msg.id === 'master') { fake.masterMuted = msg.muted; emit({ type: 'volume', masterVolume: fake.masterVolume, masterMuted: msg.muted }); }
          else if (app) app.muted = msg.muted;
          emitState(); break;
        case 'setDevice': { const d = fake.devices.find((x) => x.id === msg.id); if (d) { fake.deviceId = d.id; fake.deviceName = d.name; } emitState(); break; }
        case 'setMicMute': mic = { ...mic, muted: msg.muted }; emit({ type: 'mic', mic }); break;
        case 'media': if (msg.action === 'toggle') media.tracks[0].playing = !media.tracks[0].playing; emit(media); break;
        case 'tuck': shape = 'pill'; emit({ type: 'shape', shape, edge: 'top' }); break;
        case 'peek': shape = 'panel'; emit({ type: 'shape', shape, edge: 'top' }); break;
        case 'shapeReady': emit({ type: 'placed', shape }); break;
      }
    },
    // For trying things out from the browser console: window.fakeHost.call(true) and so on.
    call(on) { mic = { ...mic, users: on ? ['Discord'] : [] }; emit({ type: 'mic', mic }); },
    game(on) { emit({ type: 'game', on }); },
    key(step) { fake.masterVolume = clamp(fake.masterVolume + step / 100, 0, 1); emit({ type: 'volume', masterVolume: fake.masterVolume, masterMuted: fake.masterMuted }); },
    next() { const t = media.tracks[0]; t.title = t.title === 'Hatinggabi' ? 'Sarangani Blue' : 'Hatinggabi'; t.position = 0; emit(media); },
  };
  window.fakeHost = fakeApi;
  return fakeApi;
}

/* ---------- start ---------- */
cond.work = isWorkHours();
applyLook();
setMode(prefs.mode);
drawChips();
setEdge('top');
send({ type: 'blur', radius: prefs.blur });
send({ type: 'autoTuck', on: prefs.tuck });
requestAnimationFrame(animateMeters);
send({ type: 'ready' });
