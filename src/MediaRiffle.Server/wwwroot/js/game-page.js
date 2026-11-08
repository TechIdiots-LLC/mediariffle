// The game page (game.html#<id>): EmulatorJS, as added by an admin under Admin → Games, playing one game. It's a page
// of its own because the emulator needs a Content Security Policy (WebAssembly) and cross-origin isolation (threads)
// that the rest of the site doesn't have. Controllers work through the browser's Gamepad API: press a button once.

import { api } from './api.js';
import { setUpControls } from './game-controls.js';

const id = Number(location.hash.slice(1));
const back = document.getElementById('back');
const message = document.getElementById('message');

// Back to the game's page: the way we came when that was this site, else straight there
back.href = `/#/game/${id}`;
back.addEventListener('click', e => {
  if (document.referrer.startsWith(location.origin) && history.length > 1) { e.preventDefault(); history.back(); }
});

function show(text) {
  message.textContent = text;
  message.hidden = false;
}

async function start() {
  if (!id) { show('No game given.'); return; }
  let game;
  try { game = await api(`/api/games/${id}`); }
  catch (e) {
    if (e.status === 401) { location.href = '/#/login'; return; }
    show(e.status === 404 ? 'That game isn\'t in the library.' : e.message);
    return;
  }
  document.title = `${game.title} · MediaRiffle`;
  document.getElementById('title').textContent = game.title;
  if (game.notReady) { show(game.notReady); return; }

  // EmulatorJS reads its settings from these globals when its loader runs
  Object.assign(window, {
    EJS_player: '#game',
    EJS_core: game.system.core,
    EJS_gameUrl: game.fileUrl,
    // Its saves in this browser are kept under the game's name
    EJS_gameName: game.title,
    EJS_pathtodata: '/emulator/',
    EJS_language: 'en-US',
    EJS_color: '#0a66c2',
    EJS_backgroundColor: '#000000',
    // Threads (PSP, DOS) only where the page is cross-origin isolated, which game.html is
    EJS_threads: typeof SharedArrayBuffer === 'function' && window.crossOriginIsolated === true,
    // EmulatorJS 4.2.3 makes the disc menu of a game with several discs (a multi-disc .pbp or .m3u) before its
    // settings, and marking the first disc as chosen there fails (no settings yet), so the game doesn't start. That
    // first call is let go: the first disc is in already, and swapping discs as the console boots leaves it black.
    // Discs picked from the menu later go through as before.
    EJS_ready: () => {
      const emulator = window.EJS_emulator;
      if (!emulator) return;
      const changed = emulator.menuOptionChanged.bind(emulator);
      emulator.menuOptionChanged = (option, value) => {
        if (!emulator.allSettings) return;
        changed(option, value);
      };
      setUpControls(emulator, game);
    },
  });
  if (game.biosUrl) window.EJS_biosUrl = game.biosUrl;
  if (game.image) window.EJS_backgroundImage = game.image;

  const loader = document.createElement('script');
  loader.src = '/emulator/loader.js';
  loader.onerror = () => show('The emulator couldn\'t be loaded: an admin can add it again under Admin → Games.');
  document.body.append(loader);
}

start();
