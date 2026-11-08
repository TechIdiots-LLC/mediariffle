// The game page's controller mapping: the user's choice for the game's system, kept on the server (so it follows them
// to every game of the system, on every device) and put in place when the game starts, over what EmulatorJS kept in
// this browser for this one game.
//
// EmulatorJS's buttons are RetroArch's: 0 B, 1 Y, 8 A, 9 X, laid out as on a SNES pad (B bottom, A right, Y left,
// X top). The browser numbers a controller's face buttons by place: BUTTON_1 bottom, BUTTON_2 right, BUTTON_3 left,
// BUTTON_4 top (A, B, X, Y on an Xbox pad).

import { api } from './api.js';

const FACE = {
  // EmulatorJS's own: the letters match an Xbox pad's (its A presses A, though A is the bottom button there and the
  // right one on a Nintendo pad)
  letter: { 0: 'BUTTON_2', 8: 'BUTTON_1', 1: 'BUTTON_4', 9: 'BUTTON_3' },
  // The places match: the bottom button presses the console's bottom button (B on a Nintendo pad, Cross on a
  // PlayStation one)
  position: { 0: 'BUTTON_1', 8: 'BUTTON_2', 1: 'BUTTON_3', 9: 'BUTTON_4' },
};

const LABELS = {
  letter: 'By letter (A is A)',
  position: 'By place (bottom is bottom)',
  custom: 'Your own mapping',
};

const clone = o => JSON.parse(JSON.stringify(o));

function controlsFor(emulator, setting) {
  if (setting.layout === 'custom') return clone(setting.mapping);
  const controls = clone(emulator.defaultControllers);
  for (const player of Object.values(controls))
    for (const [button, pad] of Object.entries(FACE[setting.layout]))
      if (player[button]) player[button].value2 = pad;
  return controls;
}

// Wires the Buttons choice and the offer to keep a remapping into the page, once the emulator is made
export function setUpControls(emulator, game) {
  const box = document.getElementById('layout-box');
  const select = document.getElementById('layout');
  const note = document.getElementById('controls-note');
  let setting = game.controls;   // null: EmulatorJS's own, which is "letter"
  let applied = null;            // the controls as last put in place or kept, to tell a remapping
  let applying = false;

  const fill = () => {
    const current = setting?.layout ?? 'letter';
    select.replaceChildren(...['letter', 'position', 'custom']
      .filter(l => l !== 'custom' || current === 'custom')
      .map(l => new Option(LABELS[l], l, false, l === current)));
  };

  const apply = () => {
    if (!emulator.started) return;
    if (setting) {
      applying = true;
      try {
        emulator.controls = controlsFor(emulator, setting);
        emulator.setupKeys();
        if (typeof emulator.checkGamepadInputs === 'function') emulator.checkGamepadInputs();
        emulator.saveSettings();
      } finally { applying = false; }
    }
    applied = JSON.stringify(emulator.controls);
  };

  const keep = async next => {
    try {
      setting = await api(`/api/games/controls/${encodeURIComponent(game.system.id)}`, { method: 'PUT', body: next });
      fill();
      apply();
    } catch (e) { alert(e.message); fill(); }
  };

  select.addEventListener('change', () => keep({ layout: select.value }));

  // Changed in the emulator's Controls menu (which saves its settings): offered for all the system's games
  const offer = () => {
    const mapping = clone(emulator.controls);
    const yes = document.createElement('button');
    yes.textContent = `Use for all ${game.system.name} games`;
    yes.onclick = () => { note.hidden = true; keep({ layout: 'custom', mapping }); };
    const no = document.createElement('button');
    no.className = 'secondary';
    no.textContent = 'Only here';
    no.onclick = () => { note.hidden = true; };
    note.replaceChildren('Buttons changed.', yes, no);
    note.hidden = false;
  };
  const saveSettings = emulator.saveSettings.bind(emulator);
  emulator.saveSettings = () => {
    saveSettings();
    if (applying || applied === null) return;
    const now = JSON.stringify(emulator.controls);
    if (now === applied) return;
    applied = now;
    offer();
  };

  fill();
  box.hidden = false;
  emulator.on('start', apply);
}
