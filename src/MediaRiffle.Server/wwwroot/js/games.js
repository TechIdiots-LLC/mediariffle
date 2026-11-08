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
        g.system.core ? null : h('p', { class: 'muted' }, `No emulator for the ${g.system.name} runs in a browser, so this game can't be played here.`),
        listsControl('game', g.id))));
}

function size(bytes) {
  if (bytes >= 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`;
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}
