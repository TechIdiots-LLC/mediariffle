import { api, refreshCsrf } from './api.js';
import { h, mount, art, formatTime } from './dom.js';
import { Player } from './player.js';
import { startQueue, positionOf, clearQueue } from './queue.js';

const view = document.getElementById('view');
const nav = document.getElementById('nav');
let state = { setupNeeded: false, user: null };
let player = null;
let pageTimer = null;   // a page's auto-refresh, stopped when leaving it

async function loadState() {
  state = await api('/api/auth/state');
  await refreshCsrf();   // the token belongs to the signed-in user, so it changes with sign-in
  renderNav();
  document.getElementById('foot').textContent = `MediaRiffle ${state.version ?? ''}`;
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
  [/^#\/library\/(\d+)$/, m => libraryView(+m[1])],
  [/^#\/tag\/(.+)$/, m => tagView(decodeURIComponent(m[1]))],
  [/^#\/show\/(\d+)$/, m => showView(+m[1])],
  [/^#\/movie\/(\d+)$/, m => movieView(+m[1])],
  [/^#\/play\/(\d+)$/, m => playView(+m[1])],
  [/^#\/admin$/, adminView],
  [/^#?\/?$/, homeView],
];

async function route() {
  if (player) { player.destroy(); player = null; }
  if (pageTimer) { clearInterval(pageTimer); pageTimer = null; }
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

function playButtons(what, label) {
  const go = shuffle => startQueue(what, shuffle, label).catch(e => alert(e.message));
  return h('span', { class: 'row' },
    h('button', { onclick: () => go(false) }, '▶ Play all'),
    h('button', { class: 'secondary', onclick: () => go(true) }, '🔀 Shuffle'));
}

function showTiles(shows) {
  return shows.map(i => h('a', { class: 'tile', href: `#/show/${i.id}` },
    art(i.image), h('div', { class: 'name' }, i.title), h('div', { class: 'sub' }, `${i.episodes} episodes`)));
}

function movieTiles(movies) {
  return movies.map(i => h('a', { class: 'tile', href: `#/movie/${i.id}` },
    art(i.image), h('div', { class: 'name' }, i.title),
    h('div', { class: 'sub' }, [i.year, i.parts > 1 ? `${i.parts} parts` : null].filter(Boolean).join(' · '))));
}

function filterBox(onChange) {
  return h('input', { type: 'search', placeholder: 'Filter…', oninput: e => onChange(e.target.value.trim().toLowerCase()) });
}

async function homeView() {
  const [libraries, recent, tags] = await Promise.all([api('/api/libraries'), api('/api/continue'), api('/api/tags')]);
  mount(view,
    recent.length ? [h('h2', {}, 'Continue watching'), h('div', { class: 'grid' }, recent.map(r =>
      h('a', { class: 'tile', href: `#/play/${r.file.id}` },
        art(r.file.image, r.file.duration ? r.file.position / r.file.duration : 0),
        h('div', { class: 'name' }, r.show ?? r.file.title),
        h('div', { class: 'sub' }, r.show ? r.file.title : `${formatTime(r.file.position)} of ${formatTime(r.file.duration)}`))))] : null,
    tags.length ? [h('h2', {}, 'Categories'), h('div', { class: 'chips' }, tags.map(t =>
      h('a', { class: 'chip', href: `#/tag/${encodeURIComponent(t.tag)}` }, t.tag, h('span', { class: 'muted' }, ` ${t.files}`))))] : null,
    h('h2', {}, 'Libraries'),
    libraries.length
      ? h('div', { class: 'grid' }, libraries.map(l =>
          h('a', { class: 'tile card', href: `#/library/${l.id}` },
            h('div', { class: 'name' }, l.name), h('div', { class: 'sub' }, `${l.kind} · ${l.files} files`),
            l.tags.length ? h('div', { class: 'sub' }, l.tags.join(', ')) : null)))
      : h('p', { class: 'muted' }, state.user.isAdmin ? 'No libraries yet: add them under Admin.' : 'No libraries yet.'));
}

async function libraryView(id) {
  const lib = await api(`/api/libraries/${id}`);
  const shows = lib.kind === 'Shows';
  const items = await api(`/api/libraries/${id}/${shows ? 'shows' : 'movies'}`);
  const grid = h('div', { class: 'grid' });
  const draw = q => mount(grid, (shows ? showTiles : movieTiles)(items.filter(i => !q || i.title.toLowerCase().includes(q))));
  draw('');
  mount(view,
    h('div', { class: 'row' }, h('h1', {}, lib.name), playButtons({ library: id }, lib.name), filterBox(draw)),
    lib.tags.length ? h('p', { class: 'muted' }, 'Categories: ', lib.tags.map((t, i) => [i ? ', ' : '', h('a', { href: `#/tag/${encodeURIComponent(t)}` }, t)])) : null,
    grid,
    items.length ? null : h('p', { class: 'muted' }, 'Nothing here yet. An admin can rescan under Admin.'));
}

// A category: every show and movie in the libraries carrying this tag, to browse or play through
async function tagView(tag) {
  const t = await api(`/api/tags/${encodeURIComponent(tag)}`);
  const showGrid = h('div', { class: 'grid' }), movieGrid = h('div', { class: 'grid' });
  const draw = q => {
    const match = i => !q || i.title.toLowerCase().includes(q);
    mount(showGrid, showTiles(t.shows.filter(match)));
    mount(movieGrid, movieTiles(t.movies.filter(match)));
  };
  draw('');
  mount(view,
    h('div', { class: 'row' }, h('h1', {}, t.tag), playButtons({ tag: t.tag }, t.tag), filterBox(draw)),
    t.shows.length ? [h('h2', {}, 'Shows'), showGrid] : null,
    t.movies.length ? [h('h2', {}, 'Movies'), movieGrid] : null);
}

function episodeRow(e) {
  const done = e.finished;
  return h('a', { class: 'episode' + (done ? ' watched' : ''), href: `#/play/${e.id}`, onclick: () => clearQueue() },
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
  mount(view, h('div', { class: 'row' }, h('h1', {}, show.title), playButtons({ show: id }, show.title)), tabs, list);
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
  const place = positionOf(fileId);
  let queueBar = null;
  if (place) {
    p.setNavigation({ previous: place.previous?.id, next: place.next?.id });
    const nextLink = place.next ? h('a', { href: `#/play/${place.next.id}` }, `Next: ${place.next.title} →`) : null;
    queueBar = h('div', { class: 'row queue' },
      h('strong', {}, place.label), h('span', { class: 'muted' }, `${place.index + 1} of ${place.count}`), nextLink,
      h('button', { class: 'secondary', onclick: () => { clearQueue(); queueBar.remove(); } }, 'Stop after this'));
    // At the end, on to the next one after a moment (Cancel stays on this one)
    p.onFinished = () => {
      if (!place.next || !document.body.contains(queueBar)) return false;
      let left = 5;
      const msg = h('span', {}, `Next in ${left}…`);
      const cancel = h('button', { class: 'secondary', onclick: () => { clearInterval(timer); msg.textContent = 'Stopped.'; cancel.remove(); } }, 'Cancel');
      queueBar.append(msg, cancel);
      const timer = setInterval(() => {
        if (--left > 0) { msg.textContent = `Next in ${left}…`; return; }
        clearInterval(timer);
        location.hash = `#/play/${place.next.id}`;
      }, 1000);
      return true;
    };
  }
  mount(view, h('h1', {}, details.summary.title), queueBar, p.element(),
    h('p', { class: 'muted' }, [details.videoCodec, details.width ? `${details.width}×${details.height}` : null, details.container].filter(Boolean).join(' · ')));
  try { await p.start(); }
  catch (e) { p.status.textContent = e.message; }
}

// ── Admin ────────────────────────────────────────────────────────────────────

// Add or edit a library, in place of the "Add a library" button
function libraryForm(lib) {
  const name = h('input', { value: lib?.name ?? '', maxlength: 100, required: true });
  const kind = h('select', {}, ['Shows', 'Movies'].map(k => h('option', { value: k, selected: (lib?.kind ?? 'Shows') === k }, k)));
  const path = h('input', { value: lib?.path ?? '', size: 50, placeholder: '/mnt/media/Shows or \\\\files.example.lan\\Media\\Shows', required: true });
  const tags = h('input', { value: lib?.tags ?? '', size: 40, placeholder: 'e.g. Cartoons, Kids' });
  const enabled = h('input', { type: 'checkbox', checked: lib?.enabled ?? true });
  const msg = h('p', { class: 'muted' });
  const check = async () => {
    msg.className = 'muted'; msg.textContent = 'Checking…';
    const r = await api('/api/admin/libraries/check', { method: 'POST', body: { path: path.value } });
    msg.className = r.ok ? 'ok' : 'error';
    mount(msg, r.message, r.sample?.length ? h('br') : null, (r.sample ?? []).map(s => [h('br'), h('code', {}, s)]));
  };
  const save = async e => {
    e.preventDefault();
    try {
      const body = { name: name.value, kind: kind.value, path: path.value, tags: tags.value, enabled: enabled.checked };
      if (lib) await api(`/api/admin/libraries/${lib.id}`, { method: 'PUT', body });
      else await api('/api/admin/libraries', { method: 'POST', body });
      adminView();
    } catch (err) { msg.className = 'error'; msg.textContent = err.message; }
  };
  mount(document.getElementById('library-form'), h('form', { class: 'card form-wide', onsubmit: save },
    h('h2', {}, lib ? `Edit ${lib.name}` : 'Add a library'),
    h('label', {}, 'Name', name),
    h('label', {}, 'Kind', kind),
    h('label', {}, 'Folder', h('span', { class: 'row' }, path, h('button', { type: 'button', class: 'secondary', onclick: check }, 'Check folder'))),
    h('label', {}, 'Tags', tags),
    h('label', { class: 'row' }, enabled, 'Enabled'),
    lib && lib.path ? h('p', { class: 'muted' }, 'Changing the folder or kind rescans it from scratch.') : null,
    msg,
    h('div', { class: 'row' }, h('button', { type: 'submit' }, lib ? 'Save' : 'Add and scan'),
      h('button', { type: 'button', class: 'secondary', onclick: () => adminView() }, 'Cancel'))));
  name.focus();
}

async function adminView() {
  if (!state.user?.isAdmin) { location.hash = '#/'; return; }
  const [users, invites, scan, sessions, qualities, libraries] = await Promise.all([
    api('/api/admin/users'), api('/api/admin/invites'), api('/api/admin/scan'), api('/api/admin/sessions'), api('/api/admin/qualities'),
    api('/api/admin/libraries')]);

  const link = h('input', { readonly: true, size: 60 });
  const asAdmin = h('input', { type: 'checkbox' });
  const days = h('input', { type: 'number', min: 1, max: 30, value: 7, size: 3 });
  const makeInvite = async () => {
    const r = await api('/api/admin/invites', { method: 'POST', body: { isAdmin: asAdmin.checked, days: +days.value } });
    link.value = r.link;
    link.select();
  };

  const scanText = s => s.isScanning ? `Scanning: ${s.progress ?? '…'}`
    : `${s.lastResult}${s.lastScanUtc ? ' (' + new Date(s.lastScanUtc).toLocaleString() + ')' : ''}`;
  const scanMsg = h('span', { class: 'muted' }, scanText(scan));

  // File counts and scan status, refreshed every 3 s while this page is open (in place, so an open form isn't lost)
  const cells = new Map();
  const statusText = l => !l.enabled ? 'Disabled'
    : l.status ? `${{ ok: '✓', warning: '⚠', scanning: '…', unavailable: '✗', error: '✗' }[l.status.state] ?? ''} ${l.status.message}`
    : 'Waiting for the first scan';
  const fillRow = l => {
    const c = cells.get(l.id);
    if (!c) return;
    c.files.textContent = l.unreadable ? `${l.files} (${l.unreadable} unreadable)` : String(l.files);
    c.status.textContent = statusText(l);
    c.status.className = l.status?.state === 'unavailable' || l.status?.state === 'error' ? 'error' : l.status?.state === 'warning' ? '' : 'muted';
  };
  pageTimer = setInterval(async () => {
    try {
      const [s, libs] = await Promise.all([api('/api/admin/scan'), api('/api/admin/libraries')]);
      scanMsg.textContent = scanText(s);
      libs.forEach(fillRow);
    } catch { /* signed out or offline: the next tick tries again */ }
  }, 3000);

  mount(view,
    h('h1', {}, 'Admin'),
    h('h2', {}, 'Libraries'),
    h('p', { class: 'muted' }, 'Folders of shows or movies, on this server or a file share. Tags (comma-separated, as in Media Library Manager) are the categories everyone can browse and shuffle.'),
    libraries.length ? h('table', {},
      h('tr', {}, h('th', {}, 'Name'), h('th', {}, 'Kind'), h('th', {}, 'Folder'), h('th', {}, 'Tags'), h('th', {}, 'Files'), h('th', {}, 'Status'), h('th', {})),
      libraries.map(l => {
        const files = h('td', {}), status = h('td', {});
        cells.set(l.id, { files, status });
        fillRow(l);
        return [l, files, status];
      }).map(([l, files, status]) => h('tr', { class: l.enabled ? '' : 'muted' },
        h('td', {}, l.name, l.enabled ? null : ' (disabled)'), h('td', {}, l.kind), h('td', {}, h('code', {}, l.path)),
        h('td', {}, l.tags ?? ''), files, status,
        h('td', { class: 'row' },
          h('button', { class: 'secondary', onclick: () => libraryForm(l) }, 'Edit'),
          h('button', { class: 'danger', onclick: async () => {
            if (!confirm(`Remove ${l.name}? Its files stay where they are; only MediaRiffle's list of them (and everyone's place in them) goes.`)) return;
            await api(`/api/admin/libraries/${l.id}`, { method: 'DELETE' }); adminView();
          } }, 'Remove')))))
      : h('p', {}, 'No libraries yet.'),
    h('div', { class: 'row' }, h('button', { class: 'secondary', onclick: async () => { await api('/api/admin/scan', { method: 'POST' }); scanMsg.textContent = 'Scan requested…'; } }, 'Scan now'), scanMsg),
    h('div', { id: 'library-form' }, h('button', { onclick: () => libraryForm(null) }, 'Add a library')),
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
    h('h2', {}, 'Playing now'),
    sessions.length ? h('table', {}, h('tr', {}, h('th', {}, 'User'), h('th', {}, 'File'), h('th', {}, 'Mode'), h('th', {}, 'Started')),
      sessions.map(s => h('tr', {}, h('td', {}, s.user), h('td', {}, h('a', { href: `#/play/${s.fileId}` }, `#${s.fileId}`)),
        h('td', { title: s.reason }, s.mode), h('td', {}, new Date(s.createdUtc).toLocaleTimeString()))))
      : h('p', { class: 'muted' }, 'Nothing is playing.'));
}

// ── Start ────────────────────────────────────────────────────────────────────

addEventListener('hashchange', route);
loadState().then(route).catch(e => mount(view, errorBox(e)));
