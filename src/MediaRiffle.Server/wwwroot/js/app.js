import { api, refreshCsrf } from './api.js';
import { h, mount, art, formatTime } from './dom.js';
import { Player, inTheater, exitTheater } from './player.js';
import { startQueue, positionOf, clearQueue } from './queue.js';
import { music, nowPlayingView, trackRows } from './music.js';
import { avatarEl, BUILT_IN, photoToJpeg } from './avatar.js';
import { adminGate, mfaSettings } from './mfa.js';

const view = document.getElementById('view');
const nav = document.getElementById('nav');
let state = { setupNeeded: false, user: null };
let player = null;
let pageTimer = null;   // a page's auto-refresh, stopped when leaving it
let leaving = [];       // a page's clean-ups (e.g. no longer following the music player), run when leaving it
const onLeave = fn => leaving.push(fn);

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
      h('summary', {}, avatarEl(state.user.avatar, 'sm', state.user.name), ' ', state.user.name),
      h('div', { class: 'menu' },
        h('a', { href: '#/profile' }, 'Profile'),
        h('a', { href: '#/switch' }, 'Switch user'),
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
  [/^#\/reset\/([^/]+)\/(.+)$/, m => resetView(decodeURIComponent(m[1]), m[2])],
  [/^#\/logout$/, logout],
  [/^#\/switch$/, switchView],
  [/^#\/add-account(?:\/(.+))?$/, m => signInForm(m[1] ? decodeURIComponent(m[1]) : '')],
  [/^#\/library\/(\d+)$/, m => libraryView(+m[1])],
  [/^#\/tag\/(.+)$/, m => tagView(decodeURIComponent(m[1]))],
  [/^#\/list\/(.+)$/, m => listView(decodeURIComponent(m[1]))],
  [/^#\/show\/(\d+)$/, m => showView(+m[1])],
  [/^#\/movie\/(\d+)$/, m => movieView(+m[1])],
  [/^#\/artist\/(\d+)$/, m => artistView(+m[1])],
  [/^#\/album\/(\d+)$/, m => albumView(+m[1])],
  [/^#\/play\/(\d+)$/, m => playView(+m[1])],
  [/^#\/listen\/(\d+)$/, m => listenView(+m[1])],
  [/^#\/now$/, nowView],
  [/^#\/admin$/, adminView],
  [/^#\/profile$/, profileView],
  [/^#?\/?$/, homeView],
];

// ── Back ─────────────────────────────────────────────────────────────────────
// A Back button in the top bar, as phones and the home-screen app have no browser back to tap. Each page the app opens
// is numbered in its history entry, so Back can tell going back within the app from leaving it: a page opened first
// (from a link, or a reload of the home-screen app) has none of ours before it, and Back goes home instead.
let pageNumber = 0;
const backButton = document.getElementById('back');
backButton.addEventListener('click', () => { if (pageNumber > 1) history.back(); else location.hash = '#/'; });

// A page sending us straight on to another (not signed in, a one-part movie, a song link): the new page takes this
// history entry and its number, so Back neither counts it as a page nor lands back on the one that sent us on
function forward(hash) {
  try { history.replaceState({ ...(history.state ?? {}), page: pageNumber }, '', hash); }
  catch { location.replace(hash); return; }
  route();   // replaceState doesn't fire hashchange
}

function numberPage(hash) {
  const n = history.state?.page;
  if (typeof n === 'number') pageNumber = n;   // back (or forward) to a page we numbered
  else {
    pageNumber += 1;
    try { history.replaceState({ ...(history.state ?? {}), page: pageNumber }, ''); } catch { }
  }
  // The player's page uses the screen's width (a large or 4K screen gets a large picture); the others keep theirs
  document.body.classList.toggle('play-page', /^#\/play\//.test(hash));
  backButton.hidden = /^#?\/?$/.test(hash) || /^#\/(login|setup|join\/|reset\/)/.test(hash);
}

async function route() {
  closeUserMenu();
  numberPage(location.hash || '#/');
  if (player) { player.destroy(); player = null; }
  if (pageTimer) { clearInterval(pageTimer); pageTimer = null; }
  for (const fn of leaving.splice(0)) fn();
  // Full screen carries on from one video to the next (see inTheater), and ends on any other page
  if (!/^#\/play\//.test(location.hash)) exitTheater();
  const hash = location.hash || '#/';
  const open = /^#\/(login|setup|join\/|reset\/|switch|add-account)/.test(hash);
  if (state.setupNeeded && !hash.startsWith('#/setup')) { forward('#/setup'); return; }
  if (!state.user && !open) { forward('#/login'); return; }
  for (const [re, fn] of routes) {
    const m = hash.match(re);
    if (m) {
      try { await fn(m); } catch (e) { mount(view, errorBox(e)); }
      toTop(hash);
      return;
    }
  }
  mount(view, h('p', {}, 'Page not found. ', h('a', { href: '#/' }, 'Home')));
  toTop(hash);
}

// A new page starts at the top: the page isn't reloaded, so the browser would keep where the last one was scrolled to
// (half way down a show, after scrolling a library). Not when the page sent us on (a redirect renders the next one).
function toTop(hash) {
  if ((location.hash || '#/') === hash) window.scrollTo(0, 0);
}

// ── Accounts ─────────────────────────────────────────────────────────────────

function credentialsForm({ title, intro, button, onSubmit, remember = false, extra = null, userName = '' }) {
  const user = h('input', { autocomplete: 'username', required: true, maxlength: 64, value: userName });
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
    remember ? h('label', { class: 'row', title: 'Also keeps this account under Switch user on this device' }, keep, 'Keep me signed in')
      : h('p', { class: 'muted' }, 'At least 10 characters.'),
    h('button', { type: 'submit' }, button),
    msg);
  mount(view, form);
  (userName ? pass : user).focus();
}

function loginView() {
  if (state.user) { forward('#/'); return; }
  // Accounts already signed in on this device: pick one instead of typing a password
  if (state.deviceAccounts > 0) { forward('#/switch'); return; }
  signInForm();
}

// The sign-in form: on the sign-in page, and from Switch user to add another account (`userName`: an account whose
// sign-in here ran out, typed in already)
function signInForm(userName = '') {
  // Google sign-in is a page navigation (to Google and back), not a fetch
  const google = state.google
    ? [h('a', { class: 'google-btn', href: '/api/auth/google/signin' }, 'Sign in with Google'), h('p', { class: 'muted or' }, 'or')]
    : null;
  credentialsForm({ title: state.user ? 'Add an account' : 'Sign in', button: 'Sign in', remember: true, userName,
    extra: [takeGoogleMessage(), google,
      state.deviceAccounts > 0 ? h('p', {}, h('a', { href: '#/switch' }, '← Accounts on this device')) : null],
    onSubmit: async (u, p, keep) => {
      await api('/api/auth/login', { method: 'POST', body: { userName: u, password: p, remember: keep } });
      music.close(true);   // the music and its queue were the other account's
    } });
}

// ── Switch user ──────────────────────────────────────────────────────────────

/**
 * The accounts signed in on this device, to switch to without a password (a PIN if its owner set one), or to take off
 * the device; adding another goes through the sign-in form. Open without being signed in, as the sign-in page.
 */
async function switchView() {
  const accounts = await api('/api/auth/accounts');
  if (!accounts.length) { forward(state.user ? '#/add-account' : '#/login'); return; }
  const msg = h('p', { class: 'error' });
  const done = async () => {
    music.close(true);   // the music and its queue were the other account's
    await loadState();
    location.hash = '#/';
  };
  const pick = async (a, card) => {
    msg.textContent = '';
    if (a.current) { location.hash = '#/'; return; }
    if (a.expired) { location.hash = `#/add-account/${encodeURIComponent(a.name)}`; return; }
    if (a.pin && !card.querySelector('input')) { askPin(a, card); return; }
    try { await api('/api/auth/accounts/switch', { method: 'POST', body: { userId: a.id } }); await done(); }
    catch (e) {
      if (e.data?.expired) location.hash = `#/add-account/${encodeURIComponent(a.name)}`;
      else msg.textContent = e.message;
    }
  };
  const askPin = (a, card) => {
    const pin = h('input', { type: 'password', inputmode: 'numeric', autocomplete: 'off', maxlength: 8, size: 8, placeholder: 'PIN', required: true });
    const err = h('span', { class: 'error' });
    card.append(h('form', { class: 'row account-pin', onsubmit: async e => {
      e.preventDefault();
      err.textContent = '';
      try { await api('/api/auth/accounts/switch', { method: 'POST', body: { userId: a.id, pin: pin.value } }); await done(); }
      catch (x) { err.textContent = x.message; pin.select(); }
    } }, pin, h('button', { type: 'submit' }, 'Switch'), err));
    pin.focus();
  };
  const forget = async (a, e) => {
    e.stopPropagation();
    if (!confirm(`Take ${a.name} off this device? Switching back will need the password.`)) return;
    await api('/api/auth/accounts/forget', { method: 'POST', body: { userId: a.id } });
    if (a.current) { music.close(true); await loadState(); }
    switchView();
  };
  mount(view,
    h('div', { class: 'accounts' },
      h('h1', {}, state.user ? 'Switch user' : 'Who\'s watching?'),
      h('div', { class: 'account-list' }, accounts.map(a => {
        const card = h('div', { class: 'card account' + (a.current ? ' current' : '') });
        card.append(
          h('button', { class: 'account-main', onclick: () => pick(a, card) },
            avatarEl(a.avatar, 'md', a.name),
            h('span', { class: 'account-name' }, a.name,
              h('span', { class: 'muted' }, [a.current ? ' · signed in' : null, a.admin ? ' · admin' : null, a.pin ? ' · 🔒 PIN' : null,
                a.expired ? ' · sign in again' : null].filter(Boolean).join(''))),
          ),
          h('button', { class: 'chip-x', title: 'Take off this device', onclick: e => forget(a, e) }, '×'));
        return card;
      })),
      msg,
      h('div', { class: 'row' },
        h('a', { class: 'google-btn', href: '#/add-account' }, '+ Add another account'),
        h('button', { class: 'secondary', onclick: async () => {
          if (!confirm('Sign out every account on this device?')) return;
          music.close(true);
          await api('/api/auth/logout?all=true', { method: 'POST' });
          await loadState();
          location.hash = '#/login';
        } }, 'Sign out of all')),
      h('p', { class: 'muted' }, 'Accounts signed in here with "Keep me signed in" are listed for 14 days after they\'re last used. ' +
        'An account can ask for a PIN before switching to it (its Profile).')));
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
  // Two-step verification: for admins (Admin asks for it); viewers have nothing it guards
  const mfa = state.user.isAdmin ? h('div') : null;
  mount(view,
    h('h1', {}, 'Profile'),
    h('div', { class: 'card' },
      pictureSection(account),
      h('p', {}, 'Signed in as ', h('strong', {}, account.name), state.user.isAdmin ? ' (admin)' : ''),
      mfa ? [h('h2', {}, 'Two-step verification'), mfa] : null,
      h('h2', {}, 'Password'),
      passwordSection(),
      h('h2', {}, 'Video quality'),
      qualitySection(account),
      h('h2', {}, 'Switch user PIN'),
      pinSection(account),
      h('h2', {}, 'Google'),
      message,
      google,
      h('h2', {}, 'Hidden'),
      hidden));
  if (mfa) mfaSettings(mfa);
  hiddenSection(hidden);
}

// How Auto picks a video's quality: by the connection, best first, or below a ceiling to save data
const PREFERENCES = [
  ['auto', 'Auto', 'By the connection: the best it can keep up with, adjusting as it goes.'],
  ['high', 'High', 'The best first (the file as it is when this device plays it), stepping down only if the connection can\'t keep up.'],
  ['medium', 'Medium', 'Up to 720p: less data, still sharp on most screens.'],
  ['low', 'Low', 'Up to 480p: the least data, for slow or metered connections.'],
];

function preferenceSelect(value, onChange) {
  return h('select', { onchange: e => onChange(e.target.value) },
    PREFERENCES.map(([id, label]) => h('option', { value: id, selected: (value ?? 'auto') === id }, label)));
}

// Profile → Video quality: the user's own preference for Auto (an admin may have set it first), within any cap
function qualitySection(account) {
  const note = h('p', { class: 'muted' });
  const describe = id => {
    const cap = account.maxQuality ? ` An admin limits you to ${account.maxQuality}p at most.` : '';
    note.textContent = PREFERENCES.find(p => p[0] === id)[2] + cap +
      ' Picking a quality in the player overrides this for that video, on that device.';
  };
  describe(account.preferredQuality ?? 'auto');
  const msg = h('span', { class: 'error' });
  return h('div', {},
    h('label', { class: 'row' }, 'Auto plays ', preferenceSelect(account.preferredQuality, async value => {
      msg.textContent = '';
      try { await api('/api/auth/preferences', { method: 'PUT', body: { quality: value } }); account.preferredQuality = value; describe(value); }
      catch (e) { msg.textContent = e.message; }
    }), msg),
    note);
}

// A PIN others on a shared device must enter to switch into this account; setting or removing it wants the password
function pinSection(account) {
  const box = h('div');
  const draw = () => {
    const pin = h('input', { type: 'password', inputmode: 'numeric', autocomplete: 'new-password', maxlength: 8, size: 8, placeholder: '4–8 digits' });
    const pass = h('input', { type: 'password', autocomplete: 'current-password', size: 16, placeholder: 'Your password', required: true });
    const msg = h('span', { class: 'error' });
    const save = async remove => {
      msg.textContent = '';
      try {
        const r = await api('/api/auth/pin', { method: 'POST', body: { password: pass.value, pin: remove ? null : pin.value } });
        account.pin = r.pin;
        draw();
      } catch (e) { msg.textContent = e.message; }
    };
    mount(box,
      h('p', { class: 'muted' }, account.pin
        ? '🔒 On: switching to this account from Switch user asks for the PIN.'
        : 'Off: anyone using a device you\'re signed in on can switch to this account from Switch user. A PIN stops that.'),
      h('form', { class: 'row', onsubmit: e => { e.preventDefault(); save(false); } },
        pin, pass, h('button', { type: 'submit' }, account.pin ? 'Change PIN' : 'Set PIN'),
        account.pin ? h('button', { type: 'button', class: 'secondary', onclick: () => save(true) }, 'Remove PIN') : null),
      msg);
  };
  draw();
  return box;
}

// What the user hid, to open or unhide in one place
async function hiddenSection(box) {
  let items;
  try { items = await api('/api/hidden'); }
  catch (e) { mount(box, errorBox(e)); return; }
  const link = i => ({ show: `#/show/${i.id}`, movie: `#/movie/${i.id}`, artist: `#/artist/${i.id}`, album: `#/album/${i.id}`,
    track: `#/listen/${i.id}` })[i.kind] ?? `#/play/${i.id}`;
  const label = { show: 'Show', movie: 'Movie', file: 'Episode', artist: 'Artist', album: 'Album', track: 'Song' };
  mount(box,
    h('p', { class: 'muted' }, 'Hidden shows, movies, episodes, artists, albums and songs stay listed, greyed out, but are left out of Play all, Shuffle and Continue watching.'),
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
  if (!state.setupNeeded) { forward('#/'); return; }
  credentialsForm({ title: 'Welcome to MediaRiffle', intro: 'Create the admin account. Others join with invite links you make under Admin.',
    button: 'Create admin account',
    onSubmit: (u, p) => api('/api/auth/setup', { method: 'POST', body: { userName: u, password: p } }) });
}

function joinView(m) {
  credentialsForm({ title: 'Join MediaRiffle', intro: 'Choose a user name and password for your account.', button: 'Create account',
    onSubmit: (u, p) => api('/api/auth/register', { method: 'POST', body: { invite: m[1], userName: u, password: p } }) });
}

// An admin's password reset link: a new password (twice), then signed in with it
function resetView(userId, token) {
  const pass = h('input', { type: 'password', autocomplete: 'new-password', required: true, minlength: 10 });
  const again = h('input', { type: 'password', autocomplete: 'new-password', required: true, minlength: 10 });
  const keep = h('input', { type: 'checkbox', checked: true });
  const msg = h('p', { class: 'error' });
  mount(view, h('form', { class: 'form card', onsubmit: async e => {
      e.preventDefault();
      msg.textContent = '';
      if (pass.value !== again.value) { msg.textContent = 'The two passwords aren\'t the same.'; return; }
      try {
        await api('/api/auth/reset-password', { method: 'POST', body: { userId, token, password: pass.value, remember: keep.checked } });
        music.close(true);   // anything playing was whoever was signed in here before
        await loadState();
        location.hash = '#/';
      } catch (err) { msg.textContent = err.message; }
    } },
    h('h1', {}, 'Choose a new password'),
    h('p', { class: 'muted' }, 'An admin sent you this link. It works once, for 24 hours.'),
    h('label', {}, 'New password', pass),
    h('label', {}, 'New password again', again),
    h('p', { class: 'muted' }, 'At least 10 characters.'),
    h('label', { class: 'row' }, keep, 'Keep me signed in'),
    h('button', { type: 'submit' }, 'Set password and sign in'),
    msg));
  pass.focus();
}

// Profile → picture: the current one, the built-in ones to pick from, or a photo (cropped and shrunk here first)
function pictureSection(account) {
  const box = h('div', { class: 'picture-section' });
  const msg = h('p', { class: 'error' });
  const done = async avatar => {
    account.avatar = avatar;
    // The menu's picture follows
    state.user.avatar = avatar;
    renderNav();
    draw();
  };
  const pick = async n => {
    msg.textContent = '';
    try { done(await api('/api/auth/avatar', { method: 'PUT', body: { emoji: n } })); } catch (e) { msg.textContent = e.message; }
  };
  const file = h('input', { type: 'file', accept: 'image/*', hidden: true, onchange: async () => {
    msg.textContent = '';
    const f = file.files[0];
    file.value = '';
    if (!f) return;
    try { done(await api('/api/auth/avatar/photo', { method: 'POST', body: await photoToJpeg(f) })); }
    catch (e) { msg.textContent = e.message || 'That picture couldn\'t be used.'; }
  } });
  const draw = () => mount(box,
    h('div', { class: 'row' },
      avatarEl(account.avatar, 'lg', account.name),
      h('div', { class: 'picture-actions' },
        h('button', { class: 'secondary', onclick: () => file.click() }, account.avatar?.photo ? 'Change photo' : 'Use a photo'),
        account.avatar?.photo ? h('button', { class: 'secondary', onclick: async () => {
          try { done(await api('/api/auth/avatar', { method: 'DELETE' })); } catch (e) { msg.textContent = e.message; }
        } }, 'Remove photo') : null),
      file),
    h('div', { class: 'avatar-grid', role: 'listbox', 'aria-label': 'Pictures' },
      BUILT_IN.map(([, what], n) => h('button', {
        class: 'avatar-choice' + (!account.avatar?.photo && account.avatar?.emoji === n ? ' chosen' : ''),
        title: what, 'aria-label': what, onclick: () => pick(n),
      }, avatarEl({ emoji: n }, 'md')))),
    msg);
  draw();
  return box;
}

// Profile → Password: the current one, then the new one twice. Other devices are signed out; this one stays.
function passwordSection() {
  const current = h('input', { type: 'password', autocomplete: 'current-password', required: true });
  const pass = h('input', { type: 'password', autocomplete: 'new-password', required: true, minlength: 10 });
  const again = h('input', { type: 'password', autocomplete: 'new-password', required: true, minlength: 10 });
  const msg = h('p', { class: 'error' });
  const form = h('form', { class: 'password-form', onsubmit: async e => {
      e.preventDefault();
      msg.className = 'error';
      if (pass.value !== again.value) { msg.textContent = 'The two new passwords aren\'t the same.'; return; }
      try {
        await api('/api/auth/password', { method: 'POST', body: { current: current.value, new: pass.value } });
        form.reset();
        msg.className = 'ok';
        msg.textContent = 'Password changed. Your other devices will need to sign in again.';
      } catch (err) { msg.textContent = err.message; }
    } },
    h('label', {}, 'Current password', current),
    h('label', {}, 'New password', pass),
    h('label', {}, 'New password again', again),
    h('div', { class: 'row' }, h('button', { type: 'submit' }, 'Change password'), h('span', { class: 'muted' }, 'At least 10 characters.')),
    msg);
  return form;
}

// Signs out the account in use (taking it off this device); others still signed in here are offered next
async function logout() {
  music.close(true);
  const r = await api('/api/auth/logout', { method: 'POST' });
  await loadState();
  location.hash = r?.others > 0 ? '#/switch' : '#/login';
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
    recent.length ? [h('h2', {}, 'Continue watching'), h('div', { class: 'grid' }, recent.map(r => {
      // ×: off this list (not hidden: the place is kept, and watching it again brings it back)
      const tile = h('a', { class: 'tile continue', href: `#/play/${r.file.id}` },
        art(r.file.image, r.file.duration ? r.file.position / r.file.duration : 0),
        h('button', { class: 'tile-x', title: 'Remove from Continue watching', 'aria-label': 'Remove from Continue watching',
          onclick: async e => {
            e.preventDefault(); e.stopPropagation();
            try { await api(`/api/continue/${r.file.id}`, { method: 'DELETE' }); tile.remove(); } catch (err) { alert(err.message); }
          } }, '×'),
        h('div', { class: 'name' }, r.show ?? r.file.title),
        h('div', { class: 'sub' }, r.show ? r.file.title : `${formatTime(r.file.position)} of ${formatTime(r.file.duration)}`));
      return tile;
    }))] : null,
    favorites.shows.length || favorites.movies.length || favorites.artists.length || favorites.albums.length || favorites.tracks.length
      ? [h('div', { class: 'row' }, h('h2', {}, '★ Favorites'), h('a', { href: `#/list/${FAVORITES}` },
            favorites.tracks.length ? `${favorites.tracks.length} song${favorites.tracks.length === 1 ? '' : 's'}, play or shuffle →` : 'Play or shuffle →')),
         h('div', { class: 'grid' }, showTiles(favorites.shows), movieTiles(favorites.movies),
           artistTiles(favorites.artists), albumTiles(favorites.albums, true))] : null,
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

// Hidden artists and albums stay listed, greyed out, like hidden shows
function artistTiles(artists) {
  return artists.map(a => h('a', { class: 'tile' + (a.hidden ? ' hidden' : ''), href: `#/artist/${a.id}` },
    art(a.image, 0, 'music'), h('div', { class: 'name' }, a.name),
    h('div', { class: 'sub' }, a.hidden ? 'Hidden' : `${a.albums} album${a.albums === 1 ? '' : 's'}`)));
}

function albumTiles(albums, showArtist = false) {
  return albums.map(a => h('a', { class: 'tile' + (a.hidden ? ' hidden' : ''), href: `#/album/${a.id}` },
    art(a.image, 0, 'music'), h('div', { class: 'name' }, a.title),
    h('div', { class: 'sub' }, [a.hidden ? 'Hidden' : null, showArtist ? a.artist : null, a.year,
      `${a.tracks} track${a.tracks === 1 ? '' : 's'}`].filter(Boolean).join(' · '))));
}

const hiddenNote = noun => h('p', { class: 'muted' }, `You've hidden this ${noun}: it's left out of Play all and Shuffle. Only you see this.`);

async function artistView(id) {
  const a = await api(`/api/artists/${id}`);
  const draw = () => mount(view,
    h('div', { class: 'row' }, h('h1', {}, a.name), playButtons({ artist: id }, a.name, a.hidden),
      hideButton('artist', id, a.hidden, x => { a.hidden = x; draw(); }, 'artist')),
    a.hidden ? hiddenNote('artist') : null,
    listsControl('artist', id),
    h('div', { class: 'grid' }, albumTiles(a.albums.map(x => ({ ...x, hidden: x.hidden || a.hidden })))),
    a.appearsOn.length ? [h('h2', {}, 'Appears on'), h('div', { class: 'grid' }, albumTiles(a.appearsOn, true))] : null);
  draw();
}

// A track's ☆: in or out of Favorites, from an album page
function trackFavorite(t) {
  const btn = h('button', { class: 'chip-x track-fav' + (t.favorite ? ' on' : ''), title: t.favorite ? 'Take out of Favorites' : 'Add to Favorites',
    onclick: async e => {
      e.preventDefault();
      try {
        const tags = await api(`/api/items/track/${t.id}/lists`);
        const saved = await api(`/api/items/track/${t.id}/lists`, { method: 'PUT',
          body: { tags: t.favorite ? tags.filter(x => !isFavorites(x)) : [...tags, FAVORITES] } });
        t.favorite = saved.some(isFavorites);
        btn.textContent = t.favorite ? '★' : '☆';
        btn.title = t.favorite ? 'Take out of Favorites' : 'Add to Favorites';
        btn.classList.toggle('on', t.favorite);
      } catch (err) { alert(err.message); }
    } }, t.favorite ? '★' : '☆');
  return btn;
}

async function albumView(id) {
  const a = await api(`/api/albums/${id}`);
  // A track starts the album from there, in the player bar
  const pick = t => startQueue({ album: id }, false, a.title, t.id).catch(e => alert(e.message));
  let stop = () => { };
  onLeave(() => stop());
  const draw = () => {
    stop();
    // A hidden album (or artist) greys out every track; otherwise each can be hidden on its own
    const off = a.hidden || a.artistHidden;
    const tracks = trackRows({ tracks: a.tracks.map(t => ({ ...t, off })) }, pick, t => off ? null : h('span', { class: 'row track-actions' },
      trackFavorite(a.tracks.find(x => x.id === t.id)),
      hideButton('track', t.id, t.hidden, x => { a.tracks.find(y => y.id === t.id).hidden = x; draw(); }, 'track')));
    stop = tracks.cleanup;
    mount(view,
      h('div', { class: 'album-head' },
        h('div', { class: 'listen-art' }, art(a.image, 0, 'music')),
        h('div', {},
          h('h1', {}, a.title),
          h('p', {}, h('a', { href: `#/artist/${a.artist.id}` }, a.artist.name), a.year ? ` · ${a.year}` : ''),
          h('p', { class: 'muted' }, `${a.tracks.length} track${a.tracks.length === 1 ? '' : 's'} · ${formatTime(a.duration)}`),
          h('div', { class: 'row' }, playButtons({ album: id }, a.title, off),
            a.artistHidden ? null : hideButton('album', id, a.hidden, x => { a.hidden = x; draw(); }, 'album')),
          a.artistHidden ? h('p', { class: 'muted' }, 'You\'ve hidden this artist. ', h('a', { href: `#/artist/${a.artist.id}` }, 'Unhide them on their page'))
            : a.hidden ? hiddenNote('album') : null,
          listsControl('album', id))),
      tracks.element);
  };
  draw();
}

// A link to a track (a list, Profile → Hidden): played in the player bar, shown on Now playing
async function listenView(fileId) {
  try { await music.playFile(fileId); }
  catch (e) { if (e.status === 404) { forward(`#/play/${fileId}`); return; } throw e; }
  forward('#/now');
}

// Now playing, with the playing track's Favorite and lists (a playlist is a list of tracks)
function nowView() { onLeave(nowPlayingView(view, t => listsControl('track', t.id, 'This track:'))); }

// One of the user's own lists (Favorites among them): the shows, movies, artists and albums they tagged with it, and
// its tracks, in the order they were added (a playlist)
async function listView(tag) {
  const t = await api(`/api/lists/${encodeURIComponent(tag)}`);
  const showGrid = h('div', { class: 'grid' }), movieGrid = h('div', { class: 'grid' });
  const artistGrid = h('div', { class: 'grid' }), albumGrid = h('div', { class: 'grid' });
  const draw = q => {
    const match = i => !q || (i.title ?? i.name).toLowerCase().includes(q);
    mount(showGrid, showTiles(t.shows.filter(match)));
    mount(movieGrid, movieTiles(t.movies.filter(match)));
    mount(artistGrid, artistTiles(t.artists.filter(match)));
    mount(albumGrid, albumTiles(t.albums.filter(match), true));
  };
  draw('');
  // Its tracks: picking one plays the list from there; × takes it out of the list
  const remove = async track => {
    try {
      const tags = await api(`/api/items/track/${track.id}/lists`);
      await api(`/api/items/track/${track.id}/lists`, { method: 'PUT', body: { tags: tags.filter(x => x.toLowerCase() !== t.tag.toLowerCase()) } });
      listView(tag);
    } catch (e) { alert(e.message); }
  };
  const tracks = trackRows({ tracks: t.tracks.map((x, i) => ({ ...x, track: i + 1, artist: [x.artist, x.album].filter(Boolean).join(' · ') })) },
    x => startQueue({ list: t.tag }, false, t.tag, x.id).catch(e => alert(e.message)),
    x => h('button', { class: 'chip-x', title: `Take out of ${t.tag}`, onclick: () => remove(x) }, '×'));
  onLeave(tracks.cleanup);
  const empty = !t.shows.length && !t.movies.length && !t.artists.length && !t.albums.length && !t.tracks.length;
  mount(view,
    h('div', { class: 'row' }, h('h1', {}, t.favorites ? '★ Favorites' : t.tag), empty ? null : playButtons({ list: t.tag }, t.tag), empty ? null : filterBox(draw),
      // Rename (two lists by one name become one) or delete the whole list; Favorites can be cleared, not renamed
      t.favorites ? null : h('button', { class: 'secondary', onclick: async () => {
        const name = prompt('A new name for this list (an existing list\'s name puts the two together):', t.tag)?.trim();
        if (!name || name === t.tag) return;
        try { const r = await api(`/api/lists/${encodeURIComponent(t.tag)}`, { method: 'PUT', body: { name } }); location.replace(`#/list/${encodeURIComponent(r.tag)}`); }
        catch (e) { alert(e.message); }
      } }, 'Rename'),
      h('button', { class: 'secondary', onclick: async () => {
        if (!confirm(t.favorites ? 'Take everything out of Favorites?' : `Delete the list "${t.tag}"? What's on it stays in the library.`)) return;
        try { await api(`/api/lists/${encodeURIComponent(t.tag)}`, { method: 'DELETE' }); location.hash = '#/'; }
        catch (e) { alert(e.message); }
      } }, t.favorites ? 'Clear favorites' : 'Delete list')),
    h('p', { class: 'muted' }, 'Only you see your lists. Add a show, movie, artist or album to one from its page, and a song from Now playing.'),
    t.shows.length ? [h('h2', {}, 'Shows'), showGrid] : null,
    t.movies.length ? [h('h2', {}, 'Movies'), movieGrid] : null,
    t.artists.length ? [h('h2', {}, 'Artists'), artistGrid] : null,
    t.albums.length ? [h('h2', {}, 'Albums'), albumGrid] : null,
    t.tracks.length ? [h('h2', {}, 'Songs'), tracks.element] : null,
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
  // A show with other series (UK, All Stars, …: "UK - Season 01" folders) picks the series first, then its seasons
  const series = [...new Set(show.seasons.map(s => s.series))];
  const seriesTabs = series.length > 1 ? h('div', { class: 'tabs series-tabs' }) : null;
  const seasonLabel = s => series.length > 1 ? (s.number === 0 ? 'Specials' : `Season ${s.number}`) : s.title;
  let current = null;
  const pick = season => {
    current = season;
    if (seriesTabs) {
      for (const b of seriesTabs.children) b.classList.toggle('active', b.dataset.series === season.series);
      mount(tabs, show.seasons.filter(s => s.series === season.series).map(s => h('button', { 'data-id': s.id, onclick: () => pick(s) }, seasonLabel(s))));
    }
    for (const b of tabs.children) b.classList.toggle('active', b.dataset.id === String(season.id));
    mount(list, season.episodes.map(e => episodeRow(e, { off: show.hidden, onHide: hidden => { e.hidden = hidden; pick(current); } })));
  };
  // A series opens on its first season with something unwatched
  const firstOf = name => {
    const seasons = show.seasons.filter(s => s.series === name);
    return seasons.find(s => s.episodes.some(e => !e.finished)) ?? seasons[0];
  };
  const draw = () => {
    if (seriesTabs) mount(seriesTabs, series.map(name => h('button', { 'data-series': name, onclick: () => pick(firstOf(name)) }, name || show.title)));
    else mount(tabs, show.seasons.map(s => h('button', { 'data-id': s.id, onclick: () => pick(s) }, s.title)));
    mount(view,
      h('div', { class: 'row' }, h('h1', {}, show.title), playButtons({ show: id }, show.title, show.hidden),
        hideButton('show', id, show.hidden, hidden => { show.hidden = hidden; draw(); }, 'show')),
      show.hidden ? h('p', { class: 'muted' }, 'You\'ve hidden this show: it\'s left out of Play all, Shuffle and Continue watching. Only you see this.') : null,
      listsControl('show', id), seriesTabs, tabs, list);
    // Open on the season that was open, else the show's own first season with something unwatched
    const first = current ?? firstOf(series[0]) ?? show.seasons[0];
    if (first) pick(first);
  };
  draw();
}

async function movieView(id) {
  const movie = await api(`/api/movies/${id}`);
  if (movie.parts.length === 1 && !movie.hidden) { forward(`#/play/${movie.parts[0].id}`); return; }
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
  if (details.kind === 'Track') { forward(`#/listen/${fileId}`); return; }
  // A video takes over from the music
  music.close();
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
      // In full screen the queue bar is out of sight: the countdown shows over the picture
      const flash = () => { if (inTheater()) p.flash(`Next in ${left}…`, place.next.title, 1500); };
      flash();
      const timer = setInterval(() => {
        if (--left > 0) { msg.textContent = `Next in ${left}…`; flash(); return; }
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
  // Full screen's Hide: this episode (or movie) is hidden, and the next one plays, still full screen
  p.onHide = async () => {
    const kind = details.movieId ? 'movie' : 'file';
    try { await api(`/api/items/${kind}/${kind === 'movie' ? details.movieId : fileId}/hidden`, { method: 'PUT', body: { hidden: true } }); }
    catch (e) { p.flash('Couldn\'t hide it', e.message); return; }
    if (kind === 'movie') details.hidden.movie = true; else details.hidden.file = true;
    drawHide();
    if (p.nav.next) p.goNext();
    else { p.flash('Hidden', 'Nothing after this one'); p.video.pause(); }
  };
  mount(view, h('h1', {}, details.summary.title), queueBar, p.element(), lists, hideBox,
    h('p', { class: 'muted' }, [details.videoCodec, details.width ? `${details.width}×${details.height}` : null, details.container].filter(Boolean).join(' · ')));
  // Arriving in full screen, or from a queue (the next one): what this is, for a few seconds, so it's easy to skip on
  if (inTheater() || place) p.flash(details.summary.title, place ? `${place.label} · ${place.index + 1} of ${place.count}` : null);
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
  if (!state.user?.isAdmin) { forward('#/'); return; }
  let users, invites, scan, sessions, qualities, libraries;
  try {
    [users, invites, scan, sessions, qualities, libraries] = await Promise.all([
      api('/api/admin/users'), api('/api/admin/invites'), api('/api/admin/scan'), api('/api/admin/sessions'), api('/api/admin/qualities'),
      api('/api/admin/libraries')]);
  } catch (e) {
    // Admin wants two-step verification first: a check, or setting one up (guided)
    if (e.data?.mfa) { await adminGate(view, () => { if (location.hash === '#/admin') adminView(); }); return; }
    throw e;
  }

  const link = h('input', { readonly: true, size: 60 });
  const asAdmin = h('input', { type: 'checkbox' });
  // A password reset link just made, to copy and send (it isn't stored, so it shows only here, once)
  const resetBox = h('div');
  const showResetLink = (name, url) => {
    const input = h('input', { readonly: true, size: 60, value: url });
    mount(resetBox, h('div', { class: 'card reset-link' },
      h('p', {}, `Send ${name} this link to choose a new password. It works once, for 24 hours.`),
      h('div', { class: 'row' }, input,
        h('button', { class: 'secondary', onclick: async e => {
          try { await navigator.clipboard.writeText(url); e.target.textContent = 'Copied ✓'; } catch { input.select(); }
        } }, 'Copy'),
        h('button', { class: 'chip-x', title: 'Close', onclick: () => mount(resetBox) }, '×'))));
    input.select();
  };
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
    resetBox,
    h('table', {}, h('tr', {}, h('th', {}, 'Name'), h('th', {}, 'Role'), h('th', { title: 'The best quality this user may stream' }, 'Best quality'),
        h('th', { title: 'How Auto picks for this user; they can change it under Profile' }, 'Default quality'), h('th', {}, 'Joined'), h('th', {})),
      users.map(u => h('tr', {},
        h('td', { class: 'user-cell' }, avatarEl(u.avatar, 'sm', u.userName), ' ', u.userName, u.lockedOut ? h('span', { class: 'error' }, ' (locked out)') : null,
          u.mfa ? h('span', { class: 'muted', title: 'Has a passkey or an authenticator app' }, ' 🔑') : null),
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
        h('td', {}, preferenceSelect(u.preferredQuality, async value => {
          try { await api(`/api/admin/users/${u.id}/preferred-quality`, { method: 'POST', body: { quality: value } }); }
          catch (err) { alert(err.message); }
        })),
        h('td', {}, new Date(u.createdUtc).toLocaleDateString()),
        h('td', { class: 'row' },
          u.lockedOut ? h('button', { class: 'secondary', onclick: async () => { await api(`/api/admin/users/${u.id}/unlock`, { method: 'POST' }); adminView(); } }, 'Unlock') : null,
          // A one-time link for them to choose a new password (shown above the table, to send them)
          u.userName !== state.user.name ? h('button', { class: 'secondary', onclick: async () => {
            try { showResetLink(u.userName, (await api(`/api/admin/users/${u.id}/password-reset`, { method: 'POST' })).link); }
            catch (e) { alert(e.message); }
          } }, 'Reset password') : null,
          // For someone who lost their passkeys, authenticator and recovery codes
          u.mfa && u.userName !== state.user.name ? h('button', { class: 'secondary', onclick: async () => {
            if (!confirm(`Reset ${u.userName}'s two-step verification? Their passkeys, authenticator app and recovery codes stop working` +
              (u.isAdmin ? ', and they\'ll set it up again the next time they open Admin.' : '.'))) return;
            try { await api(`/api/admin/users/${u.id}/reset-mfa`, { method: 'POST' }); adminView(); } catch (e) { alert(e.message); }
          } }, 'Reset two-step') : null,
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
  // After a reload, the music that was playing is back in the bar, paused where it was
  .then(() => { if (state.user && !/^#\/(play|listen)\//.test(location.hash)) music.restore(); })
  .then(route).catch(e => mount(view, errorBox(e)));
