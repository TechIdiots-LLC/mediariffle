// Music: one <audio> for the whole app, playing the queue in a bar at the bottom of every page, so it carries on
// while browsing. The Now playing page (#/now) shows the same player bigger, with the queue. The lock screen and
// media keys get the track's details and previous/next.

import { api } from './api.js';
import { h, mount, art, formatTime } from './dom.js';
import { getQueue, saveQueue, clearQueue, onAudioQueue, pageFor } from './queue.js';

// What this browser plays besides MP3, AAC and WAV; anything else the server converts to AAC
const probe = document.createElement('audio');
const can = type => probe.canPlayType(type) !== '';
const caps = new URLSearchParams({
  flac: String(can('audio/flac')), ogg: String(can('audio/ogg; codecs="vorbis"')), opus: String(can('audio/ogg; codecs="opus"')),
});

class MusicPlayer {
  constructor() {
    this.audio = new Audio();
    this.audio.preload = 'auto';
    this.track = null;        // /api/tracks/{id} of what's loaded
    this.error = '';
    this.listeners = new Set();
    this.lastSave = 0;
    this.audio.addEventListener('ended', () => this.next());
    this.audio.addEventListener('play', () => this.emit('state'));
    this.audio.addEventListener('pause', () => this.emit('state'));
    this.audio.addEventListener('playing', () => { this.error = ''; this.emit('state'); });
    this.audio.addEventListener('error', () => {
      if (!this.audio.getAttribute('src')) return;
      this.error = 'This track couldn\'t be played.';
      this.emit('state');
    });
    this.audio.addEventListener('timeupdate', () => {
      this.emit('time');
      // Where we are, so a reload comes back to it
      if (Date.now() - this.lastSave > 5000) { this.lastSave = Date.now(); this.savePosition(); }
    });
  }

  /** Called with 'track', 'state' or 'time'; returns the function that stops it. */
  on(fn) { this.listeners.add(fn); return () => this.listeners.delete(fn); }
  emit(what) { for (const fn of this.listeners) fn(what); }

  get playing() { return !this.audio.paused; }
  get queue() { return getQueue(); }

  /** Plays the queue's item `index`: music here, a video in the video player (a mixed category's queue). */
  async playIndex(index, { autoplay = true, at = 0 } = {}) {
    const q = getQueue();
    const item = q?.items[index];
    if (!item) return;
    if (!item.audio) { this.close(); location.hash = pageFor(item); return; }
    q.index = index;
    q.position = at;
    saveQueue(q);
    this.error = '';
    try { this.track = await api(`/api/tracks/${item.id}`); }
    catch (e) { this.track = { id: item.id, title: item.title, artist: '', album: { title: '', image: '' } }; this.error = e.message; }
    this.audio.src = `/api/tracks/${item.id}/audio?${caps}`;
    if (at > 0) this.audio.addEventListener('loadedmetadata', () => { this.audio.currentTime = at; }, { once: true });
    this.mediaSession();
    this.emit('track');
    if (autoplay) {
      // Without a click first the browser may refuse; the play button is there
      try { await this.audio.play(); } catch { this.emit('state'); }
    }
  }

  /** Plays this track: from the queue when it's in it, else on its own. */
  async playFile(fileId) {
    const q = getQueue();
    const i = q?.items.findIndex(x => x.id === fileId) ?? -1;
    if (i >= 0) return this.playIndex(i);
    const t = await api(`/api/tracks/${fileId}`);
    saveQueue({ label: t.album.title, items: [{ id: t.id, title: `${t.artist} · ${t.title}`, audio: true }], index: 0 });
    return this.playIndex(0);
  }

  toggle() { if (this.audio.paused) this.audio.play().catch(() => { }); else this.audio.pause(); }
  pause() { this.audio.pause(); }
  seek(seconds) { if (Number.isFinite(seconds)) this.audio.currentTime = seconds; }

  next() {
    const q = getQueue();
    if (q && q.index + 1 < q.items.length) this.playIndex(q.index + 1);
    else { this.audio.currentTime = 0; this.audio.pause(); }   // the end of the queue
  }

