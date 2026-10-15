// The listening page: one track at a time from the play queue, in the browser's own <audio> player, on to the next
// when it ends. The lock screen and media keys get the track's details and previous/next.

import { h, art, formatTime } from './dom.js';

// What this browser plays besides MP3, AAC and WAV; anything else the server converts to AAC
const probe = document.createElement('audio');
const can = type => probe.canPlayType(type) !== '';
const caps = new URLSearchParams({
  flac: String(can('audio/flac')), ogg: String(can('audio/ogg; codecs="vorbis"')), opus: String(can('audio/ogg; codecs="opus"')),
});

// Where an item of the queue plays: music here, video in the video player
export const pageFor = item => `#/${item.audio ? 'listen' : 'play'}/${item.id}`;

export class Listener {
  // `track`: /api/tracks/{id}; `place`: the queue's place for it (positionOf), or null
  constructor(track, place) {
    this.track = track;
    this.place = place;
    this.audio = h('audio', { controls: true, preload: 'auto', src: `/api/tracks/${track.id}/audio?${caps}` });
    this.status = h('p', { class: 'muted' }, 'Loading…');
    this.audio.addEventListener('canplay', () => { this.status.textContent = ''; }, { once: true });
    this.audio.addEventListener('waiting', () => { if (this.audio.currentTime === 0) this.status.textContent = 'Loading…'; });
    this.audio.addEventListener('playing', () => { this.status.textContent = ''; });
    this.audio.addEventListener('error', () => { this.status.textContent = 'This track couldn\'t be played.'; this.status.className = 'error'; });
    // On to the next one straight away, as music players do
    this.audio.addEventListener('ended', () => this.next());
  }

  element() {
    const t = this.track, p = this.place;
    const prev = h('button', { class: 'secondary', title: 'Previous', onclick: () => this.previous() }, '⏮');
    const next = h('button', { class: 'secondary', title: 'Next', onclick: () => this.next(), disabled: !p?.next }, '⏭');
    return h('div', { class: 'listen' },
      h('div', { class: 'listen-art' }, art(t.album.image)),
      h('div', { class: 'listen-main' },
        h('h1', {}, t.title),
        h('p', {}, h('a', { href: `#/artist/${t.albumArtist.id}` }, t.artist), ' · ',
          h('a', { href: `#/album/${t.album.id}` }, t.album.title), t.album.year ? ` (${t.album.year})` : ''),
        this.audio,
        h('div', { class: 'row' }, prev, next, p ? h('span', { class: 'muted' }, `${p.label} · ${p.index + 1} of ${p.count}`) : null),
        this.status,
        p?.next ? h('p', { class: 'muted' }, 'Next: ', h('a', { href: pageFor(p.next) }, p.next.title)) : null));
  }

  async start() {
    this.mediaSession();
    try { await this.audio.play(); }
    catch { this.status.textContent = 'Press play to start.'; }   // no autoplay without a click first
  }

  previous() {
    // Back to the start of this one first, as music players do
    if (this.audio.currentTime > 3 || !this.place?.previous) this.audio.currentTime = 0;
    else location.hash = pageFor(this.place.previous);
  }

  next() { if (this.place?.next) location.hash = pageFor(this.place.next); }

  mediaSession() {
    if (!('mediaSession' in navigator)) return;
    const t = this.track;
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: t.title, artist: t.artist, album: t.album.title,
        artwork: [{ src: new URL(t.album.image, location.href).href }],
      });
    } catch { /* MediaMetadata missing */ }
    const set = (action, fn) => { try { navigator.mediaSession.setActionHandler(action, fn); } catch { } };
    set('play', () => this.audio.play());
    set('pause', () => this.audio.pause());
    set('previoustrack', () => this.previous());
    set('nexttrack', this.place?.next ? () => this.next() : null);
    set('seekto', d => { if (d.seekTime != null) this.audio.currentTime = d.seekTime; });
  }

  destroy() {
    this.audio.pause();
    this.audio.removeAttribute('src');
    this.audio.load();
    if ('mediaSession' in navigator) {
      for (const a of ['play', 'pause', 'previoustrack', 'nexttrack', 'seekto'])
        try { navigator.mediaSession.setActionHandler(a, null); } catch { }
    }
  }
}

// A track list for an album page: number, title (and its own artist on a compilation), length
export function trackRows(album, onPick) {
  let disc = null;
  return album.tracks.map(t => {
    const head = t.disc != null && t.disc !== disc ? (disc = t.disc, h('div', { class: 'disc muted' }, `Disc ${t.disc}`)) : null;
    return [head, h('a', { class: 'track' + (t.playable ? '' : ' hidden'), href: `#/listen/${t.id}`,
        onclick: e => { e.preventDefault(); if (t.playable) onPick(t); } },
      h('span', { class: 'num muted' }, t.track ?? ''),
      h('span', { class: 'title' }, t.title, t.artist ? h('span', { class: 'muted' }, ` · ${t.artist}`) : null),
      h('span', { class: 'muted' }, t.playable ? formatTime(t.duration) : 'can\'t be read'))];
  });
}
