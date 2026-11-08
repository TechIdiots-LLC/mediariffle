// The game's in-game save (a cartridge's battery save, a memory card), kept on the server as well as in this browser
// (where EmulatorJS keeps it), so it follows the user from device to device.
//
// When the game starts, the two are compared by hash. The same: nothing to do. The browser's changed here since the two
// were last in step (this page's note says so): it goes to the server. Otherwise the server's changed elsewhere (or
// the browser's is someone else's), and the game gets the server's. The server has none yet: the browser's goes up,
// so saves from before the server kept them carry on. While playing, the emulator writes its save every 30 seconds
// and when the page is left, and a changed one goes to the server.

import { api } from './api.js';

// FNV-1a, 32-bit, as hex: as the server hashes (crypto.subtle isn't there on plain HTTP)
function hash(bytes) {
  let h = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i++) h = Math.imul(h ^ bytes[i], 0x01000193) >>> 0;
  return h.toString(16).padStart(8, '0');
}

export function setUpSaves(emulator, game, leaving) {
  const status = document.getElementById('save-status');
  const noteKey = `mr-save-${game.saveKey}`;
  const note = {
    get: () => { try { return localStorage.getItem(noteKey); } catch { return null; } },
    set: v => { try { localStorage.setItem(noteKey, v); } catch { /* private mode: compared by hash only */ } },
  };
  let known = game.save?.hash ?? null;   // the hash of the save on the server
  let busy = Promise.resolve();
  let timer = null;

  const show = (text, error = false) => {
    status.textContent = text;
    status.classList.toggle('error', error);
    status.hidden = !text;
  };

  const manager = () => emulator.gameManager;
  const savePath = () => manager().getSaveFilePath();
  const local = () => {
    const path = savePath();
    return path && manager().FS.analyzePath(path).exists ? manager().FS.readFile(path) : null;
  };

  const upload = data => {
    if (hash(data) === known) return busy;
    busy = busy.then(async () => {
      const h = hash(data);
      if (h === known) return;
      try {
        const saved = await api(`/api/games/${game.id}/save`, { method: 'PUT', body: new Blob([data]) });
        known = saved.hash;
        note.set(saved.hash);
        show(`Saved ${new Date().toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}`);
      } catch (e) { show(`Save not kept on the server: ${e.message}`, true); }
    });
    return busy;
  };

  // The server's save, in place of the browser's: where the emulator keeps it, then read again by the game
  const take = data => {
    const FS = manager().FS, path = savePath();
    let dir = '';
    for (const part of path.split('/').slice(0, -1)) {
      if (!part) continue;
      dir += '/' + part;
      if (!FS.analyzePath(dir).exists) FS.mkdir(dir);
    }
    if (FS.analyzePath(path).exists) FS.unlink(path);
    FS.writeFile(path, data);
    manager().loadSaveFiles();
  };

  emulator.on('start', async () => {
    let mine = null;
    try { mine = local(); } catch { /* no save file for this core */ }
    const server = game.save;
    try {
      if (server) {
        if (mine && hash(mine) === server.hash) note.set(server.hash);
        else if (mine && note.get() === server.hash) await upload(mine);
        else {
          const r = await fetch(`/api/games/${game.id}/save`, { credentials: 'same-origin', cache: 'no-store' });
          if (!r.ok) throw new Error(`error ${r.status}`);
          take(new Uint8Array(await r.arrayBuffer()));
          note.set(server.hash);
          show('Save loaded');
        }
      } else if (mine) await upload(mine);
    } catch (e) { show(`Save not loaded from the server: ${e.message}`, true); }
    timer = setInterval(() => { try { manager().saveSaveFiles(); } catch { /* not running */ } }, 30000);
  });

  // EmulatorJS writing its save (our timer, its own, leaving the page)
  emulator.on('saveSaveFiles', data => { if (data && data.length) upload(data); });

  // The page hidden (another app, the tab closed): write the save now
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden' && emulator.started) try { manager().saveSaveFiles(); } catch { /* not running */ }
  });

  // Back: the latest save to the server first
  leaving.push(async () => {
    clearInterval(timer);
    if (!emulator.started) return;
    try { manager().saveSaveFiles(); } catch { /* not running */ }
    await busy;
  });
}