  previous() {
    const q = getQueue();
    // Back to the start of this one first, as music players do
    if (this.audio.currentTime > 3 || !q || q.index === 0) this.audio.currentTime = 0;
    else this.playIndex(q.index - 1);
  }

  /** Stops and hides the player (a video is starting); `forget` also drops the queue. */
  close(forget = false) {
    if (!this.track) return;
    this.savePosition();
    this.audio.pause();
    this.audio.removeAttribute('src');
    this.audio.load();
    this.track = null;
    if (forget) clearQueue();
    if ('mediaSession' in navigator) {
      navigator.mediaSession.metadata = null;
      for (const a of ['play', 'pause', 'previoustrack', 'nexttrack', 'seekto'])
        try { navigator.mediaSession.setActionHandler(a, null); } catch { }
    }
    this.emit('track');
  }

  savePosition() {
    const q = getQueue();
    if (!q || !this.track || q.items[q.index]?.id !== this.track.id) return;
    q.position = this.audio.currentTime;
    saveQueue(q);
  }

  /** After a reload: the queue's music track, paused where it was. */
  restore() {
    const q = getQueue();
    if (q?.items[q.index]?.audio) this.playIndex(q.index, { autoplay: false, at: q.position ?? 0 });
  }

  mediaSession() {
    if (!('mediaSession' in navigator)) return;
    const t = this.track;
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: t.title, artist: t.artist, album: t.album.title,
        artwork: t.album.image ? [{ src: new URL(t.album.image, location.href).href }] : [],
      });
    } catch { /* MediaMetadata missing */ }
    const set = (action, fn) => { try { navigator.mediaSession.setActionHandler(action, fn); } catch { } };
    set('play', () => this.audio.play());
    set('pause', () => this.audio.pause());
    set('previoustrack', () => this.previous());
    set('nexttrack', () => this.next());
    set('seekto', d => { if (d.seekTime != null) this.seek(d.seekTime); });
  }
}

export const music = new MusicPlayer();
onAudioQueue(i => music.playIndex(i));

// Play/pause, previous/next and a seek bar, kept up to date; used by the bar and the Now playing page
function controls() {
  const playBtn = h('button', { class: 'np-play', title: 'Play / pause', onclick: () => music.toggle() });
  const prev = h('button', { class: 'secondary', title: 'Previous', onclick: () => music.previous() }, '⏮');
  const next = h('button', { class: 'secondary', title: 'Next', onclick: () => music.next() }, '⏭');
  const now = h('span', { class: 'np-time muted' }), total = h('span', { class: 'np-time muted' });
  let dragging = false;
  const seek = h('input', { type: 'range', class: 'np-seek', min: 0, max: 1, step: 'any', value: 0,
    oninput: () => { dragging = true; now.textContent = formatTime(+seek.value); },
    onchange: () => { dragging = false; music.seek(+seek.value); } });
  const update = () => {
    const a = music.audio, d = Number.isFinite(a.duration) ? a.duration : (music.track?.duration ?? 0);
    playBtn.textContent = music.playing ? '⏸' : '▶';
    const q = music.queue;
    next.disabled = !q || q.index + 1 >= q.items.length;
    if (dragging) return;
    seek.max = d || 1;
    seek.value = a.currentTime;
    now.textContent = formatTime(a.currentTime);
    total.textContent = formatTime(d);
  };
  update();
  return {
    buttons: h('span', { class: 'row np-buttons' }, prev, playBtn, next),
    seekbar: h('span', { class: 'np-seekbar' }, now, seek, total),
    update,
  };
}

