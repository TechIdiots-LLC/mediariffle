// Games: tiles, a system's games, and one game's page. The game itself plays on a page of its own (game.html), whose
// Content Security Policy lets the emulator (WebAssembly) run, which this one's doesn't.

import { api } from './api.js';
import { h, mount } from './dom.js';

// Box art when the library has a picture for the game; otherwise the system's name on a plain tile
export function gameArt(g) {
  return h('div', { class: 'art game' },
    h('span', { class: 'system', 'aria-hidden': 'true' }, g.systemName ?? g.system?.name ?? ''),
    g.image ? h('img', { src: g.image, alt: '', loading: 'lazy', onerror: e => { e.target.remove(); } }) : null);
}

export function gameTiles(games) {
  return games.map(g => h('a', { class: 'tile', href: `#/game/${g.id}` },
    gameArt(g), h('div', { class: 'name', title: g.title }, g.title), h('div', { class: 'sub' }, g.systemName)));
}

function filterBox(onChange) {
  return h('input', { type: 'search', placeholder: 'Filter…', oninput: e => onChange(e.target.value.trim().toLowerCase()) });
}

// Every game for one system, from all the Games libraries
export async function systemView(view, id) {
  const [systems, games] = await Promise.all([api('/api/games/systems'), api(`/api/games?system=${encodeURIComponent(id)}`)]);
  const system = systems.find(s => s.id === id);
  const grid = h('div', { class: 'grid' });
  const draw = q => mount(grid, gameTiles(games.filter(g => !q || g.title.toLowerCase().includes(q))));
  draw('');
  mount(view,
    h('div', { class: 'row' }, h('h1', {}, system?.name ?? id), filterBox(draw)),
    system && !system.playable ? h('p', { class: 'muted' }, `No emulator for the ${system.name} runs in a browser, so these can't be played here.`) : null,
    grid,
    games.length ? null : h('p', { class: 'muted' }, 'No games for this system.'));
}

// One game: its picture, system and file, the user's lists for it, and Play
export async function gameView(view, id, { listsControl }) {
  const g = await api(`/api/games/${id}`);
  mount(view,
    h('div', { class: 'game-page' },
      h('div', { class: 'game-cover' }, gameArt(g)),
      h('div', { class: 'game-info' },
        h('h1', {}, g.title),
        h('p', { class: 'muted' },
          h('a', { href: `#/system/${encodeURIComponent(g.system.id)}` }, g.system.name), ' · ',
          h('a', { href: `#/library/${g.library.id}` }, g.library.name), ' · ', g.file, ' · ', size(g.size)),
        // Played on a page of its own (game.html), whose security settings let the emulator run
        g.notReady
          ? h('p', { class: 'muted' }, g.notReady)
          : h('div', { class: 'game-actions' }, h('a', { class: 'button', href: `/game.html#${g.id}` }, '▶ Play')),
        listsControl('game', g.id))));
}

