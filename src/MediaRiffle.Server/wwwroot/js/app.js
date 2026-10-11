import { api, refreshCsrf } from './api.js';
import { h, mount, art, formatTime } from './dom.js';
import { Player } from './player.js';

const view = document.getElementById('view');
const nav = document.getElementById('nav');
let state = { setupNeeded: false, user: null };
let player = null;

async function loadState() {
  state = await api('/api/auth/state');
  await refreshCsrf();   // the token belongs to the signed-in user, so it changes with sign-in
  renderNav();
}

function renderNav() {
  if (!state.user) { mount(nav); return; }
  mount(nav,
    h('a', { href: '#/' }, 'Home'),
    h('span', { class: 'spacer' }),
    state.user.isAdmin ? h('a', { href: '#/admin' }, 'Admin') : null,
    h('span', { class: 'muted' }, state.user.name),
    h('a', { href: '#/logout' }, 'Sign out'));
}

function errorBox(e) { return h('p', { class: 'error' }, e?.message ?? String(e)); }

// ── Routes ───────────────────────────────────────────────────────────────────

const routes = [
  [/^#\/login$/, loginView],
  [/^#\/setup$/, setupView],
  [/^#\/join\/([\w-]+)$/, joinView],
  [/^#\/logout$/, logout],
  [/^#\/library\/(.+)$/, m => libraryView(decodeURIComponent(m[1]))],
  [/^#\/show\/(\d+)$/, m => showView(+m[1])],
  [/^#\/movie\/(\d+)$/, m => movieView(+m[1])],
  [/^#\/play\/(\d+)$/, m => playView(+m[1])],
  [/^#\/admin$/, adminView],
  [/^#?\/?$/, homeView],
];

async function route() {
  if (player) { player.destroy(); player = null; }
  const hash = location.hash || '#/';
  const open = /^#\/(login|setup|join\/)/.test(hash);
  if (state.setupNeeded && !hash.startsWith('#/setup')) { location.hash = '#/setup'; return; }
  if (!state.user && !open) { location.hash = '#/login'; return; }
  for (const [re, fn] of routes) {
    const m = hash.match(re);
    if (m) {
      try { await fn(m); } catch (e) { mount(view, errorBox(e)); }
      return;
    }
  }
  mount(view, h('p', {}, 'Page not found. ', h('a', { href: '#/' }, 'Home')));
}

// ── Accounts ─────────────────────────────────────────────────────────────────

function credentialsForm({ title, intro, button, onSubmit, remember = false }) {
  const user = h('input', { autocomplete: 'username', required: true, maxlength: 64 });
  const pass = h('input', { type: 'password', autocomplete: remember ? 'current-password' : 'new-password', required: true, minlength: remember ? 1 : 10 });
  const keep = h('input', { type: 'checkbox', checked: true });
  const msg = h('p', { class: 'error' });
  const form = h('form', { class: 'form card', onsubmit: async e => {
      e.preventDefault();
      msg.textContent = '';
      try { await onSubmit(user.value, pass.value, keep.checked); await loadState(); location.hash = '#/'; }
      catch (err) { msg.textContent = err.message; }
    } },
    h('h1', {}, title),
    intro ? h('p', { class: 'muted' }, intro) : null,
    h('label', {}, 'User name', user),
    h('label', {}, 'Password', pass),
    remember ? h('label', { class: 'row' }, keep, 'Keep me signed in') : h('p', { class: 'muted' }, 'At least 10 characters.'),
    h('button', { type: 'submit' }, button),
    msg);
  mount(view, form);
  user.focus();
}

function loginView() {
  credentialsForm({ title: 'Sign in', button: 'Sign in', remember: true,
    onSubmit: (u, p, keep) => api('/api/auth/login', { method: 'POST', body: { userName: u, password: p, remember: keep } }) });
}

function setupView() {
  if (!state.setupNeeded) { location.hash = '#/'; return; }
  credentialsForm({ title: 'Welcome to MediaRiffle', intro: 'Create the admin account. Others join with invite links you make under Admin.',
    button: 'Create admin account',
    onSubmit: (u, p) => api('/api/auth/setup', { method: 'POST', body: { userName: u, password: p } }) });
}

function joinView(m) {
  credentialsForm({ title: 'Join MediaRiffle', intro: 'Choose a user name and password for your account.', button: 'Create account',
    onSubmit: (u, p) => api('/api/auth/register', { method: 'POST', body: { invite: m[1], userName: u, password: p } }) });
}

async function logout() {
  await api('/api/auth/logout', { method: 'POST' });
  await loadState();
  location.hash = '#/login';
}

// ── Browsing ─────────────────────────────────────────────────────────────────

async function homeView() {
  const [libraries, recent] = await Promise.all([api('/api/libraries'), api('/api/continue')]);
  mount(view,
    recent.length ? [h('h2', {}, 'Continue watching'), h('div', { class: 'grid' }, recent.map(r =>
      h('a', { class: 'tile', href: `#/play/${r.file.id}` },
        art(r.file.image, r.file.duration ? r.file.position / r.file.duration : 0),
        h('div', { class: 'name' }, r.show ?? r.file.title),
        h('div', { class: 'sub' }, r.show ? r.file.title : `${formatTime(r.file.position)} of ${formatTime(r.file.duration)}`))))] : null,
    h('h2', {}, 'Libraries'),
    libraries.length
      ? h('div', { class: 'grid' }, libraries.map(l =>
          h('a', { class: 'tile card', href: `#/library/${encodeURIComponent(l.name)}` },
            h('div', { class: 'name' }, l.name), h('div', { class: 'sub' }, `${l.kind} · ${l.files} files`))))
      : h('p', { class: 'muted' }, 'No libraries are set up yet; add them to appsettings.json.'));
}

async function libraryView(name) {
  const libraries = await api('/api/libraries');
  const lib = libraries.find(l => l.name === name);
  if (!lib) throw new Error('No such library.');
  const items = await api(`/api/libraries/${encodeURIComponent(name)}/${lib.kind === 'Shows' ? 'shows' : 'movies'}`);
  const filter = h('input', { type: 'search', placeholder: 'Filter…', oninput: () => draw() });
  const grid = h('div', { class: 'grid' });
  const draw = () => {
    const q = filter.value.trim().toLowerCase();
    mount(grid, items.filter(i => !q || i.title.toLowerCase().includes(q)).map(i =>
      h('a', { class: 'tile', href: lib.kind === 'Shows' ? `#/show/${i.id}` : `#/movie/${i.id}` },
        art(i.image),
        h('div', { class: 'name' }, i.title),
        h('div', { class: 'sub' }, lib.kind === 'Shows' ? `${i.episodes} episodes` : [i.year, i.parts > 1 ? `${i.parts} parts` : null].filter(Boolean).join(' · ')))));
  };
  draw();
  mount(view, h('div', { class: 'row' }, h('h1', {}, name), filter), grid,
    items.length ? null : h('p', { class: 'muted' }, 'Nothing here yet. An admin can rescan under Admin.'));
}

function episodeRow(e) {
  const done = e.finished;
  return h('a', { class: 'episode' + (done ? ' watched' : ''), href: `#/play/${e.id}` },
    art(e.image, e.duration && !done ? e.position / e.duration : 0),
    h('div', {},
      h('div', { class: 'name' }, (e.episodeNumber ? `${e.episodeNumber}. ` : '') + e.title),
      h('div', { class: 'sub muted' }, formatTime(e.duration) + (e.position > 30 && !done ? ` · stopped at ${formatTime(e.position)}` : ''),
        e.playable ? null : ' · can\'t be read')),
    h('span', { class: 'play' }, '▶'));
}

async function showView(id) {
  const show = await api(`/api/shows/${id}`);
  const list = h('div', { class: 'episodes' });
  const tabs = h('div', { class: 'tabs' });
  const pick = season => {
    for (const b of tabs.children) b.classList.toggle('active', b.dataset.id === String(season.id));
    mount(list, season.episodes.map(episodeRow));
  };
  mount(tabs, show.seasons.map(s => h('button', { 'data-id': s.id, onclick: () => pick(s) }, s.title)));
  mount(view, h('h1', {}, show.title), tabs, list);
  // Open on the first season with something unwatched
  const first = show.seasons.find(s => s.episodes.some(e => !e.finished)) ?? show.seasons[0];
  if (first) pick(first);
}

async function movieView(id) {
  const movie = await api(`/api/movies/${id}`);
  if (movie.parts.length === 1) { location.replace(`#/play/${movie.parts[0].id}`); return; }
  mount(view, h('h1', {}, movie.title + (movie.year ? ` (${movie.year})` : '')),
    h('div', { class: 'episodes' }, movie.parts.map(p => episodeRow({ ...p, title: `Part ${p.partNumber}`, episodeNumber: null }))));
}

async function playView(fileId) {
  const details = await api(`/api/files/${fileId}`);
  const p = new Player(details);
  player = p;
  mount(view, h('h1', {}, details.summary.title), p.element(),
    h('p', { class: 'muted' }, [details.videoCodec, details.width ? `${details.width}×${details.height}` : null, details.container].filter(Boolean).join(' · ')));
  try { await p.start(); }
  catch (e) { p.status.textContent = e.message; }
}

// ── Admin ────────────────────────────────────────────────────────────────────

async function adminView() {
  if (!state.user?.isAdmin) { location.hash = '#/'; return; }
  const [users, invites, scan, sessions, qualities] = await Promise.all([
    api('/api/admin/users'), api('/api/admin/invites'), api('/api/admin/scan'), api('/api/admin/sessions'), api('/api/admin/qualities')]);

  const link = h('input', { readonly: true, size: 60 });
  const asAdmin = h('input', { type: 'checkbox' });
  const days = h('input', { type: 'number', min: 1, max: 30, value: 7, size: 3 });
  const makeInvite = async () => {
    const r = await api('/api/admin/invites', { method: 'POST', body: { isAdmin: asAdmin.checked, days: +days.value } });
    link.value = r.link;
    link.select();
  };

  const scanMsg = h('span', { class: 'muted' }, scan.isScanning ? 'Scanning…' : `${scan.lastResult}${scan.lastScanUtc ? ' (' + new Date(scan.lastScanUtc).toLocaleString() + ')' : ''}`);

  mount(view,
    h('h1', {}, 'Admin'),
    h('h2', {}, 'Invite someone'),
    h('div', { class: 'card' },
      h('div', { class: 'row' }, h('label', { class: 'row' }, asAdmin, 'Admin'), h('label', { class: 'row' }, 'Valid for', days, 'days'),
        h('button', { onclick: makeInvite }, 'Make invite link')),
      h('p', {}, link),
      h('p', { class: 'muted' }, 'The link works once. Only this page shows it; it isn\'t stored.')),
    h('h2', {}, 'Users'),
    h('table', {}, h('tr', {}, h('th', {}, 'Name'), h('th', {}, 'Role'), h('th', { title: 'The best quality this user may stream' }, 'Best quality'), h('th', {}, 'Joined'), h('th', {})),
      users.map(u => h('tr', {},
        h('td', {}, u.userName, u.lockedOut ? h('span', { class: 'error' }, ' (locked out)') : null),
        h('td', {}, u.isAdmin ? 'Admin' : 'Viewer'),
        h('td', {}, h('select', { onchange: async e => {
            await api(`/api/admin/users/${u.id}/quality`, { method: 'POST', body: { maxQuality: e.target.value || null } });
          } },
          h('option', { value: '', selected: !u.maxQuality }, 'No limit'),
          qualities.map(q => h('option', { value: q.id, selected: u.maxQuality === q.id }, q.label)))),
        h('td', {}, new Date(u.createdUtc).toLocaleDateString()),
        h('td', { class: 'row' },
          u.lockedOut ? h('button', { class: 'secondary', onclick: async () => { await api(`/api/admin/users/${u.id}/unlock`, { method: 'POST' }); adminView(); } }, 'Unlock') : null,
          u.userName !== state.user.name ? h('button', { class: 'danger', onclick: async () => {
            if (confirm(`Remove ${u.userName}?`)) { await api(`/api/admin/users/${u.id}`, { method: 'DELETE' }); adminView(); }
          } }, 'Remove') : null)))),
    h('h2', {}, 'Invites'),
    invites.length ? h('table', {}, h('tr', {}, h('th', {}, 'Role'), h('th', {}, 'Expires'), h('th', {}, 'Used'), h('th', {})),
      invites.map(i => h('tr', {},
        h('td', {}, i.isAdmin ? 'Admin' : 'Viewer'),
        h('td', {}, new Date(i.expiresUtc).toLocaleString()),
        h('td', {}, i.usedUtc ? new Date(i.usedUtc).toLocaleString() : '—'),
        h('td', {}, i.usedUtc ? null : h('button', { class: 'secondary', onclick: async () => { await api(`/api/admin/invites/${i.id}`, { method: 'DELETE' }); adminView(); } }, 'Revoke')))))
      : h('p', { class: 'muted' }, 'None yet.'),
    h('h2', {}, 'Library'),
    h('div', { class: 'row' }, h('button', { onclick: async () => { await api('/api/admin/scan', { method: 'POST' }); scanMsg.textContent = 'Scan requested…'; } }, 'Scan now'), scanMsg),
    h('h2', {}, 'Playing now'),
    sessions.length ? h('table', {}, h('tr', {}, h('th', {}, 'User'), h('th', {}, 'File'), h('th', {}, 'Mode'), h('th', {}, 'Started')),
      sessions.map(s => h('tr', {}, h('td', {}, s.user), h('td', {}, h('a', { href: `#/play/${s.fileId}` }, `#${s.fileId}`)),
        h('td', { title: s.reason }, s.mode), h('td', {}, new Date(s.createdUtc).toLocaleTimeString()))))
      : h('p', { class: 'muted' }, 'Nothing is playing.'));
}

// ── Start ────────────────────────────────────────────────────────────────────

addEventListener('hashchange', route);
loadState().then(route).catch(e => mount(view, errorBox(e)));