// The bar at the bottom of every page while music is loaded
const bar = document.getElementById('nowbar');
function renderBar() {
  const t = music.track;
  document.body.classList.toggle('has-nowbar', !!t);
  if (!t) { bar.hidden = true; mount(bar); return; }
  bar.hidden = false;
  const c = controls();
  // A problem shows in place of the artist
  const artist = h('div', { class: 'np-artist muted' }, t.artist);
  const err = h('div', { class: 'error np-error' }, music.error);
  const showError = () => { err.textContent = music.error; err.hidden = !music.error; artist.hidden = !!music.error; };
  showError();
  mount(bar,
    h('a', { class: 'np-cover', href: '#/now', title: 'Now playing' }, art(t.album.image, 0, 'music')),
    h('a', { class: 'np-info', href: '#/now', title: 'Now playing' }, h('div', { class: 'np-title' }, t.title), artist, err),
    c.buttons, c.seekbar,
    h('button', { class: 'chip-x np-close', title: 'Stop and close', onclick: () => music.close(true) }, '×'));
  barUpdate = () => { c.update(); showError(); };
}
let barUpdate = () => { };
music.on(what => { if (what === 'track') renderBar(); else barUpdate(); });

/**
 * The Now playing page: the player bigger, and the queue to jump around in; `extras(track)` adds to it (the track's
 * lists). Returns its clean-up.
 */
export function nowPlayingView(view, extras = () => null) {
  const draw = () => {
    const t = music.track, q = music.queue;
    if (!t) {
      mount(view, h('h1', {}, 'Now playing'), h('p', { class: 'muted' }, 'Nothing is playing. Pick an album or press Play all.'));
      update = () => { };
      return;
    }
    const c = controls();
    const status = h('p', { class: 'error' }, music.error);
    mount(view,
      h('div', { class: 'listen' },
        h('div', { class: 'listen-art' }, art(t.album.image, 0, 'music')),
        h('div', { class: 'listen-main' },
          h('h1', {}, t.title),
          h('p', {}, t.albumArtist ? h('a', { href: `#/artist/${t.albumArtist.id}` }, t.artist) : t.artist,
            t.album.id ? [' · ', h('a', { href: `#/album/${t.album.id}` }, t.album.title), t.album.year ? ` (${t.album.year})` : ''] : null),
          c.seekbar, c.buttons, status,
          music.error ? null : extras(t),
          q ? [h('h2', {}, q.label, h('span', { class: 'muted' }, ` · ${q.index + 1} of ${q.items.length}`)),
            h('ol', { class: 'np-queue' }, q.items.map((x, i) => h('li', { class: i === q.index ? 'current' : '' },
              h('a', { href: pageFor(x), onclick: e => { e.preventDefault(); music.playIndex(i); } }, x.title))))] : null)));
    update = () => { c.update(); status.textContent = music.error; };
    view.querySelector('.np-queue .current')?.scrollIntoView({ block: 'nearest' });
  };
  let update = () => { };
  draw();
  return music.on(what => { if (what === 'track') draw(); else update(); });
}

// A track list for an album page: number, title (and its own artist on a compilation), length. The one playing is
// marked; returns the list and its clean-up.
export function trackRows(album, onPick, extra = () => null) {
  let disc = null;
  const rows = album.tracks.map(t => {
    const head = t.disc != null && t.disc !== disc ? (disc = t.disc, h('div', { class: 'disc muted' }, `Disc ${t.disc}`)) : null;
    // Hidden on its own, or with its album or artist (`off`)
    const off = !t.playable || t.hidden || t.off;
    const row = h('div', { class: 'track' + (off ? ' hidden' : ''), 'data-id': t.id },
      h('a', { class: 'track-main', href: `#/listen/${t.id}`, onclick: e => { e.preventDefault(); if (!off) onPick(t); } },
        h('span', { class: 'num muted' }, t.track ?? ''),
        h('span', { class: 'title' }, t.title, t.artist ? h('span', { class: 'muted' }, ` · ${t.artist}`) : null),
        h('span', { class: 'muted' }, !t.playable ? 'can\'t be read' : t.hidden || t.off ? 'Hidden' : formatTime(t.duration))),
      extra(t));
    return [head, row];
  });
  const box = h('div', { class: 'tracks' }, rows);
  const mark = () => {
    for (const r of box.querySelectorAll('.track')) r.classList.toggle('playing', +r.dataset.id === music.track?.id);
  };
  mark();
  return { element: box, cleanup: music.on(what => { if (what === 'track') mark(); }) };
}
