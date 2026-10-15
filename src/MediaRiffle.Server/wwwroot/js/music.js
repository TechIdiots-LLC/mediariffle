// Music: one <audio> for the whole app, playing the queue in a bar at the bottom of every page, so it carries on
// while browsing. The Now playing page (#/now) shows the same player bigger, with the queue. The lock screen and
// media keys get the track's details and previous/next. On a Chromecast, the TV plays the queue itself and the bar
// follows it and controls it.

import { api } from './api.js';
import { h, mount, art, formatTime } from './dom.js';
import { getQueue, saveQueue, clearQueue, onAudioQueue, pageFor } from './queue.js';
import { castAvailable, onCastChange, isCasting, castSession, remotePlayer, castTracks, castTrackId, castJumpTo, castStep } from './cast.js';

// What this browser plays besides MP3, AAC and WAV; anything else the server converts to AAC
const probe = document.createElement('audio');
const can = type => probe.canPlayType(type) !== '';
const caps = new URLSearchParams({
  flac: String(can('audio/flac')), ogg: String(can('audio/ogg; codecs="vorbis"')), opus: String(can('audio/ogg; codecs="opus"')),
});

// How far the Chromecast's own queue reaches around the playing track; past it, the page hands it the next stretch
const CAST_BEHIND = 20, CAST_AHEAD = 100;

class MusicPlayer {
  constructor() {
    this.audio = new Audio();
    this.audio.preload = 'auto';
    this.track = null;        // /api/tracks/{id} of what's loaded
    this.error = '';
    this.listeners = new Set();
    this.lastSave = 0;
    this.castReady = false;   // the Cast button can show (Chrome or Edge, on HTTPS)
    this.casting = false;     // the music is on a Chromecast; this page follows and controls it
    this.remoteTime = 0;      // where the Chromecast is, to carry on from here when casting stops
    this.audio.addEventListener('ended', () => this.next());
    this.audio.addEventListener('play', () => this.emit('state'));
    this.audio.addEventListener('pause', () => this.emit('state'));
    this.audio.addEventListener('playing', () => { this.error = ''; this.emit('state'); });
    this.audio.addEventListener('error', () => {
      if (!this.audio.getAttribute('src') || this.casting) return;
      this.error = 'This track couldn\'t be played.';
      this.emit('state');
    });
    this.audio.addEventListener('timeupdate', () => {
      if (this.casting) return;
      this.emit('time');
      // Where we are, so a reload comes back to it
      if (Date.now() - this.lastSave > 5000) { this.lastSave = Date.now(); this.savePosition(); }
    });
  }

  /** Called with 'track', 'state', 'time' or 'cast' (the Cast button can show); returns the function that stops it. */
  on(fn) { this.listeners.add(fn); return () => this.listeners.delete(fn); }
  emit(what) { for (const fn of this.listeners) fn(what); }

  get playing() { return this.casting ? !remotePlayer().player.isPaused : !this.audio.paused; }
  get currentTime() { return this.casting ? remotePlayer().player.currentTime : this.audio.currentTime; }
  get duration() {
    const d = this.casting ? remotePlayer().player.duration : this.audio.duration;
    return Number.isFinite(d) && d > 0 ? d : (this.track?.duration ?? 0);
  }
  get queue() { return getQueue(); }
  /** The Chromecast's name while casting, else null. */
  get castDevice() {
    try { return this.casting ? castSession()?.getCastDevice()?.friendlyName ?? 'the TV' : null; } catch { return null; }
  }

  /** Loads Google's Cast sender (once) and follows the Chromecast from then on. */
  enableCast() {
    if (this.castStarted) return;
    this.castStarted = true;
    castAvailable().then(ok => {
      if (!ok) return;
      this.castReady = true;
      const { player, controller } = remotePlayer();
      const E = cast.framework.RemotePlayerEventType;
      controller.addEventListener(E.CURRENT_TIME_CHANGED, () => {
        if (!this.casting) return;
        this.remoteTime = player.currentTime;
        this.emit('time');
      });
      controller.addEventListener(E.IS_PAUSED_CHANGED, () => { if (this.casting) this.emit('state'); });
      controller.addEventListener(E.MEDIA_INFO_CHANGED, () => this.remoteTrackChanged());
      controller.addEventListener(E.PLAYER_STATE_CHANGED, () => this.remoteStateChanged());
      onCastChange(on => this.castChanged(on));
      this.emit('cast');
      if (isCasting()) this.castChanged(true);
    });
  }

