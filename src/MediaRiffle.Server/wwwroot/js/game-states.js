// Save states on the server: the emulator's Save State and Load State buttons (its bottom bar) keep them with the user,
// each with the screenshot taken with it, rather than in this browser. Load State shows them to pick from (over the
// game, in full screen too); a game's page lists them with Resume, which opens the game at that state.

import { api } from './api.js';
import { h } from './dom.js';

const when = iso => new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });

// A screenshot of the game as it is, as a JPEG at most 640 wide, without the bars beside it. The emulator draws with
// WebGL, whose picture is gone once the frame is shown (EmulatorJS's own screenshot of it comes out black), so it's
// copied in a frame callback, after the emulator has drawn; that isn't always so, hence a few tries. Null when every
// try is blank.
function grab(emulator) {
  return new Promise(resolve => {
    let tries = 0;
    const attempt = () => requestAnimationFrame(() => {
      try {
        const c = emulator.canvas;
        if (!c?.width || !c.height) return resolve(null);
        const aspect = emulator.gameManager.getVideoDimensions('aspect') || 4 / 3;
        let w = c.width, hgt = c.height;
        if (w / hgt > aspect) w = Math.round(hgt * aspect); else hgt = Math.round(w / aspect);
        const scale = Math.min(1, 640 / w);
        const out = document.createElement('canvas');
        out.width = Math.round(w * scale);
        out.height = Math.round(hgt * scale);
        const x = out.getContext('2d');
        x.drawImage(c, (c.width - w) / 2, (c.height - hgt) / 2, w, hgt, 0, 0, out.width, out.height);
        const d = x.getImageData(0, 0, out.width, out.height).data;
        let lit = false;
        for (let k = 0; k < d.length && !lit; k += 16) lit = (d[k] | d[k + 1] | d[k + 2]) > 0;
        if (!lit && ++tries < 10) return attempt();
        if (!lit) return resolve(null);
        out.toBlob(b => resolve(b), 'image/jpeg', 0.85);
      } catch { resolve(null); }
    });
    attempt();
  });
}

export function setUpStates(emulator, game, resumeId, show) {
  const message = text => { try { emulator.displayMessage(text, 2500); } catch { /* not there yet */ } };

  const load = async id => {
    const r = await fetch(`/api/games/states/${id}`, { credentials: 'same-origin' });
    if (!r.ok) throw new Error(r.status === 404 ? 'That save state is gone.' : `error ${r.status}`);
    emulator.gameManager.loadState(new Uint8Array(await r.arrayBuffer()));
    message('STATE LOADED');
  };

  // Save State: the state, then its screenshot
  emulator.on('saveState', async ({ screenshot, format, state }) => {
    show('Saving state…');
    try {
      const picture = await grab(emulator) ?? screenshot;
      const saved = await api(`/api/games/${game.id}/states`, { method: 'POST', body: new Blob([state]) });
      if (picture) {
        const type = picture.type || `image/${format === 'jpg' ? 'jpeg' : format || 'png'}`;
        await api(`/api/games/states/${saved.id}/picture`, { method: 'PUT', body: new Blob([picture], { type }) });
      }
      show(`State saved ${new Date().toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}`);
      message('STATE SAVED');
    } catch (e) { show(`State not saved: ${e.message}`, true); }
  });

  // Load State: the states to pick from, over the game (paused meanwhile)
  emulator.on('loadState', async () => {
    const wasPaused = emulator.paused;
    emulator.pause();
    const close = () => { picker.remove(); if (!wasPaused) emulator.play(); };
    const list = h('div', { class: 'state-list' }, h('p', { class: 'muted' }, 'Loading…'));
    const picker = h('div', { class: 'state-picker', onclick: e => { if (e.target === picker) close(); } },
      h('div', { class: 'state-box' },
        h('div', { class: 'row' }, h('h2', {}, 'Load a state'), h('span', { class: 'spacer' }),
          h('button', { class: 'secondary', onclick: close }, 'Close')),
        list));
    emulator.elements.parent.append(picker);
    try {
      const states = await api(`/api/games/${game.id}/states`);
      list.replaceChildren(...(states.length ? states.map(s => {
        const card = h('div', { class: 'state-card' },
          h('button', { class: 'state-pick', title: 'Load this state', onclick: async () => {
            try { await load(s.id); close(); } catch (e) { alert(e.message); }
          } },
            s.picture ? h('img', { src: s.picture, alt: '' }) : h('span', { class: 'state-none' }, 'No picture'),
            h('span', {}, when(s.createdUtc))),
          h('button', { class: 'tile-x', title: 'Delete this state', 'aria-label': 'Delete this state', onclick: async () => {
            if (!confirm('Delete this save state?')) return;
            try { await api(`/api/games/states/${s.id}`, { method: 'DELETE' }); card.remove(); } catch (e) { alert(e.message); }
          } }, '×'));
        return card;
      }) : [h('p', { class: 'muted' }, 'No save states yet: Save State (in the bar under the game) makes one.')]));
    } catch (e) { list.replaceChildren(h('p', { class: 'error' }, e.message)); }
  });

  emulator.on('start', () => {
    api(`/api/games/${game.id}/played`, { method: 'POST' }).catch(() => { });
    // Resume from the game's page: once the game is running
    if (resumeId) setTimeout(() => load(resumeId).catch(e => show(`State not loaded: ${e.message}`, true)), 500);
  });
}