// Admin → Games: the browser emulator (EmulatorJS and its cores: not part of MediaRiffle, so downloaded from npm here,
// or uploaded as npm's .tgz files) and the BIOS files some systems need
export function adminGamesSection() {
  const box = h('div', { class: 'card emulator-admin' });
  let timer = null;

  const upload = async (url, file) => {
    const r = await api(url, { method: 'POST', body: file });
    return r;
  };

  const draw = s => {
    const busy = !!s.progress;
    const wanted = s.cores.filter(c => c.games > 0 && !c.installed);
    const note = h('span', { class: 'muted' });
    const tgz = h('input', { type: 'file', accept: '.tgz,application/gzip', multiple: true, onchange: async e => {
      for (const file of e.target.files) {
        note.textContent = `Adding ${file.name}…`;
        try { note.textContent = (await upload('/api/admin/emulator/upload', file)).result; }
        catch (err) { note.textContent = `${file.name}: ${err.message}`; return; }
      }
      refresh();
    } });
    mount(box,
      h('p', {}, s.installed
        ? `EmulatorJS ${s.installed} is set up${s.installed !== s.version ? ` (MediaRiffle is made for ${s.version}: download to change to it)` : ''}.`
        : 'The emulator isn\'t set up yet. MediaRiffle doesn\'t include it: download it here (from npm, about 2 MB, plus 4 to 15 MB per system), or upload npm\'s .tgz files of it.'),
      h('div', { class: 'row' },
        h('button', { disabled: busy || (s.installed === s.version && !wanted.length), onclick: async () => {
          try { await api('/api/admin/emulator/download', { method: 'POST', body: { cores: [] } }); refresh(); }
          catch (err) { alert(err.message); }
        } }, s.installed === s.version ? 'Download the missing systems' : `Download EmulatorJS ${s.version}`),
        h('span', { class: busy ? '' : 'muted' }, s.progress ?? s.lastResult ?? '')),
      h('table', {},
        h('tr', {}, h('th', {}, 'Core'), h('th', {}, 'Plays'), h('th', {}, 'Games'), h('th', {}, '')),
        s.cores.filter(c => c.games > 0 || c.installed).map(c => h('tr', {},
          h('td', {}, h('code', {}, c.core)), h('td', {}, c.systems.join(', ')), h('td', {}, String(c.games)),
          h('td', {}, c.installed ? '✓ added' : busy ? '' : h('button', { class: 'secondary', onclick: async () => {
            try { await api('/api/admin/emulator/download', { method: 'POST', body: { cores: [c.core] } }); refresh(); }
            catch (err) { alert(err.message); }
          } }, 'Download'))))),
      h('details', {},
        h('summary', {}, 'No internet on this server? Upload the packages instead'),
        h('p', { class: 'muted' }, `On another computer, download @emulatorjs/emulatorjs@${s.version} and the @emulatorjs/core-… packages you need (npm pack, or from npmjs.com), then add the .tgz files here.`),
        h('div', { class: 'row' }, tgz, note)),
      h('h3', {}, 'BIOS files'),
      h('p', { class: 'muted' }, 'Some systems need the console\'s own BIOS, which only you can provide. They\'re sent only to signed-in users, to play.'),
      h('table', {},
        h('tr', {}, h('th', {}, 'System'), h('th', {}, 'Needs'), h('th', {}, 'Added'), h('th', {}, '')),
        s.bios.map(b => h('tr', {},
          h('td', {}, b.name, b.games ? h('span', { class: 'muted' }, ` (${b.games})`) : null),
          h('td', {}, b.needed ? h('code', {}, b.needed)
            : h('span', { class: 'muted' }, 'optional: ', h('code', {}, b.optional ?? 'any'))),
          h('td', {}, b.files.length ? b.files.map(f => h('span', { class: 'chip' }, f,
            h('button', { class: 'chip-x', title: `Remove ${f}`, onclick: async () => {
              if (!confirm(`Remove ${f}?`)) return;
              try { await api(`/api/admin/emulator/bios/${b.system}/${encodeURIComponent(f)}`, { method: 'DELETE' }); refresh(); }
              catch (err) { alert(err.message); }
            } }, '×'))) : h('span', { class: b.needed && b.games ? 'error' : 'muted' }, 'none')),
          h('td', {}, h('input', { type: 'file', 'aria-label': `Add a BIOS file for ${b.name}`, onchange: async e => {
            const file = e.target.files[0];
            if (!file) return;
            try { await upload(`/api/admin/emulator/bios/${b.system}?name=${encodeURIComponent(file.name)}`, file); refresh(); }
            catch (err) { alert(err.message); }
          } }))))));
    clearTimeout(timer);
    if (busy) timer = setTimeout(refresh, 2000);
  };

  async function refresh() {
    if (!box.isConnected && timer) return;   // left Admin
    try { draw(await api('/api/admin/emulator')); }
    catch (err) { mount(box, h('p', { class: 'error' }, err.message)); }
  }
  refresh();
  return box;
}

function size(bytes) {
  if (bytes >= 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`;
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}