  /** Plays the queue's item `index` (here, or on the Chromecast while casting); a video goes to the video player. */
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
    // Already casting (a session from before, or started from a video): the music goes to the TV too
    if (this.castReady && isCasting()) this.casting = true;
    if (this.casting) {
      this.audio.pause();
      this.emit('track');
      await this.castFrom(index, at);
      return;
    }
    this.audio.src = `/api/tracks/${item.id}/audio?${caps}`;
    if (at > 0) this.audio.addEventListener('loadedmetadata', () => { this.audio.currentTime = at; }, { once: true });
    this.mediaSession();
    this.emit('track');
    if (autoplay) {
      // Without a click first the browser may refuse; the play button is there
      try { await this.audio.play(); } catch { this.emit('state'); }
    }
  }

  /** Jumps to the queue's item `index` (Now playing): within what the Chromecast has when it can. */
  jump(index) {
    const item = getQueue()?.items[index];
    if (this.casting && item?.audio && castJumpTo(item.id)) return;
    this.playIndex(index);
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

  toggle() {
    if (this.casting) remotePlayer().controller.playOrPause();
    else if (this.audio.paused) this.audio.play().catch(() => { });
    else this.audio.pause();
  }
  pause() { if (this.playing) this.toggle(); }
  seek(seconds) {
    if (!Number.isFinite(seconds)) return;
    if (this.casting) { const r = remotePlayer(); r.player.currentTime = seconds; r.controller.seek(); }
    else this.audio.currentTime = seconds;
  }

  next() {
    const q = getQueue();
    if (this.casting && castStep(true)) return;
    if (q && q.index + 1 < q.items.length) this.playIndex(q.index + 1);
    else if (!this.casting) { this.audio.currentTime = 0; this.audio.pause(); }   // the end of the queue
  }

  previous() {
    const q = getQueue();
    // Back to the start of this one first, as music players do
    if (this.currentTime > 3 || !q || q.index === 0) this.seek(0);
    else if (!(this.casting && castStep(false))) this.playIndex(q.index - 1);
  }

  /** Stops and hides the player (a video is starting, or ×); `forget` also drops the queue. */
  close(forget = false) {
    if (!this.track) return;
    this.savePosition();
    if (this.casting) {
      // The Chromecast stops the music (a video starting replaces it anyway); the Cast session stays
      try { remotePlayer().controller.stop(); } catch { }
      this.casting = false;
    }
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
    q.position = this.currentTime;
    saveQueue(q);
  }

  /** After a reload: the queue's music track, paused where it was. */
  restore() {
    const q = getQueue();
    if (q?.items[q.index]?.audio) this.playIndex(q.index, { autoplay: false, at: q.position ?? 0 });
  }

  // ── Casting ──

  /**
   * Hands the Chromecast the queue around `index` (some before, many after), playing `index` from `at`. The server
   * gives each track links the Chromecast can fetch without a sign-in.
   */
  async castFrom(index, at = 0) {
    const q = getQueue();
    const want = q?.items[index];
    if (!want) return;
    const ids = q.items.slice(Math.max(0, index - CAST_BEHIND), index + CAST_AHEAD).filter(x => x.audio).map(x => x.id);
    try {
      const tracks = await api('/api/cast/tracks', { method: 'POST', body: { ids } });
      const start = tracks.findIndex(t => t.id === want.id);
      if (start < 0) throw new Error('this track can\'t be played');
      await castTracks(tracks, start, at);
      this.remoteTime = at;
    } catch (e) {
      this.error = `Couldn't play on ${this.castDevice ?? 'the TV'}: ${e?.message ?? e?.description ?? e?.code ?? e}`;
      this.emit('state');
    }
  }

  // A Cast session started or ended. Starting: the music moves to the TV from where it is (or, coming back to a session
  // already playing this queue, the page just follows it). Ending: it comes back here, paused where the TV was.
  async castChanged(on) {
    if (on && !this.casting) {
      if (!this.track) return;   // nothing playing here (a video page casts its own)
      this.casting = true;
      const at = this.audio.currentTime;
      this.audio.pause();
      const q = getQueue();
      const remoteId = castTrackId();
      if (remoteId && q?.items.some(x => x.id === remoteId)) { this.remoteTrackChanged(); this.emit('track'); return; }
      this.emit('track');
      await this.castFrom(q.index, at);
    } else if (!on && this.casting) {
      this.casting = false;
      const q = getQueue();
      if (this.track && q) await this.playIndex(q.index, { autoplay: false, at: this.remoteTime });
      else this.emit('track');
    }
  }

  // The Chromecast moved on to another track of its queue: the bar and Now playing follow
  async remoteTrackChanged() {
    if (!this.casting) return;
    const id = castTrackId();
    if (!id || id === this.track?.id) return;
    const q = getQueue();
    const i = q?.items.findIndex(x => x.id === id) ?? -1;
    if (i < 0) return;
    q.index = i;
    q.position = 0;
    saveQueue(q);
    this.remoteTime = 0;
    try { this.track = await api(`/api/tracks/${id}`); } catch { /* keep the old details */ }
    this.emit('track');
  }

  // The Chromecast finished the last track it was given: hand it the next stretch of the queue, if there is one
  remoteStateChanged() {
    if (!this.casting) return;
    this.emit('state');
    const media = castSession()?.getMediaSession();
    if (remotePlayer().player.playerState !== chrome.cast.media.PlayerState.IDLE || media?.idleReason !== chrome.cast.media.IdleReason.FINISHED) return;
    const q = getQueue();
    if (q && this.track?.id === q.items[q.index]?.id && q.index + 1 < q.items.length) this.castFrom(q.index + 1);
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
    set('play', () => { if (!this.playing) this.toggle(); });
    set('pause', () => this.pause());
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
    const d = music.duration, at = music.currentTime;
    playBtn.textContent = music.playing ? '⏸' : '▶';
    const q = music.queue;
    next.disabled = !q || q.index + 1 >= q.items.length;
    if (dragging) return;
    seek.max = d || 1;
    seek.value = at;
    now.textContent = formatTime(at);
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
  music.enableCast();
  const c = controls();
  // A problem shows in place of the artist; casting, the artist line says where it's playing
  const artist = h('div', { class: 'np-artist muted' }, music.casting ? `${t.artist} · on ${music.castDevice}` : t.artist);
  const err = h('div', { class: 'error np-error' }, music.error);
  const showError = () => { err.textContent = music.error; err.hidden = !music.error; artist.hidden = !!music.error; };
  showError();
  mount(bar,
    h('a', { class: 'np-cover', href: '#/now', title: 'Now playing' }, art(t.album.image, 0, 'music')),
    h('a', { class: 'np-info', href: '#/now', title: 'Now playing' }, h('div', { class: 'np-title' }, t.title), artist, err),
    c.buttons, c.seekbar,
    // Google's Cast button: picks a Chromecast, or stops casting
    music.castReady ? h('span', { class: 'cast-btn np-cast' }, document.createElement('google-cast-launcher')) : h('span'),
    h('button', { class: 'chip-x np-close', title: 'Stop and close', onclick: () => music.close(true) }, '×'));
  barUpdate = () => { c.update(); showError(); };
}
let barUpdate = () => { };
music.on(what => { if (what === 'track' || what === 'cast') renderBar(); else barUpdate(); });

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
          c.seekbar, c.buttons,
          music.casting ? h('p', { class: 'muted' }, `Playing on ${music.castDevice}. Stop casting with the Cast button in the bar.`) : null,
          status,
          music.error ? null : extras(t),
          q ? [h('h2', {}, q.label, h('span', { class: 'muted' }, ` · ${q.index + 1} of ${q.items.length}`)),
            h('ol', { class: 'np-queue' }, q.items.map((x, i) => h('li', { class: i === q.index ? 'current' : '' },
              h('a', { href: pageFor(x), onclick: e => { e.preventDefault(); music.jump(i); } }, x.title))))] : null)));
    update = () => { c.update(); status.textContent = music.error; };
    view.querySelector('.np-queue .current')?.scrollIntoView({ block: 'nearest' });
  };
  let update = () => { };
  draw();
  return music.on(what => { if (what === 'track' || what === 'cast') draw(); else update(); });
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
