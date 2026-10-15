import { api, refreshCsrf } from './api.js';
import { h, mount, art, formatTime } from './dom.js';
import { Player } from './player.js';
import { startQueue, positionOf, clearQueue } from './queue.js';
import { Listener, trackRows } from './music.js';

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
    h('details', { class: 'usermenu' },
      h('summary', {}, state.user.name),
      h('div', { class: 'menu' },
        h('a', { href: '#/profile' }, 'Profile'),
        h('a', { href: '#/logout' }, 'Sign out'))));
}

function closeUserMenu() { nav.querySelector('.usermenu')?.removeAttribute('open'); }
// A click anywhere outside the open menu closes it
document.addEventListener('click', e => { if (!e.target.closest?.('.usermenu')) closeUserMenu(); });

function errorBox(e) { return h('p', { class: 'error' }, e?.message ?? String(e)); }

// ── Routes ───────────────────────────────────────────────────────────────────

const routes = [
  [/^#\/login$/, loginView],
  [/^#\/setup$/, setupView],
  [/^#\/join\/([\w-]+)$/, joinView],
  [/^#\/logout$/, logout],
  [/^#\/library\/(\d+)$/, m => libraryView(+m[1])],
  [/^#\/tag\/(.+)$/, m => tagView(decodeURIComponent(m[1]))],
  [/^#\/list\/(.+)$/, m => listView(decodeURIComponent(m[1]))],
  [/^#\/show\/(\d+)$/, m => showView(+m[1])],
  [/^#\/movie\/(\d+)$/, m => movieView(+m[1])],
  [/^#\/artist\/(\d+)$/, m => artistView(+m[1])],
  [/^#\/album\/(\d+)$/, m => albumView(+m[1])],
  [/^#\/play\/(\d+)$/, m => playView(+m[1])],
  [/^#\/listen\/(\d+)$/, m => listenView(+m[1])],
  [/^#\/admin$/, adminView],
  [/^#\/profile$/, profileView],
  [/^#?\/?$/, homeView],
];

async function route() {
  closeUserMenu();
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

function credentialsForm({ title, intro, button, onSubmit, remember = false, extra = null }) {
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
    extra,
    h('label', {}, 'User name', user),
    h('label', {}, 'Password', pass),
    remember ? h('label', { class: 'row' }, keep, 'Keep me signed in') : h('p', { class: 'muted' }, 'At least 10 characters.'),
    h('button', { type: 'submit' }, button),
    msg);
  mount(view, form);
  user.focus();
}

function loginView() {
  if (state.user) { location.hash = '#/'; return; }
  // Google sign-in is a page navigation (to Google and back), not a fetch
  const google = state.google
    ? [h('a', { class: 'google-btn', href: '/api/auth/google/signin' }, 'Sign in with Google'), h('p', { class: 'muted or' }, 'or')]
    : null;
  credentialsForm({ title: 'Sign in', button: 'Sign in', remember: true, extra: [takeGoogleMessage(), google],
    onSubmit: (u, p, keep) => api('/api/auth/login', { method: 'POST', body: { userName: u, password: p, remember: keep } }) });
}

async function profileView() {
  const account = await api('/api/auth/account');
  const message = takeGoogleMessage();
  let google;
  if (!state.google) google = h('p', { class: 'muted' }, 'Google sign-in isn\'t set up on this server.');
  else if (account.google) google = [
    h('p', {}, 'Linked to ', h('strong', {}, account.google), '. You can sign in with Google or with your password.'),
    h('div', { class: 'row' },
      h('a', { class: 'google-btn', href: '/api/auth/google/link' }, 'Link a different Google account'),
      h('button', { class: 'secondary', onclick: async () => {
        if (!confirm('Unlink your Google account? You\'ll sign in with your user name and password only.')) return;
        try { await api('/api/auth/google/unlink', { method: 'POST' }); profileView(); }
        catch (e) { alert(e.message); }
      } }, 'Unlink'))];
  else google = [
    h('p', { class: 'muted' }, 'Link your Google account to sign in with it instead of your password.'),
    h('a', { class: 'google-btn', href: '/api/auth/google/link' }, 'Link Google account')];
  const hidden = h('div');
  mount(view,
    h('h1', {}, 'Profile'),
    h('div', { class: 'card' },
      h('p', {}, 'Signed in as ', h('strong', {}, account.name), state.user.isAdmin ? ' (admin)' : ''),
      h('h2', {}, 'Google'),
      message,
      google,
      h('h2', {}, 'Hidden'),
      hidden));
  hiddenSection(hidden);
}

// What the user hid, to open or unhide in one place
async function hiddenSection(box) {
  let items;
  try { items = await api('/api/hidden'); }
  catch (e) { mount(box, errorBox(e)); return; }
  const link = i => i.kind === 'show' ? `#/show/${i.id}` : i.kind === 'movie' ? `#/movie/${i.id}` : `#/play/${i.id}`;
  const label = { show: 'Show', movie: 'Movie', file: 'Episode' };
  mount(box,
    h('p', { class: 'muted' }, 'Hidden shows, movies and episodes stay listed, greyed out, but are left out of Play all, Shuffle and Continue watching.'),
    items.length
      ? h('table', {},
          h('tr', {}, h('th', {}, 'Type'), h('th', {}, 'Title'), h('th', {}, 'Hidden'), h('th', {})),
          items.map(i => h('tr', {},
            h('td', { class: 'muted' }, label[i.kind]),
            h('td', {}, h('a', { href: link(i) }, i.title), i.detail ? h('span', { class: 'muted' }, ` · ${i.detail}`) : null),
            h('td', { class: 'muted' }, new Date(i.createdUtc).toLocaleDateString()),
            h('td', {}, hideButton(i.kind, i.id, true, () => hiddenSection(box), label[i.kind].toLowerCase())))))
      : h('p', {}, 'Nothing hidden.'));
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

function playButtons(what, label, disabled = false) {
  const go = shuffle => startQueue(what, shuffle, label).catch(e => alert(e.message));
  return h('span', { class: 'row' },
    h('button', { onclick: () => go(false), disabled }, '▶ Play all'),
    h('button', { class: 'secondary', onclick: () => go(true), disabled }, '🔀 Shuffle'));
}

// Hidden shows and movies stay listed, greyed out, so they can be opened and unhidden
function showTiles(shows) {
  return shows.map(i => h('a', { class: 'tile' + (i.hidden ? ' hidden' : ''), href: `#/show/${i.id}` },
    art(i.image), h('div', { class: 'name' }, i.title),
    h('div', { class: 'sub' }, i.hidden ? 'Hidden' : `${i.episodes} episodes`)));
}

function movieTiles(movies) {
  return movies.map(i => h('a', { class: 'tile' + (i.hidden ? ' hidden' : ''), href: `#/movie/${i.id}` },
    art(i.image), h('div', { class: 'name' }, i.title),
    h('div', { class: 'sub' }, [i.hidden ? 'Hidden' : null, i.year, i.parts > 1 ? `${i.parts} parts` : null].filter(Boolean).join(' · '))));
}

// Hides a show, a movie or an episode from this user (it stays listed, greyed out), or shows it again
function hideButton(kind, id, hidden, onChange, noun) {
  return h('button', { class: 'secondary hide-btn', title: hidden ? `Show this ${noun} again` : `Leave this ${noun} out of Play all, Shuffle and Continue watching`,
    onclick: async e => {
      e.preventDefault(); e.stopPropagation();
      try { const r = await api(`/api/items/${kind}/${id}/hidden`, { method: 'PUT', body: { hidden: !hidden } }); onChange(r.hidden); }
      catch (err) { alert(err.message); }
    } }, hidden ? 'Unhide' : 'Hide');
}

const FAVORITES = 'Favorites';
const isFavorites = t => t.toLowerCase() === 'favorites' || t.toLowerCase() === 'favourites';

// A show's or movie's place in the user's own lists: a Favorite star, the lists it's in (× takes it out), and a box
// to add it to a list, existing or new. Only this user sees them.
function listsControl(kind, id, label = null) {
  const box = h('div', { class: 'row my-lists' });
  let tags = [], all = [];
  const msg = h('span', { class: 'error' });
  const save = async next => {
    msg.textContent = '';
    try { tags = await api(`/api/items/${kind}/${id}/lists`, { method: 'PUT', body: { tags: next } }); draw(); }
    catch (e) { msg.textContent = e.message; }
  };
  const input = h('input', { list: `my-lists-${kind}-${id}`, placeholder: 'Add to a list…', maxlength: 50, size: 18,
    onkeydown: e => { if (e.key === 'Enter') { e.preventDefault(); add(); } } });
  const add = () => {
    const t = input.value.trim();
    input.value = '';
    if (t && !tags.some(x => x.toLowerCase() === t.toLowerCase())) save([...tags, t]);
  };
  const draw = () => {
    const fav = tags.some(isFavorites);
    const others = tags.filter(t => !isFavorites(t));
    mount(box,
      label ? h('span', { class: 'muted' }, label) : null,
      h('button', { class: 'secondary fav' + (fav ? ' on' : ''), title: fav ? 'Take out of Favorites' : 'Add to Favorites',
        onclick: () => save(fav ? tags.filter(t => !isFavorites(t)) : [...tags, FAVORITES]) }, fav ? '★ Favorite' : '☆ Favorite'),
      others.map(t => h('span', { class: 'chip' },
        h('a', { href: `#/list/${encodeURIComponent(t)}` }, t),
        h('button', { class: 'chip-x', title: `Take out of ${t}`, onclick: () => save(tags.filter(x => x !== t)) }, '×'))),
      input,
      h('datalist', { id: `my-lists-${kind}-${id}` }, all.filter(t => !isFavorites(t) && !tags.includes(t)).map(t => h('option', { value: t }))),
      h('button', { class: 'secondary', onclick: add }, 'Add'),
      msg);
  };
  Promise.all([api(`/api/items/${kind}/${id}/lists`), api('/api/lists')])
    .then(([mine, lists]) => { tags = mine; all = lists.map(l => l.tag); draw(); })
    .catch(() => box.remove());
  return box;
}

function filterBox(onChange) {
  return h('input', { type: 'search', placeholder: 'Filter…', oninput: e => onChange(e.target.value.trim().toLowerCase()) });
}

async function homeView() {
  const [libraries, recent, tags, lists, favorites] = await Promise.all([api('/api/libraries'), api('/api/continue'), api('/api/tags'),
    api('/api/lists'), api(`/api/lists/${FAVORITES}`)]);
  const myLists = lists.filter(l => !isFavorites(l.tag));
  mount(view,
    recent.length ? [h('h2', {}, 'Continue watching'), h('div', { class: 'grid' }, recent.map(r =>
      h('a', { class: 'tile', href: `#/play/${r.file.id}` },
        art(r.file.image, r.file.duration ? r.file.position / r.file.duration : 0),
        h('div', { class: 'name' }, r.show ?? r.file.title),
        h('div', { class: 'sub' }, r.show ? r.file.title : `${formatTime(r.file.position)} of ${formatTime(r.file.duration)}`))))] : null,
    favorites.shows.length || favorites.movies.length
      ? [h('div', { class: 'row' }, h('h2', {}, '★ Favorites'), h('a', { href: `#/list/${FAVORITES}` }, 'Play or shuffle →')),
         h('div', { class: 'grid' }, showTiles(favorites.shows), movieTiles(favorites.movies))] : null,
    myLists.length ? [h('h2', {}, 'My lists'), h('div', { class: 'chips' }, myLists.map(l =>
      h('a', { class: 'chip', href: `#/list/${encodeURIComponent(l.tag)}` }, l.tag, h('span', { class: 'muted' }, ` ${l.items}`))))] : null,
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
  const kind = { Shows: 'shows', Movies: 'movies', Music: 'artists' }[lib.kind];
  const tiles = { Shows: showTiles, Movies: movieTiles, Music: artistTiles }[lib.kind];
  const items = await api(`/api/libraries/${id}/${kind}`);
  const name = i => i.title ?? i.name;
  const grid = h('div', { class: 'grid' });
  const draw = q => mount(grid, tiles(items.filter(i => !q || name(i).toLowerCase().includes(q))));
  draw('');
  mount(view,
    h('div', { class: 'row' }, h('h1', {}, lib.name), playButtons({ library: id }, lib.name), filterBox(draw)),
    lib.tags.length ? h('p', { class: 'muted' }, 'Categories: ', lib.tags.map((t, i) => [i ? ', ' : '', h('a', { href: `#/tag/${encodeURIComponent(t)}` }, t)])) : null,
    grid,
    items.length ? null : h('p', { class: 'muted' }, 'Nothing here yet. An admin can rescan under Admin.'));
}

// A category: every show, movie and artist in the libraries carrying this tag, to browse or play through
async function tagView(tag) {
  const t = await api(`/api/tags/${encodeURIComponent(tag)}`);
  const showGrid = h('div', { class: 'grid' }), movieGrid = h('div', { class: 'grid' }), artistGrid = h('div', { class: 'grid' });
  const draw = q => {
    const match = i => !q || (i.title ?? i.name).toLowerCase().includes(q);
    mount(showGrid, showTiles(t.shows.filter(match)));
    mount(movieGrid, movieTiles(t.movies.filter(match)));
    mount(artistGrid, artistTiles(t.artists.filter(match)));
  };
  draw('');
  mount(view,
    h('div', { class: 'row' }, h('h1', {}, t.tag), playButtons({ tag: t.tag }, t.tag), filterBox(draw)),
    t.shows.length ? [h('h2', {}, 'Shows'), showGrid] : null,
    t.movies.length ? [h('h2', {}, 'Movies'), movieGrid] : null,
    t.artists.length ? [h('h2', {}, 'Music'), artistGrid] : null);
}

// ── Music ────────────────────────────────────────────────────────────────────

function artistTiles(artists) {
  return artists.map(a => h('a', { class: 'tile', href: `#/artist/${a.id}` },
    art(a.image, 0, 'music'), h('div', { class: 'name' }, a.name), h('div', { class: 'sub' }, `${a.albums} album${a.albums === 1 ? '' : 's'}`)));
}

function albumTiles(albums, showArtist = false) {
  return albums.map(a => h('a', { class: 'tile', href: `#/album/${a.id}` },
    art(a.image, 0, 'music'), h('div', { class: 'name' }, a.title),
    h('div', { class: 'sub' }, [showArtist ? a.artist : null, a.year, `${a.tracks} track${a.tracks === 1 ? '' : 's'}`].filter(Boolean).join(' · '))));
}

async function artistView(id) {
  const a = await api(`/api/artists/${id}`);
  mount(view,
    h('div', { class: 'row' }, h('h1', {}, a.name), playButtons({ artist: id }, a.name)),
    h('div', { class: 'grid' }, albumTiles(a.albums)),
    a.appearsOn.length ? [h('h2', {}, 'Appears on'), h('div', { class: 'grid' }, albumTiles(a.appearsOn, true))] : null);
}

async function albumView(id) {
  const a = await api(`/api/albums/${id}`);
  // A track starts the album from there
  const pick = t => startQueue({ album: id }, false, a.title, t.id).catch(e => alert(e.message));
  mount(view,
    h('div', { class: 'album-head' },
      h('div', { class: 'listen-art' }, art(a.image, 0, 'music')),
      h('div', {},
        h('h1', {}, a.title),
        h('p', {}, h('a', { href: `#/artist/${a.artist.id}` }, a.artist.name), a.year ? ` · ${a.year}` : ''),
        h('p', { class: 'muted' }, `${a.tracks.length} track${a.tracks.length === 1 ? '' : 's'} · ${formatTime(a.duration)}`),
        playButtons({ album: id }, a.title))),
    h('div', { class: 'tracks' }, trackRows(a, pick)));
}

async function listenView(fileId) {
  let track;
  try { track = await api(`/api/tracks/${fileId}`); }
  catch (e) { if (e.status === 404) { location.replace(`#/play/${fileId}`); return; } throw e; }
  const l = new Listener(track, positionOf(fileId));
  player = l;
  mount(view, l.element());
  await l.start();
}

// One of the user's own lists (Favorites among them): the shows and movies they tagged with it
async function listView(tag) {
  const t = await api(`/api/lists/${encodeURIComponent(tag)}`);
  const showGrid = h('div', { class: 'grid' }), movieGrid = h('div', { class: 'grid' });
  const draw = q => {
    const match = i => !q || i.title.toLowerCase().includes(q);
    mount(showGrid, showTiles(t.shows.filter(match)));
    mount(movieGrid, movieTiles(t.movies.filter(match)));
  };
  draw('');
  const empty = !t.shows.length && !t.movies.length;
  mount(view,
    h('div', { class: 'row' }, h('h1', {}, t.favorites ? '★ Favorites' : t.tag), empty ? null : playButtons({ list: t.tag }, t.tag), empty ? null : filterBox(draw)),
    h('p', { class: 'muted' }, 'Only you see your lists. Add a show or movie to one from its page.'),
    t.shows.length ? [h('h2', {}, 'Shows'), showGrid] : null,
    t.movies.length ? [h('h2', {}, 'Movies'), movieGrid] : null,
    empty ? h('p', {}, 'Nothing in this list.') : null);
}

// An episode or movie part. A hidden one (on its own, or with its whole show or movie: `off`) is greyed out and
// can't be played from here; `onHide` adds the Hide/Unhide button for the episode itself.
function episodeRow(e, { off = false, onHide = null } = {}) {
  const done = e.finished;
  const disabled = off || e.hidden;
  const content = [
    art(e.image, e.duration && !done && !disabled ? e.position / e.duration : 0),
    h('div', {},
      h('div', { class: 'name' }, (e.episodeNumber ? `${e.episodeNumber}. ` : '') + e.title),
      h('div', { class: 'sub muted' }, disabled ? 'Hidden · ' : '', formatTime(e.duration) + (e.position > 30 && !done ? ` · stopped at ${formatTime(e.position)}` : ''),
        e.playable ? null : ' · can\'t be read')),
    h('span', { class: 'play' }, disabled ? '' : '▶')];
  return h('div', { class: 'episode' + (done ? ' watched' : '') + (disabled ? ' hidden' : '') },
    disabled ? h('div', { class: 'episode-main' }, content)
      : h('a', { class: 'episode-main', href: `#/play/${e.id}`, onclick: () => clearQueue() }, content),
    onHide && !off ? hideButton('file', e.id, e.hidden, onHide, 'episode') : null);
}

async function showView(id) {
  const show = await api(`/api/shows/${id}`);
  const list = h('div', { class: 'episodes' });
  const tabs = h('div', { class: 'tabs' });
  let current = null;
  const pick = season => {
    current = season;
    for (const b of tabs.children) b.classList.toggle('active', b.dataset.id === String(season.id));
    mount(list, season.episodes.map(e => episodeRow(e, { off: show.hidden, onHide: hidden => { e.hidden = hidden; pick(current); } })));
  };
  const draw = () => {
    mount(tabs, show.seasons.map(s => h('button', { 'data-id': s.id, onclick: () => pick(s) }, s.title)));
    mount(view,
      h('div', { class: 'row' }, h('h1', {}, show.title), playButtons({ show: id }, show.title, show.hidden),
        hideButton('show', id, show.hidden, hidden => { show.hidden = hidden; draw(); }, 'show')),
      show.hidden ? h('p', { class: 'muted' }, 'You\'ve hidden this show: it\'s left out of Play all, Shuffle and Continue watching. Only you see this.') : null,
      listsControl('show', id), tabs, list);
    // Open on the season that was open, else the first with something unwatched
    const first = current ?? show.seasons.find(s => s.episodes.some(e => !e.finished)) ?? show.seasons[0];
    if (first) pick(first);
  };
  draw();
}

async function movieView(id) {
  const movie = await api(`/api/movies/${id}`);
  if (movie.parts.length === 1 && !movie.hidden) { location.replace(`#/play/${movie.parts[0].id}`); return; }
  const draw = () => mount(view,
    h('div', { class: 'row' }, h('h1', {}, movie.title + (movie.year ? ` (${movie.year})` : '')),
      hideButton('movie', id, movie.hidden, hidden => { movie.hidden = hidden; draw(); }, 'movie')),
    movie.hidden ? h('p', { class: 'muted' }, 'You\'ve hidden this movie: it\'s left out of Play all, Shuffle and Continue watching. Only you see this.') : null,
    listsControl('movie', id),
    h('div', { class: 'episodes' }, movie.parts.map(p => episodeRow({ ...p, title: movie.parts.length > 1 ? `Part ${p.partNumber}` : movie.title, episodeNumber: null }, { off: movie.hidden }))));
  draw();
}

async function playView(fileId) {
  const details = await api(`/api/files/${fileId}`);
  // Music (reached from a mixed queue, or an old link) plays on the listening page
  if (details.kind === 'Track') { location.replace(`#/listen/${fileId}`); return; }
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
  // Favorite or list the movie, or the show an episode is from, from here too
  const lists = details.movieId ? listsControl('movie', details.movieId)
    : details.showId ? listsControl('show', details.showId, 'This show:') : null;
  // Hide this movie, or this episode (a hidden show is unhidden from its own page); it keeps playing now
  const hideBox = h('div', { class: 'row' });
  const drawHide = () => mount(hideBox,
    details.movieId ? hideButton('movie', details.movieId, details.hidden.movie, x => { details.hidden.movie = x; drawHide(); }, 'movie')
      : details.hidden.show ? h('span', { class: 'muted' }, 'You\'ve hidden this show. ', h('a', { href: `#/show/${details.showId}` }, 'Unhide it on its page'))
      : hideButton('file', fileId, details.hidden.file, x => { details.hidden.file = x; drawHide(); }, 'episode'),
    (details.hidden.movie || details.hidden.file) ? h('span', { class: 'muted' }, 'Hidden: left out of Play all, Shuffle and Continue watching.') : null);
  drawHide();
  mount(view, h('h1', {}, details.summary.title), queueBar, p.element(), lists, hideBox,
    h('p', { class: 'muted' }, [details.videoCodec, details.width ? `${details.width}×${details.height}` : null, details.container].filter(Boolean).join(' · ')));
  try { await p.start(); }
  catch (e) { p.status.textContent = e.message; }
}

// ── Admin ────────────────────────────────────────────────────────────────────

// Add or edit a library, in place of the "Add a library" button
function libraryForm(lib) {
  const name = h('input', { value: lib?.name ?? '', maxlength: 100, required: true });
  const kind = h('select', {}, ['Shows', 'Movies', 'Music'].map(k => h('option', { value: k, selected: (lib?.kind ?? 'Shows') === k }, k)));
  const path = h('input', { value: lib?.path ?? '', size: 50, placeholder: '/mnt/media/Shows or \\\\files.example.lan\\Media\\Shows', required: true });
  const tags = h('input', { value: lib?.tags ?? '', size: 40, placeholder: 'e.g. Cartoons, Kids' });
  const enabled = h('input', { type: 'checkbox', checked: lib?.enabled ?? true });
  const msg = h('p', { class: 'muted' });
  const check = async () => {
    msg.className = 'muted'; msg.textContent = 'Checking…';
    const r = await api('/api/admin/libraries/check', { method: 'POST', body: { path: path.value, kind: kind.value } });
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
        // Your own role is fixed here (another admin can change it), so Admin can't be left without anyone
        h('td', {}, u.userName === state.user.name ? (u.isAdmin ? 'Admin' : 'Viewer') : h('select', { onchange: async e => {
            const admin = e.target.value === 'admin';
            if (admin && !confirm(`Make ${u.userName} an admin? Admins can add and remove libraries, users and invites.`)) { e.target.value = 'viewer'; return; }
            try { await api(`/api/admin/users/${u.id}/role`, { method: 'POST', body: { isAdmin: admin } }); }
            catch (err) { alert(err.message); }
            adminView();
          } },
          h('option', { value: 'viewer', selected: !u.isAdmin }, 'Viewer'),
          h('option', { value: 'admin', selected: u.isAdmin }, 'Admin'))),
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

// Coming back from Google, the server says what happened in ?google=…; it's shown once on Sign in or Profile
const googleOutcome = new URLSearchParams(location.search).get('google');
let googleMessage = {
  linked: { ok: true, text: 'Your Google account is linked: you can now sign in with Google.' },
  taken: { text: 'That Google account is already linked to another MediaRiffle account.' },
  unknown: { text: 'That Google account isn\'t linked to a MediaRiffle account. Sign in with your user name and password, then link it under Profile.' },
  locked: { text: 'Too many failed sign-ins; try again in 15 minutes.' },
  cancelled: { text: 'Google sign-in was cancelled.' },
  failed: { text: 'Google sign-in didn\'t work; please try again.' },
}[googleOutcome] ?? null;
if (googleOutcome !== null) history.replaceState(null, '', location.pathname + location.hash);

function takeGoogleMessage() {
  const m = googleMessage;
  googleMessage = null;
  return m ? h('p', { class: m.ok ? 'ok' : 'error' }, m.text) : null;
}

addEventListener('hashchange', route);
loadState()
  // replaceState, not location.hash, so the page isn't routed twice
  .then(() => { if (googleMessage && state.user) history.replaceState(null, '', location.pathname + '#/profile'); })
  .then(route).catch(e => mount(view, errorBox(e)));
